using System.Security.Cryptography;
using api.Contracts;
using api.Data;
using Microsoft.EntityFrameworkCore;

namespace api.Printers;

public static class PrinterTelemetryCollectorSettingsSupport
{
    public const string ProviderName = "PrinterTelemetry";
    public const string HeaderName = "X-Printer-Collector-Key";
    public const string ConfigKey = "PrinterTelemetry:CollectorApiKey";

    public static async Task<string?> ResolveConfiguredApiKeyAsync(AppDbContext db, IConfiguration configuration, CancellationToken cancellationToken = default)
    {
        var dbConfig = await db.IntegrationProviderConfigs
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Provider == ProviderName, cancellationToken);

        if (!string.IsNullOrWhiteSpace(dbConfig?.ClientSecret))
        {
            return dbConfig.ClientSecret.Trim();
        }

        return configuration[ConfigKey]?.Trim();
    }

    public static async Task<PrinterTelemetryIntegrationConfigDto> GetConfigDtoAsync(AppDbContext db, IConfiguration configuration, CancellationToken cancellationToken = default)
    {
        var apiKey = await ResolveConfiguredApiKeyAsync(db, configuration, cancellationToken);
        return new(
            HasCollectorApiKey: !string.IsNullOrWhiteSpace(apiKey),
            CollectorApiKey: apiKey);
    }

    public static string GenerateApiKey()
        => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
}
