using System.Net.Http.Headers;
using System.Text.Json;
using api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace api.Integrations;

public sealed class MicrosoftGraphClient : IMicrosoftGraphClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptionsMonitor<MicrosoftGraphOptions> _optionsMonitor;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MicrosoftGraphClient> _logger;
    private readonly SemaphoreSlim _graphTokenLock = new(1, 1);
    private readonly SemaphoreSlim _armTokenLock = new(1, 1);

    private string? _graphAccessToken;
    private DateTime _graphAccessTokenExpiresUtc = DateTime.MinValue;
    private string? _graphCacheKey;

    private string? _armAccessToken;
    private DateTime _armAccessTokenExpiresUtc = DateTime.MinValue;
    private string? _armCacheKey;

    public MicrosoftGraphClient(
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<MicrosoftGraphOptions> optionsMonitor,
        IServiceScopeFactory scopeFactory,
        ILogger<MicrosoftGraphClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _optionsMonitor = optionsMonitor;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<IReadOnlyList<GraphUserDto>> GetUsersAsync(CancellationToken cancellationToken)
    {
        var options = await ResolveOptionsAsync(cancellationToken);
        if (!IsConfigured(options))
        {
            _logger.LogInformation("Microsoft Graph integration not configured. Skipping user import.");
            return [];
        }

        var token = await GetGraphAccessTokenAsync(options, cancellationToken);
        if (string.IsNullOrWhiteSpace(token))
        {
            return [];
        }

        var endpoint = $"{options.GraphBaseUrl.TrimEnd('/')}/v1.0/users?$select=id,displayName,userPrincipalName,mail,mobilePhone,businessPhones&$top={Math.Max(1, options.PageSize)}";
        var rows = await GetPagedGraphRowsAsync(endpoint, token, cancellationToken);

        return rows
            .Select(x => new GraphUserDto(
                ExternalId: ReadString(x, "id") ?? string.Empty,
                UserPrincipalName: ReadString(x, "userPrincipalName"),
                Mail: ReadString(x, "mail"),
                DisplayName: ReadString(x, "displayName"),
                MobilePhone: ReadString(x, "mobilePhone"),
                BusinessPhone: ReadFirstStringFromArray(x, "businessPhones")))
            .Where(x => !string.IsNullOrWhiteSpace(x.ExternalId))
            .ToList();
    }

    public async Task<IReadOnlyList<GraphDeviceDto>> GetDevicesAsync(CancellationToken cancellationToken)
    {
        var options = await ResolveOptionsAsync(cancellationToken);
        if (!IsConfigured(options))
        {
            return [];
        }

        var token = await GetGraphAccessTokenAsync(options, cancellationToken);
        if (string.IsNullOrWhiteSpace(token))
        {
            return [];
        }

        var endpoint = $"{options.GraphBaseUrl.TrimEnd('/')}/v1.0/devices?$select=id,displayName,deviceId,serialNumber&$top={Math.Max(1, options.PageSize)}";
        var rows = await GetPagedGraphRowsAsync(endpoint, token, cancellationToken);

        return rows
            .Select(x => new GraphDeviceDto(
                ExternalId: ReadString(x, "id") ?? string.Empty,
                DisplayName: ReadString(x, "displayName"),
                DeviceId: ReadString(x, "deviceId"),
                SerialNumber: ReadString(x, "serialNumber")))
            .Where(x => !string.IsNullOrWhiteSpace(x.ExternalId))
            .ToList();
    }

    public async Task<IReadOnlyList<IntuneManagedDeviceDto>> GetManagedDevicesAsync(CancellationToken cancellationToken)
    {
        var options = await ResolveOptionsAsync(cancellationToken);
        if (!IsConfigured(options))
        {
            return [];
        }

        var token = await GetGraphAccessTokenAsync(options, cancellationToken);
        if (string.IsNullOrWhiteSpace(token))
        {
            return [];
        }

        var endpoint = $"{options.GraphBaseUrl.TrimEnd('/')}/v1.0/deviceManagement/managedDevices?$select=id,deviceName,azureADDeviceId,serialNumber&$top={Math.Max(1, options.PageSize)}";
        var rows = await GetPagedGraphRowsAsync(endpoint, token, cancellationToken);

        return rows
            .Select(x => new IntuneManagedDeviceDto(
                ExternalId: ReadString(x, "id") ?? string.Empty,
                DeviceName: ReadString(x, "deviceName"),
                AzureAdDeviceId: ReadString(x, "azureADDeviceId"),
                SerialNumber: ReadString(x, "serialNumber")))
            .Where(x => !string.IsNullOrWhiteSpace(x.ExternalId))
            .ToList();
    }

    public async Task<IReadOnlyList<AzureVmDto>> GetVirtualMachinesAsync(CancellationToken cancellationToken)
    {
        var options = await ResolveOptionsAsync(cancellationToken);
        if (!IsConfigured(options) || options.AzureSubscriptionIds.Count == 0)
        {
            return [];
        }

        var token = await GetArmAccessTokenAsync(options, cancellationToken);
        if (string.IsNullOrWhiteSpace(token))
        {
            return [];
        }

        var all = new List<AzureVmDto>();
        foreach (var subscriptionId in options.AzureSubscriptionIds.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            var endpoint = $"{options.ResourceManagerBaseUrl.TrimEnd('/')}/subscriptions/{subscriptionId.Trim()}/providers/Microsoft.Compute/virtualMachines?api-version=2024-03-01";
            var rows = await GetPagedArmRowsAsync(endpoint, token, cancellationToken);

            all.AddRange(rows
                .Select(x => new AzureVmDto(
                    ExternalId: ReadString(x, "id") ?? string.Empty,
                    Name: ReadString(x, "name"),
                    VmId: ReadNestedString(x, "properties", "vmId")))
                .Where(x => !string.IsNullOrWhiteSpace(x.ExternalId)));
        }

        return all;
    }

    private async Task<MicrosoftGraphOptions> ResolveOptionsAsync(CancellationToken cancellationToken)
    {
        var defaults = _optionsMonitor.CurrentValue;

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var config = await db.IntegrationProviderConfigs.AsNoTracking().FirstOrDefaultAsync(x => x.Provider == "MicrosoftGraph", cancellationToken);

        if (config is null)
        {
            return defaults;
        }

        var subscriptions = string.IsNullOrWhiteSpace(config.AzureSubscriptionIdsCsv)
            ? defaults.AzureSubscriptionIds
            : config.AzureSubscriptionIdsCsv
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

        return new MicrosoftGraphOptions
        {
            TenantId = config.TenantId ?? defaults.TenantId,
            ClientId = config.ClientId ?? defaults.ClientId,
            ClientSecret = config.ClientSecret ?? defaults.ClientSecret,
            GraphBaseUrl = config.BaseUrl ?? defaults.GraphBaseUrl,
            ResourceManagerBaseUrl = config.ResourceManagerBaseUrl ?? defaults.ResourceManagerBaseUrl,
            PageSize = config.PageSize is > 0 ? config.PageSize.Value : defaults.PageSize,
            AzureSubscriptionIds = subscriptions
        };
    }

    private static bool IsConfigured(MicrosoftGraphOptions options)
        => !string.IsNullOrWhiteSpace(options.TenantId)
           && !string.IsNullOrWhiteSpace(options.ClientId)
           && !string.IsNullOrWhiteSpace(options.ClientSecret);

    private async Task<string?> GetGraphAccessTokenAsync(MicrosoftGraphOptions options, CancellationToken cancellationToken)
    {
        var cacheKey = $"{options.TenantId}|{options.ClientId}|graph";
        if (_graphCacheKey != cacheKey)
        {
            _graphCacheKey = cacheKey;
            _graphAccessToken = null;
            _graphAccessTokenExpiresUtc = DateTime.MinValue;
        }

        if (!string.IsNullOrWhiteSpace(_graphAccessToken) && _graphAccessTokenExpiresUtc > DateTime.UtcNow.AddMinutes(1))
        {
            return _graphAccessToken;
        }

        await _graphTokenLock.WaitAsync(cancellationToken);
        try
        {
            if (!string.IsNullOrWhiteSpace(_graphAccessToken) && _graphAccessTokenExpiresUtc > DateTime.UtcNow.AddMinutes(1))
            {
                return _graphAccessToken;
            }

            var token = await RequestClientCredentialTokenAsync(
                tenantId: options.TenantId,
                clientId: options.ClientId,
                clientSecret: options.ClientSecret,
                scope: "https://graph.microsoft.com/.default",
                cancellationToken: cancellationToken);

            _graphAccessToken = token.AccessToken;
            _graphAccessTokenExpiresUtc = token.ExpiresAtUtc;
            return _graphAccessToken;
        }
        finally
        {
            _graphTokenLock.Release();
        }
    }

    private async Task<string?> GetArmAccessTokenAsync(MicrosoftGraphOptions options, CancellationToken cancellationToken)
    {
        var cacheKey = $"{options.TenantId}|{options.ClientId}|arm";
        if (_armCacheKey != cacheKey)
        {
            _armCacheKey = cacheKey;
            _armAccessToken = null;
            _armAccessTokenExpiresUtc = DateTime.MinValue;
        }

        if (!string.IsNullOrWhiteSpace(_armAccessToken) && _armAccessTokenExpiresUtc > DateTime.UtcNow.AddMinutes(1))
        {
            return _armAccessToken;
        }

        await _armTokenLock.WaitAsync(cancellationToken);
        try
        {
            if (!string.IsNullOrWhiteSpace(_armAccessToken) && _armAccessTokenExpiresUtc > DateTime.UtcNow.AddMinutes(1))
            {
                return _armAccessToken;
            }

            var token = await RequestClientCredentialTokenAsync(
                tenantId: options.TenantId,
                clientId: options.ClientId,
                clientSecret: options.ClientSecret,
                scope: "https://management.azure.com/.default",
                cancellationToken: cancellationToken);

            _armAccessToken = token.AccessToken;
            _armAccessTokenExpiresUtc = token.ExpiresAtUtc;
            return _armAccessToken;
        }
        finally
        {
            _armTokenLock.Release();
        }
    }

    private async Task<(string? AccessToken, DateTime ExpiresAtUtc)> RequestClientCredentialTokenAsync(
        string tenantId,
        string clientId,
        string clientSecret,
        string scope,
        CancellationToken cancellationToken)
    {
        var tokenUrl = $"https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/token";
        var client = _httpClientFactory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, tokenUrl)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
                ["scope"] = scope
            })
        };

        using var response = await client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Microsoft token request failed ({Scope}): {Code} {Body}", scope, (int)response.StatusCode, body);
            return (null, DateTime.MinValue);
        }

        using var json = JsonDocument.Parse(body);
        var accessToken = json.RootElement.TryGetProperty("access_token", out var tokenEl)
            ? tokenEl.GetString()
            : null;

        var expiresInSeconds = json.RootElement.TryGetProperty("expires_in", out var expiresEl)
            && expiresEl.ValueKind == JsonValueKind.Number
            && expiresEl.TryGetInt32(out var parsed)
            ? parsed
            : 3600;

        return (accessToken, DateTime.UtcNow.AddSeconds(Math.Max(60, expiresInSeconds)));
    }

    private async Task<List<JsonElement>> GetPagedGraphRowsAsync(string firstUrl, string accessToken, CancellationToken cancellationToken)
    {
        var rows = new List<JsonElement>();
        var url = firstUrl;

        while (!string.IsNullOrWhiteSpace(url))
        {
            using var doc = await GetJsonAsync(url, accessToken, cancellationToken);
            if (doc is null)
            {
                break;
            }

            if (doc.RootElement.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array)
            {
                rows.AddRange(value.EnumerateArray().Select(x => x.Clone()));
            }

            url = doc.RootElement.TryGetProperty("@odata.nextLink", out var next)
                ? next.GetString()
                : null;
        }

        return rows;
    }

    private async Task<List<JsonElement>> GetPagedArmRowsAsync(string firstUrl, string accessToken, CancellationToken cancellationToken)
    {
        var rows = new List<JsonElement>();
        var url = firstUrl;

        while (!string.IsNullOrWhiteSpace(url))
        {
            using var doc = await GetJsonAsync(url, accessToken, cancellationToken);
            if (doc is null)
            {
                break;
            }

            if (doc.RootElement.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array)
            {
                rows.AddRange(value.EnumerateArray().Select(x => x.Clone()));
            }

            url = doc.RootElement.TryGetProperty("nextLink", out var next)
                ? next.GetString()
                : null;
        }

        return rows;
    }

    private async Task<JsonDocument?> GetJsonAsync(string url, string accessToken, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("HTTP {Code} from {Url}: {Body}", (int)response.StatusCode, url, body);
            return null;
        }

        return JsonDocument.Parse(body);
    }

    private static string? ReadString(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
    }

    private static string? ReadNestedString(JsonElement obj, string parent, string child)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(parent, out var parentEl) || parentEl.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return ReadString(parentEl, child);
    }

    private static string? ReadFirstStringFromArray(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var text = item.GetString();
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        return null;
    }
}
