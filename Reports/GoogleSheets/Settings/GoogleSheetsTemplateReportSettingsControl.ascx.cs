using System;
using System.Configuration;
using System.Xml.Serialization;
using DNNStuff.SQLViewPro.Controls;
using DNNStuff.SQLViewPro.Services.GoogleSheets;

namespace DNNStuff.SQLViewPro.GoogleSheetsReports
{

	public partial class GoogleSheetsTemplateReportSettingsControl : ReportSettingsControlBase
	{

#region  Web Form Designer Generated Code

		//This call is required by the Web Form Designer.
		[System.Diagnostics.DebuggerStepThrough()]private void InitializeComponent()
		{

		}

		private void Page_Init(System.Object sender, System.EventArgs e)
		{
			//CODEGEN: This method call is required by the Web Form Designer
			//Do not modify it using the code editor.
			InitializeComponent();
		}

#endregion


#region  Base Method Implementations
		protected override string LocalResourceFile => ResolveUrl("App_LocalResources/GoogleSheetsTemplateReportSettingsControl");

	    public override string UpdateSettings()
		{

			var obj = new GoogleSheetsTemplateReportSettings();
			obj.DriveFolderId = ddDriveFolderId.SelectedValue;
			obj.TemplateName = txtTemplateName.Text;
			obj.DataSheetName = txtDataSheetName.Text;
			obj.ContainsHeaderRow = chkContainsHeaderRow.Checked;
			obj.OutputFileName = txtOutputFileName.Text;
			obj.DispositionType = ddDispositionType.SelectedValue;

			return Serialization.SerializeObject(obj, typeof(GoogleSheetsTemplateReportSettings));

		}

		public override void LoadSettings(string settings)
		{
			// LoadSettings is invoked directly by EditReport.ascx.cs right after LoadControl(),
			// before this control is added to the page's control tree - so this is the only
			// reliable place (on both the initial load and every postback) to populate the
			// folder dropdown before selecting the persisted value.
			PopulateDriveFolderDropdown();

			var obj = new GoogleSheetsTemplateReportSettings();
			if (!string.IsNullOrEmpty(settings))
			{
				obj = (GoogleSheetsTemplateReportSettings) (Serialization.DeserializeObject(settings, typeof(GoogleSheetsTemplateReportSettings)));
			}
			ControlHelpers.InitDropDownByValue(ddDriveFolderId, obj.DriveFolderId);
			txtTemplateName.Text = obj.TemplateName;
			txtDataSheetName.Text = obj.DataSheetName;
			chkContainsHeaderRow.Checked = obj.ContainsHeaderRow;
			txtOutputFileName.Text = obj.OutputFileName;

			ControlHelpers.InitDropDownByValue(ddDispositionType, obj.DispositionType);
		}

#endregion

#region  Drive Folder Dropdown

		private void PopulateDriveFolderDropdown()
		{
			try
			{
				var sharedDriveId = ConfigurationManager.AppSettings["DNNStuff:SQLViewPro:GoogleSheetsSharedDriveId"];
				if (string.IsNullOrEmpty(sharedDriveId))
				{
					throw new InvalidOperationException("DNNStuff:SQLViewPro:GoogleSheetsSharedDriveId is not configured in web.config appSettings.");
				}

				var client = new GoogleSheetsClient();
				client.Authenticate();
				var folders = client.ListFoldersInSharedDrive(sharedDriveId);

				ddDriveFolderId.DataSource = folders;
				ddDriveFolderId.DataBind();

				litDriveFolderError.Visible = false;
				litDriveFolderError.Text = string.Empty;
			}
			catch (Exception ex)
			{
				DotNetNuke.Services.Exceptions.Exceptions.LogException(ex);

				ddDriveFolderId.Items.Clear();
				litDriveFolderError.Text = "<span class=\"dnnFormMessage dnnFormValidationSummary\">Unable to load Drive folders. Check the Google Sheets configuration.</span>";
				litDriveFolderError.Visible = true;
			}
		}

#endregion

	}

#region  Settings
	/// <summary>
	/// Per-report configuration for the "Google Sheets Template" report type. Follows the
	/// same XML-serialized settings pattern as <c>ExcelTemplateReportSettings</c>.
	/// </summary>
	[XmlRootAttribute(ElementName = "Settings", IsNullable = false)]public class GoogleSheetsTemplateReportSettings
	{
		/// <summary>The Google Drive folder id that contains the template spreadsheet.</summary>
		public string DriveFolderId {get; set;}
		/// <summary>The template spreadsheet's file name within <see cref="DriveFolderId"/>.</summary>
		public string TemplateName {get; set;}
		/// <summary>The sheet name within the template where report data is written.</summary>
		public string DataSheetName {get; set;}
		/// <summary>Prefix for the exported file name. "[TICKS]" is replaced with the current tick count.</summary>
		public string OutputFileName {get; set;}
		public bool ContainsHeaderRow {get; set;}
	    public string DispositionType { get; set; } = "attachment";
	}
#endregion

}
