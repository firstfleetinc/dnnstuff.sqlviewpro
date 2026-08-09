using System.Collections;
using System.Web.UI;
using System.Data;
using System.Text;
using System.Web;

namespace DNNStuff.SQLViewPro.Controls
{
    public abstract class ReportControlBase : UserControl
    {
        public event OnDrillDownEventHandler OnDrillDown;
        public delegate void OnDrillDownEventHandler(object o, DrilldownEventArgs e);

        #region Public Properties
        public string FullScreenUrl { get; set; }
        public DrilldownState State { get; set; }
        public ReportInfo Report { get; set; }
        public StringBuilder DebugInfo { get; set; } = new StringBuilder();

        public Hashtable ReportTokens { get; set; }
        public Hashtable ReportParameterTokens { get; set; }

	    public string QueryText
		{
			get
			{
				var s = Report.ReportCommand;
				if (s != "")
				{
					s = ReplaceReportTokens(s);
				}
				return s;
			}
		}
#endregion
		
#region  Public Methods
		/// <summary>
		/// Retrieve data for the given query
		/// </summary>
		/// <param name="query">The query to execute</param>
		/// <returns>DataSet containing all data</returns>
		/// <remarks></remarks>
		public DataSet ReportData(string query)
		{
			return Services.Data.Query.RetrieveData(query, Report.ReportConnectionString, Report.ReportCommandCacheTimeout, Report.ReportCommandCacheScheme);
		}
		
		/// <summary>
		/// Retrieves data for the report query
		/// </summary>
		/// <returns>DataSet containing all data</returns>
		/// <remarks></remarks>
		public DataSet ReportData()
		{
			return ReportData(QueryText);
		}
		
		/// <summary>
		/// Retrieves a unique string based on the given key
		/// </summary>
		/// <param name="key"></param>
		/// <returns></returns>
		/// <remarks></remarks>
		public string Unique(string key)
		{
			return key + "_" + Report.ReportId.ToString();
		}
		
#endregion
		
#region Abstract Methods
		public abstract void LoadRuntimeSettings(ReportInfo settings);
#endregion
		
		public virtual string RenderHeaderAsText()
		{
			var s = new StringBuilder();
			if (Report.ReportHeaderText.Length > 0)
			{
				s.AppendFormat("<div class=\"{0}_Header\">{1}</div>", Report.ReportTheme, ReplaceReportTokens(Report.ReportHeaderText, includeParameterTokens: true));
			}
			return s.ToString();
		}
		
		public virtual string RenderFooterAsText()
		{
			var s = new StringBuilder();
			if (Report.ReportFooterText.Length > 0)
			{
				s.AppendFormat("<div class=\"{0}_Footer\">{1}</div>", Report.ReportTheme, ReplaceReportTokens(Report.ReportFooterText, includeParameterTokens: true));
			}
			return s.ToString();
		}
		
		public virtual void RenderNoItems()
		{
			if (Report.ReportNoItemsText.Length > 0)
			{
				// No Items
				var ctrl = new System.Web.UI.HtmlControls.HtmlGenericControl("div");
				ctrl.InnerHtml = ReplaceReportTokens(Report.ReportNoItemsText, includeParameterTokens: true);
				ctrl.Attributes.Add("class", Report.ReportTheme + "_NoItems");
				Controls.Add(ctrl);
			}
		}
		
		public virtual string RenderPageTitleAsText()
		{
			return ReplaceReportTokens(Report.ReportPageTitle, includeParameterTokens:true);
		}
		
		public virtual string RenderMetaDescriptionAsText()
		{
			return ReplaceReportTokens(Report.ReportMetaDescription, includeParameterTokens: true);
		}
		
#region Events
        protected void DrillDown(ReportControlBase o, DrilldownEventArgs e)
        {
            var onDrillDownEventHandler = OnDrillDown;
            if (onDrillDownEventHandler != null)
            {
                onDrillDownEventHandler(o, e);
            }
        }

