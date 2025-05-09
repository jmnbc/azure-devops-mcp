using System.Net.Http.Headers;
using Azure.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.Services.Client;
using Microsoft.VisualStudio.Services.WebApi;

namespace AzureMcpAgents.Functions.Extensions;

public static class VssExtensions
{
    public static IServiceCollection AddVssConnection(this IServiceCollection services, IConfiguration configuration)
    {
        var orgUrl = configuration["Vss:OrgUrl"];
        var tenantId = configuration["Vss:TenantId"];
        var clientId = configuration["Vss:ClientId"];

        if (string.IsNullOrEmpty(orgUrl) || string.IsNullOrEmpty(tenantId) || string.IsNullOrEmpty(clientId))
        {
            throw new ArgumentException("VSS connection parameters are not set in the configuration.");
        }

        var vssConnection = CreateVssConnection(orgUrl, tenantId, clientId);
        services.AddSingleton(vssConnection);

        return services;
    }
    
    private static VssConnection CreateVssConnection(string orgUrl, string tenantId, string clientId)
    {
        var credentials = new VssAzureIdentityCredential(
            new DefaultAzureCredential(
                new DefaultAzureCredentialOptions
                {
                    TenantId = tenantId,
                    ManagedIdentityClientId = clientId,
                    ExcludeEnvironmentCredential = true // Excluding because EnvironmentCredential was not using correct identity when running in Visual Studio
                }
            )
        );

        var settings = VssClientHttpRequestSettings.Default.Clone();
        settings.UserAgent = AppUserAgent;

        var organizationUrl = new Uri(orgUrl);
        return new VssConnection(organizationUrl, credentials, settings);
    }
    
    private static List<ProductInfoHeaderValue> AppUserAgent { get; } = new()
    {
        new ProductInfoHeaderValue("Identity.ManagedIdentitySamples", "1.0"),
        new ProductInfoHeaderValue("(2-ConsoleApp-ManagedIdentity)")
    };
}