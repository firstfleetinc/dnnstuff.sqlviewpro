using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Google.Apis.Sheets.v4;
using Google.Apis.Sheets.v4.Data;
using DNNStuff.SQLViewPro.Services;

namespace DNNStuff.SQLViewPro.Services.GoogleSheets
{
	/// <summary>
	/// Thin wrapper around the Google Drive v3 / Sheets v4 APIs used by the
	/// "Google Sheets Template" report type: locate a template in Drive, clone it so the
	/// original is never mutated, write report data into it (letting formulas/pivot tables
	/// recalculate live), export the result as .xlsx, and clean up the temporary clone.
	///
	/// The service-account JSON key is never stored in this codebase - it is retrieved on
	/// demand from Delinea Secret Server via <see cref="DelineaClient"/> and the resulting
	/// authenticated Drive/Sheets service clients are cached for <see cref="CredentialLifetime"/>.
	/// </summary>
	public class GoogleSheetsClient
	{
		private static readonly TimeSpan CredentialLifetime = TimeSpan.FromHours(1);
		private static readonly string[] Scopes = { DriveService.Scope.Drive, SheetsService.Scope.Spreadsheets };

		/// <summary>Max columns to scan rightward from a pivot table's anchor cell when detecting its header width.</summary>
		private const int PivotMaxColumnScan = 50;

		/// <summary>Max rows to scan downward from a pivot table's anchor cell when detecting its extent, bounding pathologically large sheets.</summary>
		private const int PivotMaxRowScan = 50000;

		/// <summary>Consecutive fully-blank rows required before treating a pivot's rendered output as finished, so a single stray blank separator row doesn't end the scan early.</summary>
		private const int PivotBlankRowConfirmation = 2;

		/// <summary>Max collapse requests per batchUpdate call, so workbooks with many pivot tables can't produce an oversized single payload.</summary>
		private const int PivotBatchChunkSize = 100;

		/// <summary>Matches a pivot subtotal row's rendered label, e.g. "Driver: John Smith Total".</summary>
		private static readonly Regex TotalRowPattern = new Regex(@"\bTotal$", RegexOptions.IgnoreCase);

		private static readonly object CredentialLock = new object();
		private static DriveService _cachedDriveService;
		private static SheetsService _cachedSheetsService;
		private static DateTime _cachedCredentialExpiresAtUtc = DateTime.MinValue;

		private readonly DelineaClient _delineaClient;
		private readonly string _secretName;
		private readonly string _fieldSlug;

		public GoogleSheetsClient() : this(new DelineaClient(),
			ConfigurationManager.AppSettings["DNNStuff:SQLViewPro:DelineaGoogleSecretName"],
			"json-key")
		{
		}

		public GoogleSheetsClient(DelineaClient delineaClient, string secretName, string fieldSlug)
		{
			if (delineaClient == null)
			{
				throw new ArgumentNullException("delineaClient");
			}
			if (string.IsNullOrEmpty(secretName))
			{
				throw new InvalidOperationException("DNNStuff:SQLViewPro:DelineaGoogleSecretName is not configured in web.config appSettings, and no secret name override was supplied.");
			}

			_delineaClient = delineaClient;
			_secretName = secretName;
			_fieldSlug = fieldSlug;
		}

		/// <summary>
		/// Authenticates against Google (if needed) and returns the cached Drive/Sheets
		/// service clients. Safe to call before every operation - it is a no-op once cached.
		/// </summary>
		public void Authenticate()
		{
			lock (CredentialLock)
			{
				if (_cachedDriveService != null && DateTime.UtcNow < _cachedCredentialExpiresAtUtc)
				{
					return;
				}
			}

			string serviceAccountJson;
			try
			{
				serviceAccountJson = _delineaClient.GetFieldValue(_secretName, _fieldSlug);
			}
			catch (Exception ex)
			{
				throw new GoogleSheetsClientException(GoogleSheetsErrorType.Authentication, "Unable to retrieve the Google service-account key from Delinea Secret Server.", ex);
			}

			try
			{
				GoogleCredential credential;
				using (var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(serviceAccountJson)))
				{
					credential = GoogleCredential.FromStream(stream).CreateScoped(Scopes);
				}

				var initializer = new BaseClientService.Initializer
				{
					HttpClientInitializer = credential,
					ApplicationName = "DNNStuff SQLViewPro - Google Sheets Template"
				};

				lock (CredentialLock)
				{
					_cachedDriveService = new DriveService(initializer);
					_cachedSheetsService = new SheetsService(initializer);
					_cachedCredentialExpiresAtUtc = DateTime.UtcNow.Add(CredentialLifetime);
				}
			}
			catch (Exception ex)
			{
				throw new GoogleSheetsClientException(GoogleSheetsErrorType.Authentication, "Unable to authenticate with Google using the retrieved service-account key.", ex);
			}
		}

