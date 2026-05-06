namespace OpenFhirFirelyPlugin.OpenEhr;

public class CdrEntry
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public AuthMethod AuthMethod { get; set; } = AuthMethod.None;
    public BasicAuthConfig BasicAuth { get; set; } = new();
    public ClientCredentialsConfig OAuth2 { get; set; } = new();
}
