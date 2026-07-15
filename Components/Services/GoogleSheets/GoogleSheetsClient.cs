using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
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
		/// Best-effort cleanup of the temporary cloned spreadsheet. Callers should invoke this
		/// from a finally block and treat failures as non-fatal (log a warning, don't fail the report).
		/// </summary>
		public void DeleteSpreadsheet(string spreadsheetId)
		{
			try
			{
				var request = Drive.Files.Delete(spreadsheetId);
				request.SupportsAllDrives = true;
				request.Execute();
			}
			catch (Google.GoogleApiException ex)
			{
				throw GoogleSheetsClientException.FromGoogleApiException(GoogleSheetsErrorType.Delete, string.Format("Error deleting temporary spreadsheet '{0}'.", spreadsheetId), ex);
			}
		}
	}
}