		private DriveService Drive
		{
			get { return _cachedDriveService; }
		}

		private SheetsService Sheets
		{
			get { return _cachedSheetsService; }
		}

		/// <summary>
		/// Finds a template file by name within a Drive folder (non-recursive) and returns its
		/// file id. Throws <see cref="GoogleSheetsErrorType.TemplateNotFound"/> when no match exists.
		/// </summary>
		public string FindTemplateByName(string folderId, string templateName)
		{
			try
			{
				var request = Drive.Files.List();
				request.Q = string.Format("'{0}' in parents and name = '{1}' and trashed = false", folderId, templateName.Replace("'", "\\'"));
				request.Fields = "files(id, name)";
				request.PageSize = 1;
				// The template folder lives on a shared drive - all
				// three of these are required together for Drive to search shared-drive content.
				request.SupportsAllDrives = true;
				request.IncludeItemsFromAllDrives = true;
				request.Corpora = "allDrives";

				var result = request.Execute();
				var file = result.Files != null ? result.Files.FirstOrDefault() : null;
				if (file == null)
				{
					throw new GoogleSheetsClientException(GoogleSheetsErrorType.TemplateNotFound, string.Format("Template '{0}' was not found in Drive folder '{1}'.", templateName, folderId));
				}

				return file.Id;
			}
			catch (Google.GoogleApiException ex)
			{
				throw GoogleSheetsClientException.FromGoogleApiException(GoogleSheetsErrorType.TemplateNotFound, string.Format("Error searching for template '{0}'.", templateName), ex);
			}
		}

		/// <summary>
		/// Clones the template spreadsheet so the original is never modified, and returns the
		/// id of the new spreadsheet. The clone is explicitly placed in <paramref name="parentFolderId"/> 
		/// so the caller always knows - and controls - where the temporary clone lives.
		/// </summary>
		public string CloneSpreadsheet(string templateFileId, string newFileName, string parentFolderId)
		{
			try
			{
				var copyMetadata = new Google.Apis.Drive.v3.Data.File { Name = newFileName };
				if (!string.IsNullOrEmpty(parentFolderId))
				{
					copyMetadata.Parents = new List<string> { parentFolderId };
				}

				var request = Drive.Files.Copy(copyMetadata, templateFileId);
				request.Fields = "id";
				// Required so the copy succeeds when the template lives on a shared drive.
				request.SupportsAllDrives = true;
				var result = request.Execute();
				return result.Id;
			}
			catch (Google.GoogleApiException ex)
			{
				throw GoogleSheetsClientException.FromGoogleApiException(GoogleSheetsErrorType.Clone, string.Format("Error cloning template '{0}'.", templateFileId), ex);
			}
		}

		/// <summary>Clears all values in the given A1 range (e.g. "Data!A2:Z").</summary>
		public void ClearRange(string spreadsheetId, string range)
		{
			try
			{
				var request = Sheets.Spreadsheets.Values.Clear(new ClearValuesRequest(), spreadsheetId, range);
				request.Execute();
			}
			catch (Google.GoogleApiException ex)
			{
				throw GoogleSheetsClientException.FromGoogleApiException(GoogleSheetsErrorType.Write, string.Format("Error clearing range '{0}'.", range), ex);
			}
		}

		/// <summary>
		/// Writes <paramref name="values"/> starting at the top-left cell of <paramref name="range"/>
		/// (e.g. "Data!A1"), using USER_ENTERED so formulas typed into the range are evaluated.
		/// </summary>
		public void WriteData(string spreadsheetId, string range, IList<IList<object>> values)
		{
			try
			{
				var valueRange = new ValueRange { Values = values };
				var request = Sheets.Spreadsheets.Values.Update(valueRange, spreadsheetId, range);
				request.ValueInputOption = SpreadsheetsResource.ValuesResource.UpdateRequest.ValueInputOptionEnum.USERENTERED;
				request.Execute();
			}
			catch (Google.GoogleApiException ex)
			{
				throw GoogleSheetsClientException.FromGoogleApiException(GoogleSheetsErrorType.Write, string.Format("Error writing data to range '{0}'.", range), ex);
			}
		}