        protected void Page_Init(object sender, System.EventArgs e)
		{
			var postbackControlId = "";
			if (Page.IsPostBack)
			{
				var postbackControl = ControlHelpers.GetPostBackControl(Page);
				if (postbackControl != null)
				{
					postbackControlId = postbackControl.ID;
				}
			}
			
			if (postbackControlId != "cmdBack")
			{
				if (Report.ReportPageTitle != "")
				{
					var thisPage = (DotNetNuke.Framework.CDefault) Page;
					thisPage.Title = RenderPageTitleAsText();
				}
				if (Report.ReportMetaDescription != "")
				{
					var thisPage = (DotNetNuke.Framework.CDefault) Page;
					var meta = RenderMetaDescriptionAsText();
					if (meta.Contains("<meta"))
					{
						thisPage.Header.Controls.Add(new LiteralControl(meta));
					}
					else
					{
						thisPage.Description = meta;
					}
				}
				
			}
		}

        protected void Page_PreRender(object sender, System.EventArgs e)
		{
			if (State.ReportSet.ReportSetDebug)
			{
				Controls.Add(RenderDebug(DebugInfo.ToString()));
			}
		}
#endregion
		
#region Token Replacement
		protected string ReplaceReportTokens(string text, DataSet ds = null, bool includeParameterTokens = false)
		{
            GenerateReportParameters();
            GenerateReportTokens();
            string ret = text;
            if (includeParameterTokens)
            {
                // do this first or we get @parameter tokens in the output text
                ret = Services.Data.TokenReplacement.ReplaceTokens(ret, ReportParameterTokens, ds);
            }
            ret = Services.Data.TokenReplacement.ReplaceTokens(ret, ReportTokens, ds);
            return ret;
		}

        private void GenerateReportParameters()
        {
            ReportParameterTokens = new Hashtable(); // holds tokens for report parameters, only used in headers etc. not used in sql query

            // do parameters
            foreach (ParameterInfo param in State.Parameters)
                {
                    var tokenValue = "";
                    if (param.Values != null)
                    {
                        if (param.MultiValued)
                        {
                            tokenValue = string.Join(",", param.Values.ToArray());
                        }
                        else
                        {
                            tokenValue = (string)(param.Values[0].Replace("\'", "\'\'"));
                        }
                    }
                    ReportParameterTokens.SafeHashtableAdd("PARAMETER:" + param.ParameterIdentifier.ToUpper(), tokenValue);

                if (param.ExtraValues != null)
                    {
                        foreach (string key in param.ExtraValues.Keys)
                        {
                            ReportParameterTokens.SafeHashtableAdd("PARAMETER:" + param.ParameterIdentifier.ToUpper() + ":" + key.ToLower(), param.ExtraValues[key]);
                        }
                    }
                }

                var keyval = default(object);
                // add querystring values
                var qs = new System.Collections.Specialized.NameValueCollection(HttpContext.Current.Request.QueryString); // create a copy, some weird errors happening with url rewriters
                foreach (string key in qs.Keys)
                {
                    keyval = qs[key];
                    if (key != null && keyval != null)
                    {
                        ReportParameterTokens.SafeHashtableAdd("QS:" + key.ToUpper(), keyval.ToString().Replace("\'", "\'\'"));
                        ReportParameterTokens.SafeHashtableAdd("QUERYSTRING:" + key.ToUpper(), keyval.ToString().Replace("\'", "\'\'"));
                    }
                }

                // add form variables
                var fv = new System.Collections.Specialized.NameValueCollection(HttpContext.Current.Request.Form); // create a copy
                foreach (string key in fv.Keys)
                {
                    keyval = fv[key];
                    if (key != null && keyval != null)
                    {
                        ReportParameterTokens.SafeHashtableAdd("FV:" + key.ToUpper(), keyval.ToString().Replace("\'", "\'\'"));
                        ReportParameterTokens.SafeHashtableAdd("FORMVAL:" + key.ToUpper(), keyval.ToString().Replace("\'", "\'\'"));
                    }
                }

        }

