using System.Net.Http.Headers;
using System.Text.Json;
using api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace api.Integrations;

public sealed class NinjaClient : INinjaClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptionsMonitor<NinjaOptions> _optionsMonitor;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<NinjaClient> _logger;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    private string? _cachedAccessToken;
    private DateTime _accessTokenExpiresUtc = DateTime.MinValue;
    private string? _tokenCacheKey;

    public NinjaClient(
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<NinjaOptions> optionsMonitor,
        IServiceScopeFactory scopeFactory,
        ILogger<NinjaClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _optionsMonitor = optionsMonitor;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<IReadOnlyList<NinjaDeviceDto>> GetDevicesAsync(CancellationToken cancellationToken)
    {
        var options = await ResolveOptionsAsync(cancellationToken);
        if (!IsConfigured(options))
        {
            _logger.LogInformation("Ninja integration not configured. Skipping device import.");
            return [];
        }

        try
        {
            var token = await GetAccessTokenAsync(options, cancellationToken);
            if (string.IsNullOrWhiteSpace(token))
            {
                return [];
            }

            var devices = new List<NinjaDeviceDto>();
            string? pageToken = null;

            do
            {
                var uri = BuildDevicesUri(options, pageToken);
                var payload = await SendAuthorizedGetAsync(uri, token, cancellationToken);
                if (payload is null)
                {
                    break;
                }

                var (pageDevices, nextPageToken) = ParseDevices(payload);
                devices.AddRange(pageDevices);
                pageToken = nextPageToken;
            }
            while (!string.IsNullOrWhiteSpace(pageToken));

            _logger.LogInformation("Ninja device import fetched {Count} devices.", devices.Count);
            return devices;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ninja device import failed");
            return [];
        }
    }

    private async Task<NinjaOptions> ResolveOptionsAsync(CancellationToken cancellationToken)
    {
        var options = _optionsMonitor.CurrentValue;

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var dbConfig = await db.IntegrationProviderConfigs.AsNoTracking().FirstOrDefaultAsync(x => x.Provider == "Ninja", cancellationToken);

        if (dbConfig is null)
        {
            return options;
        }

        return new NinjaOptions
        {
            BaseUrl = dbConfig.BaseUrl ?? options.BaseUrl,
            ClientId = dbConfig.ClientId ?? options.ClientId,
            ClientSecret = dbConfig.ClientSecret ?? options.ClientSecret,
            Scope = dbConfig.Scope ?? options.Scope,
            TokenPath = dbConfig.TokenPath ?? options.TokenPath,
            DevicesPath = dbConfig.DevicesPath ?? options.DevicesPath,
            PageSize = dbConfig.PageSize is > 0 ? dbConfig.PageSize.Value : options.PageSize
        };
    }

    private static bool IsConfigured(NinjaOptions options)
        => !string.IsNullOrWhiteSpace(options.BaseUrl)
           && !string.IsNullOrWhiteSpace(options.ClientId)
           && !string.IsNullOrWhiteSpace(options.ClientSecret);

    private async Task<string?> GetAccessTokenAsync(NinjaOptions options, CancellationToken cancellationToken)
    {
        var cacheKey = $"{options.BaseUrl}|{options.ClientId}|{options.Scope}|{options.TokenPath}";
        if (_tokenCacheKey != cacheKey)
        {
            _cachedAccessToken = null;
            _accessTokenExpiresUtc = DateTime.MinValue;
            _tokenCacheKey = cacheKey;
        }

        if (!string.IsNullOrWhiteSpace(_cachedAccessToken) && _accessTokenExpiresUtc > DateTime.UtcNow.AddMinutes(1))
        {
            return _cachedAccessToken;
        }

        await _tokenLock.WaitAsync(cancellationToken);
        try
        {
            if (!string.IsNullOrWhiteSpace(_cachedAccessToken) && _accessTokenExpiresUtc > DateTime.UtcNow.AddMinutes(1))
            {
                return _cachedAccessToken;
            }

            var client = _httpClientFactory.CreateClient();
            var tokenUri = BuildUri(options.BaseUrl, options.TokenPath);

            using var request = new HttpRequestMessage(HttpMethod.Post, tokenUri)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials",
                    ["client_id"] = options.ClientId!,
                    ["client_secret"] = options.ClientSecret!,
                    ["scope"] = options.Scope
                })
            };

            using var response = await client.SendAsync(request, cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Ninja token request failed: {Code} {Body}", (int)response.StatusCode, responseBody);
                return null;
            }

            using var json = JsonDocument.Parse(responseBody);
            if (!json.RootElement.TryGetProperty("access_token", out var tokenEl))
            {
                _logger.LogWarning("Ninja token response missing access_token.");
                return null;
            }

            var expiresInSeconds = json.RootElement.TryGetProperty("expires_in", out var expiresEl)
                && expiresEl.ValueKind == JsonValueKind.Number
                && expiresEl.TryGetInt32(out var parsed)
                ? parsed
                : 3600;

            _cachedAccessToken = tokenEl.GetString();
            _accessTokenExpiresUtc = DateTime.UtcNow.AddSeconds(Math.Max(60, expiresInSeconds));
            return _cachedAccessToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private async Task<string?> SendAuthorizedGetAsync(Uri uri, string accessToken, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await client.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Ninja devices request failed: {Code} {Body}", (int)response.StatusCode, responseBody);
            return null;
        }

        return responseBody;
    }

    private static Uri BuildDevicesUri(NinjaOptions options, string? pageToken)
    {
        var path = options.DevicesPath.Contains('?')
            ? options.DevicesPath
            : $"{options.DevicesPath}?pageSize={Math.Max(1, options.PageSize)}";

        if (!string.IsNullOrWhiteSpace(pageToken))
        {
            path += $"&pageToken={Uri.EscapeDataString(pageToken)}";
        }

        return BuildUri(options.BaseUrl, path);
    }

    private static (List<NinjaDeviceDto> Devices, string? NextPageToken) ParseDevices(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        JsonElement deviceArray = default;
        var found = false;

        if (root.ValueKind == JsonValueKind.Array)
        {
            deviceArray = root;
            found = true;
        }
        else if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var candidate in new[] { "items", "data", "results", "devices" })
            {
                if (root.TryGetProperty(candidate, out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    deviceArray = arr;
                    found = true;
                    break;
                }
            }
        }

        var devices = new List<NinjaDeviceDto>();
        if (found)
        {
            foreach (var item in deviceArray.EnumerateArray())
            {
                var externalId = ReadString(item, "id", "deviceId", "guid") ?? string.Empty;
                if (string.IsNullOrWhiteSpace(externalId))
                {
                    continue;
                }

                devices.Add(new NinjaDeviceDto(
                    ExternalId: externalId,
                    DeviceName: ReadString(item, "displayName", "name", "deviceName"),
                    SerialNumber: ReadString(item, "serialNumber", "serial", "serialNo"),
                    Hostname: ReadString(item, "hostname", "dnsName", "computerName"),
                    Os: ReadString(item, "os", "operatingSystem"),
                    LastSeenAtUtc: ReadDateTime(item, "lastSeen", "lastSeenAt", "lastContact")));
            }
        }

        string? nextPageToken = null;
        if (root.ValueKind == JsonValueKind.Object)
        {
            nextPageToken = ReadString(root, "nextPageToken", "nextToken", "cursor");
        }

        return (devices, nextPageToken);
    }

    private static Uri BuildUri(string baseUrl, string relativeOrAbsolute)
    {
        if ((relativeOrAbsolute.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
             || relativeOrAbsolute.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            && Uri.TryCreate(relativeOrAbsolute, UriKind.Absolute, out var absoluteHttp))
        {
            return absoluteHttp;
        }

        var baseUri = new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/", UriKind.Absolute);
        return new Uri(baseUri, relativeOrAbsolute.TrimStart('/'));
    }

    private static string? ReadString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value))
            {
                if (value.ValueKind == JsonValueKind.String) return value.GetString();
                if (value.ValueKind == JsonValueKind.Number) return value.GetRawText();
            }
        }

        return null;
    }

    private static DateTime? ReadDateTime(JsonElement element, params string[] names)
    {
        var raw = ReadString(element, names);
        return DateTime.TryParse(raw, out var parsed) ? parsed.ToUniversalTime() : null;
    }
}