		/// <summary>
		/// Exports the (already-recalculated) spreadsheet as an .xlsx byte array.
		/// </summary>
		public byte[] ExportAsXlsx(string spreadsheetId)
		{
			const string xlsxMimeType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
			try
			{
				var request = Drive.Files.Export(spreadsheetId, xlsxMimeType);
				using (var ms = new MemoryStream())
				{
					request.Download(ms);
					return ms.ToArray();
				}
			}
			catch (Google.GoogleApiException ex)
			{
				throw GoogleSheetsClientException.FromGoogleApiException(GoogleSheetsErrorType.Export, string.Format("Error exporting spreadsheet '{0}' as xlsx.", spreadsheetId), ex);
			}
		}

		/// <summary>
		/// Collapse every pivot table's outer-most row-field group by writing
		/// <c>Collapsed = true</c> into that group's <c>ValueMetadata</c>, one entry per unique
		/// group value, so the pivot renders with only header/Total rows visible.
		///
		/// This reads each pivot table's rendered cell text to discover the actual group values
		/// currently present (since a freshly written pivot has no pre-existing
		/// <c>ValueMetadata</c>), merges <c>Collapsed = true</c> into the outer-most
		/// <see cref="PivotGroup"/>'s <c>ValueMetadata</c> for each discovered value (preserving
		/// any existing entries for values not currently rendered), and writes the entire
		/// <c>PivotTable</c> object back via <c>UpdateCells</c> (the Sheets API requires the
		/// whole pivot table definition on write, not a partial patch). Spreadsheets with no
		/// pivot tables, or pivot tables with no row fields, are a no-op.
		/// </summary>
		public void CollapsePivotTables(string spreadsheetId)
		{
			try
			{
				var getRequest = Sheets.Spreadsheets.Get(spreadsheetId);
				getRequest.Fields = "sheets(properties.sheetId,properties.gridProperties.rowCount,data(startRow,startColumn,rowData(values(formattedValue,pivotTable))))";
				var spreadsheet = getRequest.Execute();

				var collapseRequests = new List<Request>();

				foreach (var sheet in spreadsheet.Sheets ?? new List<Sheet>())
				{
					var sheetId = sheet.Properties != null ? sheet.Properties.SheetId : null;
					if (sheetId == null)
					{
						continue;
					}

					var maxRow = Math.Min(
						sheet.Properties.GridProperties != null && sheet.Properties.GridProperties.RowCount.HasValue ? sheet.Properties.GridProperties.RowCount.Value : 0,
						PivotMaxRowScan);
					var dataChunks = sheet.Data ?? new List<GridData>();
					var cellText = BuildCellTextLookup(dataChunks);

					foreach (var dataChunk in dataChunks)
					{
						var startRow = dataChunk.StartRow ?? 0;
						var startColumn = dataChunk.StartColumn ?? 0;
						var rowDataList = dataChunk.RowData ?? new List<RowData>();

						for (var rowIndex = 0; rowIndex < rowDataList.Count; rowIndex++)
						{
							var values = rowDataList[rowIndex].Values ?? new List<CellData>();
							for (var colIndex = 0; colIndex < values.Count; colIndex++)
							{
								var pivotTable = values[colIndex].PivotTable;
								var outerRowField = pivotTable != null && pivotTable.Rows != null && pivotTable.Rows.Count > 0 ? pivotTable.Rows[0] : null;
								if (pivotTable == null || outerRowField == null)
								{
									continue;
								}

								var anchorRow = startRow + rowIndex;
								var anchorColumn = startColumn + colIndex;

								var width = DetectPivotWidth(cellText, anchorRow, anchorColumn, pivotTable);
								var extentEnd = DetectPivotExtent(cellText, anchorRow, anchorColumn, width, maxRow);

								if (extentEnd <= anchorRow)
								{
									continue;
								}

								// Only the outer-most row field needs to be collapsed:
								// collapsing its groups collapses everything nested beneath.
								var groupValues = DetectOuterGroupValues(
									cellText,
									anchorColumn,
									anchorRow + 1,
									extentEnd,
									outerRowField.ShowTotals != false);

								if (groupValues.Count == 0)
								{
									continue;
								}

								var updatedRows = new List<PivotGroup>(pivotTable.Rows);
								updatedRows[0] = new PivotGroup
								{
									GroupRule = outerRowField.GroupRule,
									Label = outerRowField.Label,
									RepeatHeadings = outerRowField.RepeatHeadings,
									ShowTotals = outerRowField.ShowTotals,
									SortOrder = outerRowField.SortOrder,
									SourceColumnOffset = outerRowField.SourceColumnOffset,
									ValueBucket = outerRowField.ValueBucket,
									ValueMetadata = MergeCollapsedValueMetadata(outerRowField.ValueMetadata, groupValues),
								};

								var updatedPivotTable = new PivotTable
								{
									Columns = pivotTable.Columns,
									Criteria = pivotTable.Criteria,
									Rows = updatedRows,
									Source = pivotTable.Source,
									ValueLayout = pivotTable.ValueLayout,
									Values = pivotTable.Values,
								};

								collapseRequests.Add(new Request
								{
									UpdateCells = new UpdateCellsRequest
									{
										Start = new GridCoordinate { SheetId = sheetId, RowIndex = anchorRow, ColumnIndex = anchorColumn },
										Rows = new List<RowData>
										{
											new RowData
											{
												Values = new List<CellData>
												{
													new CellData { PivotTable = updatedPivotTable },
												},
											},
										},
										Fields = "pivotTable",
									},
								});
							}
						}
					}
				}

				if (collapseRequests.Count == 0)
				{
					return;
				}

				for (var i = 0; i < collapseRequests.Count; i += PivotBatchChunkSize)
				{
					var chunk = collapseRequests.Skip(i).Take(PivotBatchChunkSize).ToList();
					var batchRequest = Sheets.Spreadsheets.BatchUpdate(new BatchUpdateSpreadsheetRequest { Requests = chunk }, spreadsheetId);
					batchRequest.Execute();
				}
			}
			catch (Google.GoogleApiException ex)
			{
				throw GoogleSheetsClientException.FromGoogleApiException(GoogleSheetsErrorType.Collapse, string.Format("Error collapsing pivot tables in spreadsheet '{0}'.", spreadsheetId), ex);
			}
		}