        private void GenerateReportTokens()
		{
			
		    ReportTokens = new Hashtable();

		    var fullScreenParameters = "";

            // do parameters - substitute actual parameter values directly into the query text
            foreach (ParameterInfo param in State.Parameters)
            {
                var tokenValue = "";
                if (param.Values != null)
                {
                    if (param.MultiValued)
                    {
                        tokenValue = string.Join(",", param.Values.ToArray());
                    }
                    else
                    {
                        tokenValue = (string)(param.Values[0].Replace("\'", "\'\'"));
                    }
                }
                ReportTokens.SafeHashtableAdd("PARAMETER:" + param.ParameterIdentifier.ToUpper(), tokenValue);

                if (param.ExtraValues != null)
                {
                    foreach (string key in param.ExtraValues.Keys)
                    {
                        ReportTokens.SafeHashtableAdd("PARAMETER:" + param.ParameterIdentifier.ToUpper() + ":" + key.ToUpper(), param.ExtraValues[key]);
                    }
                }
            }

            // add querystring values
            var qs = new System.Collections.Specialized.NameValueCollection(HttpContext.Current.Request.QueryString); // create a copy, some weird errors happening with url rewriters
            var keyval = default(object);
            foreach (string key in qs.Keys)
            {
                keyval = qs[key];
                if (key != null && keyval != null)
                {
                    ReportTokens.SafeHashtableAdd("QS:" + key.ToUpper(), keyval.ToString().Replace("\'", "\'\'"));
                    ReportTokens.SafeHashtableAdd("QUERYSTRING:" + key.ToUpper(), keyval.ToString().Replace("\'", "\'\'"));
                }
            }

            // add form values
            var fv = new System.Collections.Specialized.NameValueCollection(HttpContext.Current.Request.Form); // create a copy, some weird errors happening with url rewriters
            foreach (string key in fv.Keys)
            {
                keyval = fv[key];
                if (key != null && keyval != null)
                {
                    ReportTokens.SafeHashtableAdd("FV:" + key.ToUpper(), keyval.ToString().Replace("\'", "\'\'"));
                    ReportTokens.SafeHashtableAdd("FORMVAL:" + key.ToUpper(), keyval.ToString().Replace("\'", "\'\'"));
                }
            }

            // fullscreen url
            foreach (DictionaryEntry param in ReportTokens)
            {
                fullScreenParameters = fullScreenParameters + string.Format("&{0}={1}", param.Key.ToString(), param.Value.ToString());
            }
            ReportTokens.SafeHashtableAdd("FULLSCREENURL", string.Format("{0}{1}&hidebackbutton=1", FullScreenUrl, fullScreenParameters));

            ReportTokens.SafeHashtableAdd("REPORTNAME", Report.ReportName);
            ReportTokens.SafeHashtableAdd("REPORTTYPE", Report.ReportTypeId);
            ReportTokens.SafeHashtableAdd("REPORTTYPENAME", Report.ReportTypeName);
		    if (Request != null)
		    {
                ReportTokens.SafeHashtableAdd("PAGEURL", Request.Url.AbsoluteUri);
		    }
            ReportTokens.SafeHashtableAdd("IMAGEURL", ResolveUrl("~/images"));
		}
		
		
#endregion
		
#region Debug
		private Control RenderDebug(string info)
		{
			return ParseControl(RenderDebugAsText(info));
		}
		
		private string RenderDebugAsText(string info)
		{
			var s = new StringBuilder();
			if (info.Length > 0)
			{
				s.AppendFormat("<div class=\"{0}_Debug\"><pre>{1}</pre></div>", Report.ReportTheme, info);
			}
			return s.ToString();
		}
#endregion
		
		
	}
}

