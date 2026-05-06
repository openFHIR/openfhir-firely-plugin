using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using OpenFhirFirelyPlugin.OpenEhr;

namespace OpenFhirFirelyPlugin.Auth;

/// <summary>
/// Fetches and caches an OAuth2 client_credentials access token. Thread-safe.
/// The token is refreshed automatically when it expires (with a 30-second safety margin).
/// </summary>
public class ClientCredentialsTokenProvider
{
    private const int ExpiryMarginSeconds = 30;

    private readonly ClientCredentialsConfig _config;
    private readonly IHttpClientFactory _httpClientFactory;

    private readonly object _lock = new();
    private string? _cachedToken;
    private DateTime _tokenExpiry = DateTime.MinValue;

    public ClientCredentialsTokenProvider(ClientCredentialsConfig config, IHttpClientFactory httpClientFactory)
    {
        _config = config;
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>Returns a valid Bearer token, fetching a new one when expired or near expiry.</summary>
    public string GetToken()
    {
        lock (_lock)
        {
            if (_cachedToken == null || DateTime.UtcNow >= _tokenExpiry.AddSeconds(-ExpiryMarginSeconds))
                FetchToken();
            return _cachedToken!;
        }
    }

    private void FetchToken()
    {
        var isBasic = string.Equals(_config.AuthMethod, "basic", StringComparison.OrdinalIgnoreCase);

        var formParams = new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials"
        };

        if (!isBasic)
        {
            formParams["client_id"] = _config.ClientId;
            formParams["client_secret"] = _config.ClientSecret;
        }

        if (!string.IsNullOrWhiteSpace(_config.Scope))
            formParams["scope"] = _config.Scope;

        foreach (var kv in _config.ExtraParams)
            formParams[kv.Key] = kv.Value;

        using var httpClient = _httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, _config.TokenUrl);
        request.Content = new FormUrlEncodedContent(formParams);

        if (isBasic)
        {
            var credentials = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{_config.ClientId}:{_config.ClientSecret}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        }

        var response = httpClient.Send(request);
        var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Token request failed with status {(int)response.StatusCode}: {body}");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        _cachedToken = root.GetProperty("access_token").GetString()
            ?? throw new InvalidOperationException("Token response missing access_token");

        var expiresIn = root.TryGetProperty("expires_in", out var expProp) ? expProp.GetInt64() : 3600L;
        _tokenExpiry = DateTime.UtcNow.AddSeconds(expiresIn);
    }
}
