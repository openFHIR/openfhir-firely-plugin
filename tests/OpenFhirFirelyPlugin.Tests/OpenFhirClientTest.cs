using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenFhirFirelyPlugin.Configuration;
using OpenFhirFirelyPlugin.OpenFhir;
using Xunit;

namespace OpenFhirFirelyPlugin.Tests;

public class OpenFhirClientTest
{
    private sealed record RecordedRequest(string Path, string Query, string? ContentType, string? ReqId, string Body);

    private sealed class StubHandler : HttpMessageHandler
    {
        public Queue<(HttpStatusCode Status, string Body)> Responses { get; } = new();
        public List<RecordedRequest> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content == null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new RecordedRequest(
                request.RequestUri!.AbsolutePath,
                request.RequestUri.Query,
                request.Content?.Headers.ContentType?.MediaType,
                request.Headers.TryGetValues("x-req-id", out var reqIds) ? reqIds.First() : null,
                body));

            var (status, responseBody) = Responses.Count > 0
                ? Responses.Dequeue()
                : (HttpStatusCode.InternalServerError, string.Empty);
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/fhir+json")
            };
        }
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri("http://localhost") };
    }

    private readonly StubHandler _handler = new();
    private readonly OpenFhirClient _client;

    public OpenFhirClientTest()
    {
        _client = new OpenFhirClient(
            new StubHttpClientFactory(_handler),
            Options.Create(new OpenFhirOptions { BaseUrl = "http://localhost" }),
            NullLogger<OpenFhirClient>.Instance);
    }

    private void Stub(HttpStatusCode status, string body) => _handler.Responses.Enqueue((status, body));

    private RecordedRequest Recorded => _handler.Requests.Single();

    // ---------------------------------------------------------------- Convert

    [Fact]
    public async Task Convert_PostsBundleToToOpenEhrAndUnwrapsComposition()
    {
        Stub(HttpStatusCode.OK, ParametersWithComposition("{\"_type\": \"COMPOSITION\"}"));

        var bundleJson = """{"resourceType":"Bundle","type":"transaction"}""";
        var result = await _client.Convert(bundleJson, "req-1");

        Assert.Equal("{\"_type\": \"COMPOSITION\"}", result);
        Assert.Equal("/$toopenehr", Recorded.Path);
        Assert.Equal("?format=canonical", Recorded.Query);
        Assert.Equal("application/fhir+json", Recorded.ContentType);
        Assert.Equal("req-1", Recorded.ReqId);
        Assert.Equal(bundleJson, Recorded.Body);
    }

    [Fact]
    public async Task Convert_ToleratesOutcomeParameter()
    {
        Stub(HttpStatusCode.OK, """
            {"resourceType":"Parameters","parameter":[
              {"name":"composition","valueString":"COMPO"},
              {"name":"outcome","resource":{"resourceType":"OperationOutcome","issue":[
                {"severity":"warning","code":"processing","diagnostics":"partial mapping"}]}}
            ]}
            """);

        Assert.Equal("COMPO", await _client.Convert("{\"resourceType\":\"Bundle\"}", "req-2"));
    }

    [Fact]
    public async Task Convert_ThrowsWhenCompositionParameterMissing()
    {
        Stub(HttpStatusCode.OK, """{"resourceType":"Parameters"}""");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _client.Convert("{\"resourceType\":\"Bundle\"}", "req-3"));
        Assert.Contains("no composition", ex.Message);
    }

    [Fact]
    public async Task Convert_ErrorIncludesOperationOutcomeDiagnostics()
    {
        Stub(HttpStatusCode.BadRequest, """
            {"resourceType":"OperationOutcome","issue":[{"code":"invalid","diagnostics":"bad payload"}]}
            """);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _client.Convert("{\"resourceType\":\"Bundle\"}", "req-4"));
        Assert.Contains("400", ex.Message);
        Assert.Contains("bad payload", ex.Message);
    }

    // ----------------------------------------------------------------- ToFhir

    [Fact]
    public async Task ToFhir_SendsParametersWithCompositionTemplateIdAndContext()
    {
        Stub(HttpStatusCode.OK, BundleOf(Patient("p1")));

        var rows = new[] { JsonSerializer.Deserialize<JsonElement>("{\"a\":1}") };
        await _client.ToFhir(rows, "req-5", "IPS template", "ehr-123", "Patient/42");

        Assert.Equal("/$tofhir", Recorded.Path);
        Assert.Equal(string.Empty, Recorded.Query);
        Assert.Equal("application/fhir+json", Recorded.ContentType);

        using var sent = JsonDocument.Parse(Recorded.Body);
        Assert.Equal("Parameters", sent.RootElement.GetProperty("resourceType").GetString());
        Assert.Equal("[{\"a\":1}]", Param(sent.RootElement, "composition")?.GetProperty("valueString").GetString());
        Assert.Equal("IPS template", Param(sent.RootElement, "templateId")?.GetProperty("valueString").GetString());
        var context = Param(sent.RootElement, "context");
        Assert.NotNull(context);
        Assert.Equal("ehr-123", Part(context.Value, "ehr_id")?.GetProperty("valueString").GetString());
        Assert.Equal("Patient/42",
            Part(context.Value, "patient")?.GetProperty("valueReference").GetProperty("reference").GetString());
    }

    [Fact]
    public async Task ToFhir_OmitsBlankTemplateId()
    {
        Stub(HttpStatusCode.OK, BundleOf(Patient("p1")));

        await _client.ToFhir(Array.Empty<JsonElement>(), "req-6", " ", "ehr-123", "Patient/42");

        using var sent = JsonDocument.Parse(Recorded.Body);
        Assert.Null(Param(sent.RootElement, "templateId"));
        Assert.NotNull(Param(sent.RootElement, "composition"));
    }

    [Fact]
    public async Task ToFhir_OmitsContextWhenEhrIdAndPatientBlank()
    {
        Stub(HttpStatusCode.OK, BundleOf(Patient("p1")));

        await _client.ToFhir(Array.Empty<JsonElement>(), "req-6b", "IPS template", null, " ");

        using var sent = JsonDocument.Parse(Recorded.Body);
        Assert.Null(Param(sent.RootElement, "context"));
    }

    [Fact]
    public async Task ToFhir_StripsEngineProvenanceAndOperationOutcome()
    {
        Stub(HttpStatusCode.OK, Bundle(
            "\"type\":\"searchset\",\"total\":3,",
            Patient("p1"),
            EngineProvenance(),
            """{"resourceType":"OperationOutcome","issue":[{"code":"processing","diagnostics":"mapping issue"}]}"""));

        var result = await _client.ToFhir(Array.Empty<JsonElement>(), "req-7", null);

        using var bundle = JsonDocument.Parse(result);
        var entries = bundle.RootElement.GetProperty("entry").EnumerateArray().ToList();
        Assert.Single(entries);
        Assert.Equal("Patient", entries[0].GetProperty("resource").GetProperty("resourceType").GetString());
        Assert.Equal(1, bundle.RootElement.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task ToFhir_KeepsUnmarkedProvenanceWhenMarkedOnePresent()
    {
        Stub(HttpStatusCode.OK, Bundle(null,
            Patient("p1"),
            """{"resourceType":"Provenance","id":"mapping"}""",
            EngineProvenance()));

        var result = await _client.ToFhir(Array.Empty<JsonElement>(), "req-8", null);

        using var bundle = JsonDocument.Parse(result);
        var entries = bundle.RootElement.GetProperty("entry").EnumerateArray().ToList();
        Assert.Equal(2, entries.Count);
        Assert.Equal("mapping", entries[1].GetProperty("resource").GetProperty("id").GetString());
    }

    [Fact]
    public async Task ToFhir_FallbackStripsTrailingUnmarkedProvenance()
    {
        Stub(HttpStatusCode.OK, Bundle(null,
            Patient("p1"),
            """{"resourceType":"Provenance","id":"trailing"}"""));

        var result = await _client.ToFhir(Array.Empty<JsonElement>(), "req-9", null);

        using var bundle = JsonDocument.Parse(result);
        var entries = bundle.RootElement.GetProperty("entry").EnumerateArray().ToList();
        Assert.Single(entries);
        Assert.Equal("Patient", entries[0].GetProperty("resource").GetProperty("resourceType").GetString());
    }

    [Fact]
    public async Task ToFhir_ErrorIncludesOperationOutcomeDiagnostics()
    {
        Stub(HttpStatusCode.InternalServerError, """
            {"resourceType":"OperationOutcome","issue":[{"code":"exception","diagnostics":"engine exploded"}]}
            """);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _client.ToFhir(Array.Empty<JsonElement>(), "req-10", null));
        Assert.Contains("500", ex.Message);
        Assert.Contains("engine exploded", ex.Message);
    }

    // ----------------------------------------------------------------- GetAql

    [Fact]
    public async Task GetAql_StillUsesLegacyToAqlEndpoint()
    {
        Stub(HttpStatusCode.OK,
            """{"aqls":[{"aql":"SELECT c FROM COMPOSITION c","type":"ENTRY","templateId":"T"}]}""");

        var response = await _client.GetAql(new ToAqlRequest("T", "ehr-123", "/Condition"), "req-11");

        Assert.Equal("/openfhir/toaql", Recorded.Path);
        Assert.Equal("application/json", Recorded.ContentType);
        Assert.NotNull(response.Aqls);
        Assert.Single(response.Aqls);
        Assert.Equal("SELECT c FROM COMPOSITION c", response.Aqls[0].Aql);
    }

    // ---------------------------------------------------------------- helpers

    private static string ParametersWithComposition(string composition) =>
        JsonSerializer.Serialize(new
        {
            resourceType = "Parameters",
            parameter = new[] { new { name = "composition", valueString = composition } }
        });

    private static string Patient(string id) => $$"""{"resourceType":"Patient","id":"{{id}}"}""";

    private static string BundleOf(string resourceJson) => Bundle(null, resourceJson);

    private static string Bundle(string? extraProps, params string[] resources) =>
        "{\"resourceType\":\"Bundle\"," + (extraProps ?? string.Empty) + "\"entry\":["
        + string.Join(",", resources.Select(r => "{\"resource\":" + r + "}")) + "]}";

    private static string EngineProvenance() => """
        {"resourceType":"Provenance","id":"engine","entity":[
          {"role":"source","what":{"identifier":{"system":"urn:openfhir:templateId","value":"IPS template"}}}]}
        """;

    private static JsonElement? Param(JsonElement parameters, string name) =>
        FindByName(parameters.GetProperty("parameter"), name);

    private static JsonElement? Part(JsonElement parameter, string name) =>
        FindByName(parameter.GetProperty("part"), name);

    private static JsonElement? FindByName(JsonElement array, string name)
    {
        foreach (var element in array.EnumerateArray())
        {
            if (element.TryGetProperty("name", out var n) && n.ValueEquals(name))
                return element;
        }
        return null;
    }
}
