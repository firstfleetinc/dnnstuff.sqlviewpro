using System;
using System.Collections.Generic;
using System.Data;
using System.Web.UI;
using DotNetNuke.Common;
using DNNStuff.SQLViewPro.Services.GoogleSheets;

namespace DNNStuff.SQLViewPro.GoogleSheetsReports
{
	public partial class GoogleSheetsTemplateReportControl : Controls.ReportControlBase
	{

#region  Web Form Designer Generated Code

		//This call is required by the Web Form Designer.
		[System.Diagnostics.DebuggerStepThrough()]private void InitializeComponent()
		{

		}

		private void Page_Init(Object sender, EventArgs e)
		{
			//CODEGEN: This method call is required by the Web Form Designer
			//Do not modify it using the code editor.
			InitializeComponent();

			try
			{
				if (Globals.IsEditMode())
				{
					Controls.Add(new LiteralControl("<strong>Please switch to view mode to generate the Google Sheets Template file</strong>"));
				}
				else
				{
					ProcessGoogleSheetsTemplate();
				}
			}
			catch (GoogleSheetsClientException ex)
			{
				DotNetNuke.Services.Exceptions.Exceptions.LogException(ex);
				Controls.Add(new LiteralControl(string.Format("<strong>Unable to generate the Google Sheets Template file ({0}): {1}</strong>", ex.ErrorType, ex.Message)));
			}
			catch (Exception ex)
			{
				DotNetNuke.Services.Exceptions.Exceptions.ProcessModuleLoadException(this, ex);
			}
		}

#endregion

#region  Page

		private GoogleSheetsTemplateReportSettings ReportExtra { get; set; } = new GoogleSheetsTemplateReportSettings();

#endregion

#region  Base Method Implementations
		public override void LoadRuntimeSettings(ReportInfo Settings)
		{
			ReportExtra = (GoogleSheetsTemplateReportSettings) (Serialization.DeserializeObject(Settings.ReportConfig, typeof(GoogleSheetsTemplateReportSettings)));
		}
#endregion

#region  Google Sheets Template
		private void ProcessGoogleSheetsTemplate()
		{
			var ds = ReportData();

			// add debug info
			if (State.ReportSet.ReportSetDebug)
			{
				DebugInfo.Append(QueryText);
			}

			if (ds.Tables.Count == 0 || ds.Tables[0].Rows.Count == 0)
			{
				RenderNoItems();
			}
			else
			{
				RenderGoogleSheetsTemplate(ds.Tables[0]);
			}
		}

		private void RenderGoogleSheetsTemplate(DataTable dt)
		{
			var client = CreateClient();
			client.Authenticate();

			var templateFileId = client.FindTemplateByName(ReportExtra.DriveFolderId, ReportExtra.TemplateName);

			var outputFileName = ReportExtra.OutputFileName.Replace("[TICKS]", DateTime.Now.Ticks.ToString());
			var spreadsheetId = client.CloneSpreadsheet(templateFileId, outputFileName, ReportExtra.DriveFolderId);

			try
			{
				var dataSheetName = ReportExtra.DataSheetName;

				if (ReportExtra.ContainsHeaderRow)
				{
					// keep the existing header row - clear/write starting on row 2
					client.ClearRange(spreadsheetId, string.Format("{0}!A2", dataSheetName));
					client.WriteData(spreadsheetId, string.Format("{0}!A2", dataSheetName), BuildValueRows(dt, includeHeader: false));
				}
				else
				{
					// no existing header - clear the whole sheet and write our own header row
					client.ClearRange(spreadsheetId, dataSheetName);
					client.WriteData(spreadsheetId, string.Format("{0}!A1", dataSheetName), BuildValueRows(dt, includeHeader: true));
				}

				try
				{
					client.CollapsePivotTables(spreadsheetId);
				}
				catch (Exception ex)
				{
					// Best-effort: a failure here shouldn't block the report from being
					// generated - it just means the pivot table(s) will be fully expanded
					// instead of collapsed in this export.
					DotNetNuke.Services.Exceptions.Exceptions.LogException(ex);
				}

				var xlsxBytes = client.ExportAsXlsx(spreadsheetId);

				var details = new ExportDetails();
				details.Binary = xlsxBytes;
				details.Filename = outputFileName + ".xlsx";
				details.Disposition = ReportExtra.DispositionType;

				Session[Export.EXPORT_KEY] = details;

				if (Request.ServerVariables["HTTP_USER_AGENT"].Contains("ipad") || Request.ServerVariables["HTTP_USER_AGENT"].Contains("iphone"))
				{
					//' no iframe for iphone, ipad
					Response.Redirect(string.Format("{0}?ModuleId={1}&TabId={2}", ResolveUrl("~/DesktopModules/DNNStuff - SQLViewPro/Export.aspx"), State.ModuleId, State.TabId));
				}
				else
				{
					Controls.Add(new LiteralControl(string.Format("<iframe style=\'display:none\' scrolling=\'auto\' src=\'{0}?ModuleId={1}&TabId={2}\'></iframe>", ResolveUrl("~/DesktopModules/DNNStuff - SQLViewPro/Export.aspx"), State.ModuleId, State.TabId)));
				}
			}
			finally
			{
				// best-effort cleanup of the temporary clone - never fail the report because of it
				try
				{
					client.DeleteSpreadsheet(spreadsheetId);
				}
				catch (Exception ex)
				{
					DotNetNuke.Services.Exceptions.Exceptions.LogException(ex);
				}
			}
		}

		private GoogleSheetsClient CreateClient()
		{
			return new GoogleSheetsClient();
		}

		private static IList<IList<object>> BuildValueRows(DataTable dt, bool includeHeader)
		{
			var rows = new List<IList<object>>();

			if (includeHeader)
			{
				var headerRow = new List<object>();
				foreach (DataColumn column in dt.Columns)
				{
					headerRow.Add(column.ColumnName);
				}
				rows.Add(headerRow);
			}

			foreach (DataRow dataRow in dt.Rows)
			{
				var row = new List<object>();
				for (var col = 0; col <= dt.Columns.Count - 1; col++)
				{
					row.Add(dataRow[col]);
				}
				rows.Add(row);
			}

			return rows;
		}
#endregion
	}
}
