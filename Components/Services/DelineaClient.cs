using System;
using System.Configuration;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace DNNStuff.SQLViewPro.Services
{
	/// <summary>
	/// Minimal client for Delinea Secret Server's REST API, used to retrieve secret field
	/// values (e.g. the Google service-account JSON key) without storing them in this codebase.
	/// Configuration is read from web.config appSettings:
	/// DNNStuff:SQLViewPro:DelineaBaseUrl, DNNStuff:SQLViewPro:DelineaUsername,
	/// DNNStuff:SQLViewPro:DelineaPassword.
	/// </summary>
	public class DelineaClient
	{
		private const int TokenExpiryBufferSeconds = 60;

		private static readonly object TokenLock = new object();
		private static string _cachedAccessToken;
		private static DateTime _cachedTokenExpiresAtUtc = DateTime.MinValue;

		private readonly string _baseUrl;
		private readonly string _username;
		private readonly string _password;

		public DelineaClient() : this(
			ConfigurationManager.AppSettings["DNNStuff:SQLViewPro:DelineaBaseUrl"],
			ConfigurationManager.AppSettings["DNNStuff:SQLViewPro:DelineaUsername"],
			ConfigurationManager.AppSettings["DNNStuff:SQLViewPro:DelineaPassword"])
		{
		}

		public DelineaClient(string baseUrl, string username, string password)
		{
			if (string.IsNullOrEmpty(baseUrl))
			{
				throw new InvalidOperationException("DNNStuff:SQLViewPro:DelineaBaseUrl is not configured in web.config appSettings.");
			}
			if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
			{
				throw new InvalidOperationException("DNNStuff:SQLViewPro:DelineaUsername / DelineaPassword are not configured in web.config appSettings.");
			}

			_baseUrl = baseUrl;
			_username = username;
			_password = password;
		}

		/// <summary>
		/// Looks up a secret by name and returns the value of the field identified by
		/// <paramref name="fieldSlug"/> (e.g. "data" for a JSON-key style secret).
		/// </summary>
		public string GetFieldValue(string secretName, string fieldSlug)
		{
			return GetFieldValueAsync(secretName, fieldSlug).GetAwaiter().GetResult();
		}

		public async Task<string> GetFieldValueAsync(string secretName, string fieldSlug)
		{
			if (string.IsNullOrEmpty(secretName))
			{
				throw new ArgumentException("secretName must be provided.", "secretName");
			}
			if (string.IsNullOrEmpty(fieldSlug))
			{
				throw new ArgumentException("fieldSlug must be provided.", "fieldSlug");
			}

			var accessToken = await GetAccessTokenAsync().ConfigureAwait(false);

			using (var client = CreateHttpClient(accessToken))
			{
				var secretId = await FindSecretIdByNameAsync(client, secretName).ConfigureAwait(false);
				return await GetSecretFieldValueAsync(client, secretId, fieldSlug).ConfigureAwait(false);
			}
		}

		private async Task<string> GetAccessTokenAsync()
		{
			lock (TokenLock)
			{
				if (_cachedAccessToken != null && DateTime.UtcNow < _cachedTokenExpiresAtUtc)
				{
					return _cachedAccessToken;
				}
			}

			using (var client = new HttpClient())
			{
				var form = new FormUrlEncodedContent(new[]
				{
					new System.Collections.Generic.KeyValuePair<string, string>("grant_type", "password"),
					new System.Collections.Generic.KeyValuePair<string, string>("username", _username),
					new System.Collections.Generic.KeyValuePair<string, string>("password", _password)
				});

				var response = await client.PostAsync(_baseUrl + "/oauth2/token", form).ConfigureAwait(false);
				var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

				if (!response.IsSuccessStatusCode)
				{
					throw new InvalidOperationException(string.Format("Delinea token request failed ({0}): {1}", (int)response.StatusCode, body));
				}

				var json = JObject.Parse(body);
				var accessToken = (string)json["access_token"];
				var expiresIn = (int?)json["expires_in"] ?? 0;

				lock (TokenLock)
				{
					_cachedAccessToken = accessToken;
					_cachedTokenExpiresAtUtc = DateTime.UtcNow.AddSeconds(Math.Max(0, expiresIn - TokenExpiryBufferSeconds));
				}

				return accessToken;
			}
		}

		private static async Task<int> FindSecretIdByNameAsync(HttpClient client, string secretName)
		{
			var url = string.Format("/api/v1/secrets?filter.searchText={0}", Uri.EscapeDataString(secretName));
			var response = await client.GetAsync(url).ConfigureAwait(false);
			var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

			if (!response.IsSuccessStatusCode)
			{
				throw new InvalidOperationException(string.Format("Delinea secret search failed ({0}): {1}", (int)response.StatusCode, body));
			}

			var json = JObject.Parse(body);
			var records = json["records"] as JArray;
			if (records == null || records.Count == 0)
			{
				throw new InvalidOperationException(string.Format("Delinea secret '{0}' was not found.", secretName));
			}

			return (int)records[0]["id"];
		}

		private static async Task<string> GetSecretFieldValueAsync(HttpClient client, int secretId, string fieldSlug)
		{
			var response = await client.GetAsync(string.Format("/api/v1/secrets/{0}", secretId)).ConfigureAwait(false);
			var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

			if (!response.IsSuccessStatusCode)
			{
				throw new InvalidOperationException(string.Format("Delinea secret fetch failed ({0}): {1}", (int)response.StatusCode, body));
			}

			var json = JObject.Parse(body);
			var items = json["items"] as JArray;
			if (items != null)
			{
				foreach (var item in items)
				{
					if (string.Equals((string)item["slug"], fieldSlug, StringComparison.OrdinalIgnoreCase))
					{
						return (string)item["itemValue"];
					}
				}
			}

			throw new InvalidOperationException(string.Format("Delinea secret {0} does not have a field with slug '{1}'.", secretId, fieldSlug));
		}

		private HttpClient CreateHttpClient(string accessToken)
		{
			var client = new HttpClient { BaseAddress = new Uri(_baseUrl) };
			client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
			return client;
		}
	}
}
