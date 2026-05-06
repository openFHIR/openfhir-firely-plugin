using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenFhirFirelyPlugin.Auth;
using OpenFhirFirelyPlugin.Configuration;
using OpenFhirFirelyPlugin.OpenEhr;

namespace OpenFhirFirelyPlugin.OpenFhir;

public class OpenFhirClient
{
    private const string ToOpenEhrPath = "/openfhir/toopenehr";
    private const string ToAqlPath = "/openfhir/toaql";
    private const string ToFhirPath = "/openfhir/tofhir";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly OpenFhirOptions _options;
    private readonly ClientCredentialsTokenProvider? _tokenProvider;
    private readonly ILogger<OpenFhirClient> _logger;

    public OpenFhirClient(
        IHttpClientFactory httpClientFactory,
        IOptions<OpenFhirOptions> options,
        ILogger<OpenFhirClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;

        if (_options.OAuth2.IsConfigured())
            _tokenProvider = new ClientCredentialsTokenProvider(_options.OAuth2, httpClientFactory);
    }

    /// <summary>Converts a FHIR JSON resource to an openEHR composition via /openfhir/toopenehr.</summary>
    public async Task<string> Convert(string fhirJson, string reqId)
    {
        using var request = NewRequest(HttpMethod.Post, ToOpenEhrPath, reqId);
        request.Content = new StringContent(fhirJson, Encoding.UTF8, "application/json");

        using var httpClient = _httpClientFactory.CreateClient("OpenFhir");
        var response = await httpClient.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"OpenFHIR toopenehr failed with status {(int)response.StatusCode}: {body}");

        _logger.LogInformation("OpenFHIR toopenehr successful, status={Status}", (int)response.StatusCode);
        return body;
    }

    /// <summary>Gets AQL queries for the given FHIR path via /openfhir/toaql.</summary>
    public async Task<ToAqlResponse> GetAql(ToAqlRequest toAqlRequest, string reqId)
    {
        var payload = JsonSerializer.Serialize(toAqlRequest, JsonOptions);
        using var request = NewRequest(HttpMethod.Post, ToAqlPath, reqId);
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        using var httpClient = _httpClientFactory.CreateClient("OpenFhir");
        var response = await httpClient.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"OpenFHIR toaql failed with status {(int)response.StatusCode}: {body}");

        _logger.LogInformation("OpenFHIR toaql successful, status={Status}, reqId={ReqId}",
            (int)response.StatusCode, reqId);

        return JsonSerializer.Deserialize<ToAqlResponse>(body, JsonOptions)
               ?? new ToAqlResponse();
    }

    /// <summary>Converts openEHR archetype rows to FHIR resources via /openfhir/tofhir.</summary>
    public async Task<string> ToFhir(IReadOnlyList<JsonElement> rows, string reqId, string? templateId)
    {
        var payload = JsonSerializer.Serialize(rows);
        return await ToFhirRaw(payload, reqId, templateId);
    }

    private async Task<string> ToFhirRaw(string payload, string reqId, string? templateId)
    {
        var path = string.IsNullOrWhiteSpace(templateId)
            ? ToFhirPath
            : $"{ToFhirPath}?templateId={Uri.EscapeDataString(templateId)}";

        using var request = NewRequest(HttpMethod.Post, path, reqId);
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        using var httpClient = _httpClientFactory.CreateClient("OpenFhir");
        var response = await httpClient.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"OpenFHIR tofhir failed with status {(int)response.StatusCode}: {body}");

        _logger.LogInformation("OpenFHIR tofhir successful, status={Status}, reqId={ReqId}",
            (int)response.StatusCode, reqId);
        return body;
    }

    private HttpRequestMessage NewRequest(HttpMethod method, string path, string reqId)
    {
        var effectiveReqId = !string.IsNullOrWhiteSpace(reqId) ? reqId : Guid.NewGuid().ToString();
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("x-req-id", effectiveReqId);

        if (_tokenProvider != null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _tokenProvider.GetToken());

        return request;
    }
}
