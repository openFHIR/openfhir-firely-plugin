extern alias FhirR5Asm;
extern alias FhirStu3Asm;
extern alias VonkR3;
extern alias VonkR5;

using Hl7.Fhir.Introspection;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;
using Vonk.Core.Common;

namespace OpenFhirFirelyPlugin;

/// <summary>
/// Version-aware helpers for FHIR serialization and Vonk IResource conversion.
/// Dispatches to the correct Vonk.Fhir.R* extension based on ctx.InformationModel.
/// </summary>
public static class FhirVersionHelper
{
    // Vonk InformationModel string constants (actual runtime values used by Firely Server)
    public const string FhirR3 = "Fhir3.0";
    public const string FhirR4 = "Fhir4.0";
    public const string FhirR5 = "Fhir5.0";

    // Resolve version-specific ModelInspector instances using ForAssembly on a sentinel type
    // from each version's aliased assembly.
#pragma warning disable CS0618 // ForAssembly is obsolete but necessary for multi-version support
    private static readonly ModelInspector InspectorR3 =
        ModelInspector.ForAssembly(typeof(FhirStu3Asm::Hl7.Fhir.Model.Patient).Assembly);

    private static readonly ModelInspector InspectorR4 =
        ModelInspector.ForAssembly(typeof(Hl7.Fhir.Model.Patient).Assembly);

    private static readonly ModelInspector InspectorR5 =
        ModelInspector.ForAssembly(typeof(FhirR5Asm::Hl7.Fhir.Model.Patient).Assembly);
#pragma warning restore CS0618

    // Use BaseFhirJsonSerializer/Deserializer (from Hl7.Fhir.Base) to avoid CS0433 ambiguity
    // with version-specific FhirJsonSerializer re-exports.
    private static readonly BaseFhirJsonSerializer SerializerR3 = new(InspectorR3);
    private static readonly BaseFhirJsonSerializer SerializerR4 = new(InspectorR4);
    private static readonly BaseFhirJsonSerializer SerializerR5 = new(InspectorR5);

    private static readonly BaseFhirJsonDeserializer DeserializerR3 = new(InspectorR3, new DeserializerSettings().UsingMode(DeserializationMode.SyntaxOnly));
    private static readonly BaseFhirJsonDeserializer DeserializerR4 = new(InspectorR4, new DeserializerSettings().UsingMode(DeserializationMode.SyntaxOnly));
    private static readonly BaseFhirJsonDeserializer DeserializerR5 = new(InspectorR5, new DeserializerSettings().UsingMode(DeserializationMode.SyntaxOnly));

    public static BaseFhirJsonSerializer GetSerializer(string? informationModel) =>
        informationModel switch
        {
            FhirR3 => SerializerR3,
            FhirR5 => SerializerR5,
            _      => SerializerR4,
        };

    public static BaseFhirJsonDeserializer GetDeserializer(string? informationModel) =>
        informationModel switch
        {
            FhirR3 => DeserializerR3,
            FhirR5 => DeserializerR5,
            _      => DeserializerR4,
        };

    /// <summary>
    /// Resolves the InformationModel string from the request's Content-Type / Accept header
    /// fhirVersion MIME parameter, falling back to the value Vonk already set on the context.
    /// This is needed in middleware that runs before Vonk has a chance to set InformationModel.
    /// </summary>
    public static string ResolveInformationModel(HttpRequest request, string? vonkInformationModel)
    {
        var fromHeader = FhirVersionFromHeaders(request);
        if (fromHeader != null)
            return fromHeader;
        return vonkInformationModel ?? FhirR4;
    }

    /// <summary>
    /// Parses the fhirVersion MIME parameter from Content-Type or Accept headers.
    /// Returns one of the FhirR3/FhirR4/FhirR5 constants, or null if not determinable.
    /// </summary>
    private static string? FhirVersionFromHeaders(HttpRequest request)
    {
        var fhirVersion = ParseFhirVersionParam(request.ContentType)
                       ?? ParseFhirVersionParam(request.Headers[HeaderNames.Accept].ToString());

        return fhirVersion switch
        {
            { } v when v.StartsWith("3.") => FhirR3,  // "3.0" → "Fhir3.0"
            { } v when v.StartsWith("4.") => FhirR4,  // "4.0" → "Fhir4.0"
            { } v when v.StartsWith("5.") => FhirR5,  // "5.0" → "Fhir5.0"
            _ => null,
        };
    }

    private static string? ParseFhirVersionParam(string? headerValue)
    {
        if (string.IsNullOrEmpty(headerValue)) return null;
        // e.g. "application/fhir+json; fhirVersion=3.0"
        foreach (var part in headerValue.Split(';'))
        {
            var trimmed = part.Trim();
            if (trimmed.StartsWith("fhirVersion=", StringComparison.OrdinalIgnoreCase))
                return trimmed.Substring("fhirVersion=".Length).Trim(' ', '"');
        }
        return null;
    }
}