		/// <summary>Build a fast (row, col) -> formattedValue lookup over one or more fetched GridData chunks.</summary>
		private static Func<int, int, string> BuildCellTextLookup(IList<GridData> dataChunks)
		{
			var map = new Dictionary<string, string>();

			foreach (var chunk in dataChunks)
			{
				var startRow = chunk.StartRow ?? 0;
				var startColumn = chunk.StartColumn ?? 0;
				var rowDataList = chunk.RowData ?? new List<RowData>();

				for (var rowIndex = 0; rowIndex < rowDataList.Count; rowIndex++)
				{
					var values = rowDataList[rowIndex].Values ?? new List<CellData>();
					for (var colIndex = 0; colIndex < values.Count; colIndex++)
					{
						if (!string.IsNullOrEmpty(values[colIndex].FormattedValue))
						{
							map[string.Format("{0}:{1}", startRow + rowIndex, startColumn + colIndex)] = values[colIndex].FormattedValue;
						}
					}
				}
			}

			return (row, col) =>
			{
				string text;
				return map.TryGetValue(string.Format("{0}:{1}", row, col), out text) ? text : string.Empty;
			};
		}

		/// <summary>Count contiguous non-empty header columns starting at the pivot table's anchor cell, to bound scans.</summary>
		private static int DetectPivotWidth(Func<int, int, string> cellText, int anchorRow, int anchorColumn, PivotTable pivotTable)
		{
			var structuralMinWidth = (pivotTable.Rows != null ? pivotTable.Rows.Count : 0) + (pivotTable.Values != null ? pivotTable.Values.Count : 0);

			var width = 0;
			while (width < PivotMaxColumnScan && cellText(anchorRow, anchorColumn + width) != string.Empty)
			{
				width++;
			}

			return Math.Max(width, Math.Max(structuralMinWidth, 1));
		}

		/// <summary>Finds the last row (inclusive) belonging to a rendered pivot table's output, scanning down from its anchor.</summary>
		private static int DetectPivotExtent(Func<int, int, string> cellText, int anchorRow, int anchorColumn, int width, int maxRow)
		{
			var extentEnd = anchorRow;
			var consecutiveBlankRows = 0;

			for (var row = anchorRow + 1; row < maxRow; row++)
			{
				var rowHasContent = false;
				for (var col = anchorColumn; col < anchorColumn + width; col++)
				{
					if (cellText(row, col) != string.Empty)
					{
						rowHasContent = true;
						break;
					}
				}

				if (!rowHasContent)
				{
					consecutiveBlankRows++;
					if (consecutiveBlankRows >= PivotBlankRowConfirmation)
					{
						break;
					}
					continue;
				}

				consecutiveBlankRows = 0;
				extentEnd = row;
			}

			return extentEnd;
		}

