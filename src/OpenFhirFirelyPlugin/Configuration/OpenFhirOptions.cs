using OpenFhirFirelyPlugin.OpenEhr;

namespace OpenFhirFirelyPlugin.Configuration;

public class OpenFhirOptions
{
    public string BaseUrl { get; set; } = string.Empty;
    public ClientCredentialsConfig OAuth2 { get; set; } = new();
}
