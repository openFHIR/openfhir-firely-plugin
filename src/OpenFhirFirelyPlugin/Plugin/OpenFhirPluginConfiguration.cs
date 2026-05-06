using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Vonk.Core.Context;
using Vonk.Core.Pluggability;
using Vonk.Core.Pluggability.ContextAware;
using OpenFhirFirelyPlugin.Configuration;
using OpenFhirFirelyPlugin.Ips;
using OpenFhirFirelyPlugin.Middleware;
using OpenFhirFirelyPlugin.OpenEhr;
using OpenFhirFirelyPlugin.OpenFhir;
using OpenFhirFirelyPlugin.Patient;
using OpenFhirFirelyPlugin.Pix;

namespace OpenFhirFirelyPlugin.Plugin;

[VonkConfiguration(order: 1116)]
public static class OpenFhirPluginConfiguration
{
    public static IServiceCollection ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        var logger = services.BuildServiceProvider().GetRequiredService<ILoggerFactory>()
            .CreateLogger("OpenFhirFirelyPlugin.Plugin.OpenFhirPluginConfiguration");
        logger.LogInformation("OpenFHIR plugin: registering services");

        services.Configure<InterceptorOptions>(configuration.GetSection("OpenFhirPlugin:Interceptor"));
        services.Configure<OpenFhirOptions>(configuration.GetSection("OpenFhirPlugin:OpenFhir"));

        var baseUrl = configuration["OpenFhirPlugin:OpenFhir:BaseUrl"];
        if (!string.IsNullOrWhiteSpace(baseUrl))
        {
            services.AddHttpClient("OpenFhir", client =>
            {
                client.BaseAddress = new Uri(baseUrl);
            });
        }
        else
        {
            services.AddHttpClient("OpenFhir");
        }

        services.TryAddScoped<OpenEhrCdrFileLoader>();
        services.TryAddScoped<OpenEhrCdrRegistry>();
        services.TryAddScoped<OpenFhirClient>();
        services.TryAddScoped<PixManager>();
        services.TryAddScoped<PatientCreatedHandler>();
        services.TryAddScoped<IpsSummaryService>();
        services.AddHttpContextAccessor();

        return services;
    }

    public static IApplicationBuilder Configure(IApplicationBuilder app)
    {
        var logger = app.ApplicationServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("OpenFhirFirelyPlugin.Plugin.OpenFhirPluginConfiguration");
        logger.LogInformation("OpenFHIR plugin: registering middleware and Vonk pipeline handlers");

        app.UseMiddleware<FhirCreateMiddleware>();
        app.UseMiddleware<FhirQueryMiddleware>();

        // Vonk pipeline: post-Patient-create EHR provisioning
        app.OnInteraction(VonkInteraction.type_create)
           .AndResourceTypes("Patient")
           .PostHandleAsyncWith<PatientCreatedHandler>((svc, ctx) => svc.OnPatientCreated(ctx));

        // Vonk pipeline: $summary custom operation on Patient instance
        app.OnCustomInteraction(VonkInteraction.instance_custom, "summary")
           .AndResourceTypes("Patient")
           .AndMethod("GET")
           .HandleAsyncWith<IpsSummaryService>((svc, ctx) => svc.ExecuteSummary(ctx));

        return app;
    }
}
