using System;

namespace DNNStuff.SQLViewPro.Services.GoogleSheets
{
	public enum GoogleSheetsErrorType
	{
		Authentication,
		TemplateNotFound,
		Clone,
		Write,
		Export,
		Delete,
		FolderList,
		Collapse,
		RateLimit,
		Unknown
	}

	/// <summary>
	/// Wraps failures from <see cref="GoogleSheetsClient"/> operations with a categorized
	/// <see cref="GoogleSheetsErrorType"/> so callers (report control, logging) can react
	/// appropriately - e.g. retry on RateLimit, surface a friendly message on TemplateNotFound.
	/// </summary>
	public class GoogleSheetsClientException : Exception
	{
		public GoogleSheetsErrorType ErrorType { get; private set; }

		public GoogleSheetsClientException(GoogleSheetsErrorType errorType, string message)
			: base(message)
		{
			ErrorType = errorType;
		}

		public GoogleSheetsClientException(GoogleSheetsErrorType errorType, string message, Exception innerException)
			: base(message, innerException)
		{
			ErrorType = errorType;
		}

		/// <summary>
		/// Maps a Google API exception to the appropriate error type, treating HTTP 429
		/// (and 403 quota-exceeded) responses as <see cref="GoogleSheetsErrorType.RateLimit"/>.
		/// </summary>
		public static GoogleSheetsClientException FromGoogleApiException(GoogleSheetsErrorType defaultErrorType, string message, Google.GoogleApiException ex)
		{
			var errorType = defaultErrorType;
			if ((int)ex.HttpStatusCode == 429 ||
				(ex.HttpStatusCode == System.Net.HttpStatusCode.Forbidden && ex.Message != null && ex.Message.IndexOf("rate", StringComparison.OrdinalIgnoreCase) >= 0))
			{
				errorType = GoogleSheetsErrorType.RateLimit;
			}

			return new GoogleSheetsClientException(errorType, message, ex);
		}
	}
}
