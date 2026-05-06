using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenFhirFirelyPlugin.Auth;
using OpenFhirFirelyPlugin.OpenFhir;

namespace OpenFhirFirelyPlugin.OpenEhr;

/// <summary>
/// HTTP executor for a single OpenEHR CDR instance.
/// Not a DI-registered class — created per-request by <see cref="OpenEhrCdrRegistry"/>.
/// </summary>
public class OpenEhrCdrClient
{
    private const string AqlPath = "/openehr/v1/query/aql";
    private const string StorePath = "/openehr/v1/ehr/{0}/composition";
    private const string CreateEhrPath = "/openehr/v1/ehr";

    private readonly CdrEntry _entry;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ClientCredentialsTokenProvider? _tokenProvider;
    private readonly string? _basicAuthHeader;

    public string CdrId => _entry.Id;

    public OpenEhrCdrClient(CdrEntry entry, IHttpClientFactory httpClientFactory)
    {
        _entry = entry;
        _httpClientFactory = httpClientFactory;

        if (entry.AuthMethod == AuthMethod.OAuth2 && entry.OAuth2.IsConfigured())
        {
            _tokenProvider = new ClientCredentialsTokenProvider(entry.OAuth2, httpClientFactory);
        }
        else if (entry.AuthMethod == AuthMethod.Basic && entry.BasicAuth.IsConfigured())
        {
            var credentials = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{entry.BasicAuth.Username}:{entry.BasicAuth.Password}"));
            _basicAuthHeader = $"Basic {credentials}";
        }
    }

    /// <summary>Creates a new EHR and returns the EHR ID from the Location header.</summary>
    public async Task<string> CreateEhr()
    {
        using var httpClient = CreateHttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, _entry.BaseUrl + CreateEhrPath);
        request.Content = new StringContent(string.Empty, Encoding.UTF8, "application/json");
        AddAuth(request);

        var response = await httpClient.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"OpenEHR CDR EHR creation failed with status {(int)response.StatusCode}: {body}");

        var location = response.Headers.Location?.ToString()
            ?? throw new InvalidOperationException("OpenEHR CDR EHR creation response missing Location header");

        var ehrId = location.Substring(location.LastIndexOf('/') + 1);
        return ehrId;
    }

    /// <summary>Stores an openEHR composition and returns the Location header value.</summary>
    public async Task<string?> Store(string openEhrPayload, string ehrId)
    {
        using var httpClient = CreateHttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post,
            _entry.BaseUrl + string.Format(StorePath, ehrId));
        request.Content = new StringContent(openEhrPayload, Encoding.UTF8, "application/json");
        request.Headers.Add("Prefer", "return=representation");
        AddAuth(request);

        var response = await httpClient.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"OpenEHR CDR store failed with status {(int)response.StatusCode}: {body}");

        return response.Headers.Location?.ToString();
    }

    /// <summary>Executes an AQL query and returns the raw JSON response body.</summary>
    public async Task<string> QueryAql(string aql)
    {
        using var httpClient = CreateHttpClient();
        var payload = JsonSerializer.Serialize(new AqlRequest(aql));
        using var request = new HttpRequestMessage(HttpMethod.Post, _entry.BaseUrl + AqlPath);
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        AddAuth(request);

        var response = await httpClient.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"OpenEHR CDR AQL query failed with status {(int)response.StatusCode}: {body}");

        return body;
    }

    private HttpClient CreateHttpClient() => _httpClientFactory.CreateClient();

    private void AddAuth(HttpRequestMessage request)
    {
        if (_tokenProvider != null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _tokenProvider.GetToken());
        }
        else if (_basicAuthHeader != null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", _basicAuthHeader);
        }
    }
}
