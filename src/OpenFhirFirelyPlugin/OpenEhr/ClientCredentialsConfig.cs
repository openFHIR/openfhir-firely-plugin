namespace OpenFhirFirelyPlugin.OpenEhr;

public class ClientCredentialsConfig
{
    public string TokenUrl { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string Scope { get; set; } = string.Empty;

    /// <summary>How credentials are transmitted: "body" (default) or "basic".</summary>
    public string AuthMethod { get; set; } = "body";

    public Dictionary<string, string> ExtraParams { get; set; } = new();

    public bool IsConfigured() =>
        !string.IsNullOrWhiteSpace(TokenUrl) &&
        !string.IsNullOrWhiteSpace(ClientId) &&
        !string.IsNullOrWhiteSpace(ClientSecret);
}