		/// <summary>
		/// Scans the outer-most pivot row-field column and returns the distinct group values
		/// rendered there. When <paramref name="excludeTotals"/> is true (the field has
		/// ShowTotals enabled), rows matching <see cref="TotalRowPattern"/> are treated as
		/// generated subtotal rows and skipped; otherwise every non-blank value is kept, even
		/// if it happens to end in "Total".
		/// </summary>
		private static List<string> DetectOuterGroupValues(Func<int, int, string> cellText, int column, int rangeStart, int rangeEnd, bool excludeTotals)
		{
			var values = new List<string>();

			for (var row = rangeStart; row <= rangeEnd; row++)
			{
				var text = cellText(row, column);
				if (text == string.Empty || (excludeTotals && TotalRowPattern.IsMatch(text.Trim())))
				{
					continue;
				}

				values.Add(text);
			}

			return values;
		}

		/// <summary>
		/// Merges <c>Collapsed = true</c> into a row field's existing <c>ValueMetadata</c> for
		/// each newly-discovered group value, keyed by <c>Value.StringValue</c>. Existing
		/// entries for values not currently rendered (e.g. filtered out) are preserved rather
		/// than discarded.
		/// </summary>
		private static List<PivotGroupValueMetadata> MergeCollapsedValueMetadata(IList<PivotGroupValueMetadata> existing, List<string> groupValues)
		{
			var merged = new Dictionary<string, PivotGroupValueMetadata>();
			var order = new List<string>();

			foreach (var entry in existing ?? new List<PivotGroupValueMetadata>())
			{
				var key = entry.Value != null ? entry.Value.StringValue : null;
				if (key != null)
				{
					if (!merged.ContainsKey(key))
					{
						order.Add(key);
					}
					merged[key] = entry;
				}
			}

			foreach (var value in groupValues)
			{
				if (!merged.ContainsKey(value))
				{
					order.Add(value);
				}
				merged[value] = new PivotGroupValueMetadata { Value = new ExtendedValue { StringValue = value }, Collapsed = true };
			}

			return order.Select(key => merged[key]).ToList();
		}

		/// <summary>
		/// Lists every (non-trashed) folder anywhere within the given shared drive - not just
		/// the folders directly under the drive's root - sorted by name. Used to populate the
		/// "Drive Folder" picker in the report settings UI, so users select a folder by name
		/// instead of pasting a raw folder id.
		/// </summary>
		public IList<Google.Apis.Drive.v3.Data.File> ListFoldersInSharedDrive(string sharedDriveId)
		{
			try
			{
				var folders = new List<Google.Apis.Drive.v3.Data.File>();
				string pageToken = null;

				do
				{
					var request = Drive.Files.List();
					request.Q = "mimeType = 'application/vnd.google-apps.folder' and trashed = false";
					request.Fields = "nextPageToken, files(id, name)";
					request.DriveId = sharedDriveId;
					request.Corpora = "drive";
					// Required together for Drive to enumerate shared-drive content.
					request.SupportsAllDrives = true;
					request.IncludeItemsFromAllDrives = true;
					request.PageToken = pageToken;
					request.PageSize = 1000;

					var result = request.Execute();
					if (result.Files != null)
					{
						folders.AddRange(result.Files);
					}

					pageToken = result.NextPageToken;
				} while (!string.IsNullOrEmpty(pageToken));

				return folders.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
			}
			catch (Google.GoogleApiException ex)
			{
				throw GoogleSheetsClientException.FromGoogleApiException(GoogleSheetsErrorType.FolderList, string.Format("Error listing folders in shared drive '{0}'.", sharedDriveId), ex);
			}
		}

		/// <summary>
		/// Best-effort cleanup of the temporary cloned spreadsheet. Moves the file to Trash
		/// instead of permanently deleting it, since the shared drive's permission settings
		/// do not allow a hard delete. Callers should invoke this from a finally block and
		/// treat failures as non-fatal (log a warning, don't fail the report).
		/// </summary>
		public void DeleteSpreadsheet(string spreadsheetId)
		{
			try
			{
				var request = Drive.Files.Update(new Google.Apis.Drive.v3.Data.File { Trashed = true }, spreadsheetId);
				request.SupportsAllDrives = true;
				request.Execute();
			}
			catch (Google.GoogleApiException ex)
			{
				throw GoogleSheetsClientException.FromGoogleApiException(GoogleSheetsErrorType.Delete, string.Format("Error trashing temporary spreadsheet '{0}'.", spreadsheetId), ex);
			}
		}
	}
}
