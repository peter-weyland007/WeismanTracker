using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using web.Models;

namespace web.Services;

public class CatEtApiClient(IHttpClientFactory httpClientFactory, AuthTokenStore tokenStore)
{
    private readonly HttpClient _http = httpClientFactory.CreateClient("WeismanApi");

    private async Task<string?> ResolveTokenAsync()
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var token = await tokenStore.GetTokenAsync();
            if (!string.IsNullOrWhiteSpace(token))
            {
                return token;
            }

            if (!tokenStore.LastReadFailed)
            {
                return null;
            }

            await Task.Delay(100);
        }

        return await tokenStore.GetTokenAsync();
    }

    private async Task<HttpRequestMessage> CreateAuthedRequestAsync(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        var token = await ResolveTokenAsync();
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return request;
    }

    private async Task EnsureAuthorizationHeaderAsync()
    {
        var token = await ResolveTokenAsync();
        _http.DefaultRequestHeaders.Authorization = string.IsNullOrWhiteSpace(token)
            ? null
            : new AuthenticationHeaderValue("Bearer", token);
    }

    private async Task<T?> GetAuthedJsonAsync<T>(string url)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var request = await CreateAuthedRequestAsync(HttpMethod.Get, url);
            using var response = await _http.SendAsync(request);
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized || response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                if (attempt == 0)
                {
                    await Task.Delay(150);
                    continue;
                }

                await tokenStore.ClearTokenAsync();
                return default;
            }

            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<T>();
        }

        return default;
    }

    public async Task<LoginResponse?> LoginAsync(string username, string password)
    {
        var response = await _http.PostAsJsonAsync("/api/auth/login", new { Username = username, Password = password });
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return await response.Content.ReadFromJsonAsync<LoginResponse>();
    }

    public async Task<IReadOnlyList<string>> GetAvailablePermissionsAsync()
    {
        await EnsureAuthorizationHeaderAsync();
        return await GetAuthedJsonAsync<List<string>>("/api/auth/permissions") ?? [];
    }

    public async Task<ProfileDto?> GetProfileAsync()
    {
        await EnsureAuthorizationHeaderAsync();
        return await GetAuthedJsonAsync<ProfileDto>("/api/profile");
    }

    public async Task<(bool Success, string? Error)> ChangeOwnPasswordAsync(ChangeOwnPasswordRequest request)
    {
        await EnsureAuthorizationHeaderAsync();
        using var message = await CreateAuthedRequestAsync(HttpMethod.Post, "/api/profile/change-password");
        message.Content = JsonContent.Create(request);
        using var response = await _http.SendAsync(message);
        return response.IsSuccessStatusCode ? (true, null) : (false, await ReadErrorAsync(response));
    }

    public async Task<IReadOnlyList<PrinterTelemetryDto>> GetPrintersAsync()
    {
        return await GetAuthedJsonAsync<List<PrinterTelemetryDto>>("/api/printers") ?? [];
    }

    public async Task<IReadOnlyList<UserAccessDto>> GetUsersAsync()
    {
        await EnsureAuthorizationHeaderAsync();
        return await GetAuthedJsonAsync<List<UserAccessDto>>("/api/admin/users") ?? [];
    }

    public async Task<(bool Success, string? Error)> CreateUserAsync(CreateOrUpdateUserRequest request)
    {
        await EnsureAuthorizationHeaderAsync();
        using var message = await CreateAuthedRequestAsync(HttpMethod.Post, "/api/admin/users");
        message.Content = JsonContent.Create(request);
        var response = await _http.SendAsync(message);
        return response.IsSuccessStatusCode ? (true, null) : (false, await ReadErrorAsync(response));
    }

    public async Task<(bool Success, string? Error)> UpdateUserAsync(int id, CreateOrUpdateUserRequest request)
    {
        await EnsureAuthorizationHeaderAsync();
        using var message = await CreateAuthedRequestAsync(HttpMethod.Put, $"/api/admin/users/{id}");
        message.Content = JsonContent.Create(request);
        var response = await _http.SendAsync(message);
        return response.IsSuccessStatusCode ? (true, null) : (false, await ReadErrorAsync(response));
    }

    public async Task<PagedResultDto<TrackedPersonDto>> GetPeoplePageAsync(
        int page = 1,
        int pageSize = 50,
        string? search = null,
        string? sortBy = null,
        string? sortDir = null,
        string? filter = null)
    {
        var url = $"/api/catet/people?page={Math.Max(1, page)}&pageSize={Math.Clamp(pageSize, 1, 500)}";
        if (!string.IsNullOrWhiteSpace(search))
        {
            url += $"&search={Uri.EscapeDataString(search.Trim())}";
        }

        if (!string.IsNullOrWhiteSpace(sortBy))
        {
            url += $"&sortBy={Uri.EscapeDataString(sortBy.Trim())}";
        }

        if (!string.IsNullOrWhiteSpace(sortDir))
        {
            url += $"&sortDir={Uri.EscapeDataString(sortDir.Trim())}";
        }

        if (!string.IsNullOrWhiteSpace(filter))
        {
            url += $"&filter={Uri.EscapeDataString(filter.Trim())}";
        }

        return await GetAuthedJsonAsync<PagedResultDto<TrackedPersonDto>>(url)
            ?? new PagedResultDto<TrackedPersonDto>([], 0, Math.Max(1, page), Math.Clamp(pageSize, 1, 500));
    }

    public async Task<IReadOnlyList<TrackedPersonDto>> GetPeopleAsync()
    {
        const int pageSize = 500;
        var pageNumber = 1;
        var all = new List<TrackedPersonDto>();

        while (true)
        {
            var page = await GetPeoplePageAsync(page: pageNumber, pageSize: pageSize);
            all.AddRange(page.Items);

            if (all.Count >= page.TotalCount || page.Items.Count == 0)
            {
                break;
            }

            pageNumber++;
        }

        return all;
    }

    public async Task<(bool Success, string? Error)> CreatePersonAsync(CreateTrackedPersonRequest request)
    {
        await EnsureAuthorizationHeaderAsync();
        var response = await _http.PostAsJsonAsync("/api/catet/people", request);
        if (response.IsSuccessStatusCode)
        {
            return (true, null);
        }

        return (false, await ReadErrorAsync(response));
    }

    public async Task<(bool Success, string? Error)> UpdatePersonAsync(int id, CreateTrackedPersonRequest request)
    {
        await EnsureAuthorizationHeaderAsync();
        var response = await _http.PutAsJsonAsync($"/api/catet/people/{id}", request);
        if (response.IsSuccessStatusCode)
        {
            return (true, null);
        }

        return (false, await ReadErrorAsync(response));
    }

    public async Task<(bool Success, string? Error)> DeletePersonAsync(int id)
    {
        await EnsureAuthorizationHeaderAsync();
        var response = await _http.DeleteAsync($"/api/catet/people/{id}");
        if (response.IsSuccessStatusCode)
        {
            return (true, null);
        }

        return (false, await ReadErrorAsync(response));
    }

    public async Task<PagedResultDto<CellPhoneAllowanceDto>> GetCellPhoneAllowancesPageAsync(
        int page = 1,
        int pageSize = 50,
        string? search = null,
        string? sortBy = null,
        string? sortDir = null,
        string? filter = null)
    {
        var url = $"/api/allowances/cell-phone?page={Math.Max(1, page)}&pageSize={Math.Clamp(pageSize, 1, 500)}";
        if (!string.IsNullOrWhiteSpace(search))
        {
            url += $"&search={Uri.EscapeDataString(search.Trim())}";
        }

        if (!string.IsNullOrWhiteSpace(sortBy))
        {
            url += $"&sortBy={Uri.EscapeDataString(sortBy.Trim())}";
        }

        if (!string.IsNullOrWhiteSpace(sortDir))
        {
            url += $"&sortDir={Uri.EscapeDataString(sortDir.Trim())}";
        }

        if (!string.IsNullOrWhiteSpace(filter))
        {
            url += $"&filter={Uri.EscapeDataString(filter.Trim())}";
        }

        return await GetAuthedJsonAsync<PagedResultDto<CellPhoneAllowanceDto>>(url)
            ?? new PagedResultDto<CellPhoneAllowanceDto>([], 0, Math.Max(1, page), Math.Clamp(pageSize, 1, 500));
    }

    public async Task<(byte[] Content, string FileName)> ExportCellPhoneAllowancesAsync(string? search = null, string? filter = null)
    {
        var url = "/api/allowances/cell-phone/export";
        var query = new List<string>();

        if (!string.IsNullOrWhiteSpace(search))
        {
            query.Add($"search={Uri.EscapeDataString(search.Trim())}");
        }

        if (!string.IsNullOrWhiteSpace(filter))
        {
            query.Add($"filter={Uri.EscapeDataString(filter.Trim())}");
        }

        if (query.Count > 0)
        {
            url += $"?{string.Join("&", query)}";
        }

        using var request = await CreateAuthedRequestAsync(HttpMethod.Get, url);
        using var response = await _http.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsByteArrayAsync();
        var fileName = response.Content.Headers.ContentDisposition?.FileNameStar
            ?? response.Content.Headers.ContentDisposition?.FileName
            ?? $"cell-phone-allowance-{DateTime.UtcNow:yyyyMMddHHmmss}.xlsx";

        return (content, fileName.Trim('"'));
    }

    public async Task<(bool Success, string? Error)> CreateCellPhoneAllowanceAsync(CreateCellPhoneAllowanceRequest request)
    {
        await EnsureAuthorizationHeaderAsync();
        var response = await _http.PostAsJsonAsync("/api/allowances/cell-phone", request);
        if (response.IsSuccessStatusCode)
        {
            return (true, null);
        }

        return (false, await ReadErrorAsync(response));
    }

    public async Task<(bool Success, string? Error)> UpdateCellPhoneAllowanceAsync(int id, CreateCellPhoneAllowanceRequest request)
    {
        await EnsureAuthorizationHeaderAsync();
        var response = await _http.PutAsJsonAsync($"/api/allowances/cell-phone/{id}", request);
        if (response.IsSuccessStatusCode)
        {
            return (true, null);
        }

        return (false, await ReadErrorAsync(response));
    }

    public async Task<(bool Success, string? Error)> DeleteCellPhoneAllowanceAsync(int id)
    {
        await EnsureAuthorizationHeaderAsync();
        var response = await _http.DeleteAsync($"/api/allowances/cell-phone/{id}");
        if (response.IsSuccessStatusCode)
        {
            return (true, null);
        }

        return (false, await ReadErrorAsync(response));
    }

    public async Task<PagedResultDto<TrackedComputerDto>> GetComputersPageAsync(
        int page = 1,
        int pageSize = 50,
        string? search = null,
        string? sortBy = null,
        string? sortDir = null,
        string? filter = null,
        string? visibility = null,
        string? category = null)
    {
        var url = $"/api/catet/computers?page={Math.Max(1, page)}&pageSize={Math.Clamp(pageSize, 1, 500)}";
        if (!string.IsNullOrWhiteSpace(search))
        {
            url += $"&search={Uri.EscapeDataString(search.Trim())}";
        }

        if (!string.IsNullOrWhiteSpace(sortBy))
        {
            url += $"&sortBy={Uri.EscapeDataString(sortBy.Trim())}";
        }

        if (!string.IsNullOrWhiteSpace(sortDir))
        {
            url += $"&sortDir={Uri.EscapeDataString(sortDir.Trim())}";
        }

        if (!string.IsNullOrWhiteSpace(filter))
        {
            url += $"&filter={Uri.EscapeDataString(filter.Trim())}";
        }

        if (!string.IsNullOrWhiteSpace(visibility))
        {
            url += $"&visibility={Uri.EscapeDataString(visibility.Trim())}";
        }

        if (!string.IsNullOrWhiteSpace(category))
        {
            url += $"&category={Uri.EscapeDataString(category.Trim())}";
        }

        return await GetAuthedJsonAsync<PagedResultDto<TrackedComputerDto>>(url)
            ?? new PagedResultDto<TrackedComputerDto>([], 0, Math.Max(1, page), Math.Clamp(pageSize, 1, 500));
    }

    public async Task<IReadOnlyList<TrackedComputerDto>> GetComputersAsync()
    {
        const int pageSize = 500;
        var pageNumber = 1;
        var all = new List<TrackedComputerDto>();

        while (true)
        {
            var page = await GetComputersPageAsync(page: pageNumber, pageSize: pageSize);
            all.AddRange(page.Items);

            if (all.Count >= page.TotalCount || page.Items.Count == 0)
            {
                break;
            }

            pageNumber++;
        }

        return all;
    }

    public async Task<IReadOnlyList<TrackedComputerDto>> SearchLicenseAssignableComputersAsync(string? search, int maxResults = 50)
    {
        var page = await GetComputersPageAsync(
            page: 1,
            pageSize: Math.Clamp(maxResults, 1, 100),
            search: search,
            visibility: "all");

        return page.Items;
    }

    public async Task<IReadOnlyList<TrackedComputerDto>> GetLicenseAssignableComputersAsync()
    {
        await EnsureAuthorizationHeaderAsync();
        return await GetAuthedJsonAsync<List<TrackedComputerDto>>("/api/catet/licenses/assignable-computers") ?? [];
    }

    public async Task<(bool Success, string? Error)> CreateComputerAsync(CreateTrackedComputerRequest request)
    {
        await EnsureAuthorizationHeaderAsync();
        var response = await _http.PostAsJsonAsync("/api/catet/computers", request);
        if (response.IsSuccessStatusCode)
        {
            return (true, null);
        }

        return (false, await ReadErrorAsync(response));
    }

    public async Task<(bool Success, string? Error)> UpdateComputerAsync(int id, CreateTrackedComputerRequest request)
    {
        await EnsureAuthorizationHeaderAsync();
        var response = await _http.PutAsJsonAsync($"/api/catet/computers/{id}", request);
        if (response.IsSuccessStatusCode)
        {
            return (true, null);
        }

        return (false, await ReadErrorAsync(response));
    }

    public async Task<(bool Success, string? Error)> DeleteComputerAsync(int id)
    {
        await EnsureAuthorizationHeaderAsync();
        var response = await _http.DeleteAsync($"/api/catet/computers/{id}");
        if (response.IsSuccessStatusCode)
        {
            return (true, null);
        }

        return (false, await ReadErrorAsync(response));
    }

    public async Task<(bool Success, string? Error)> UpdateComputerFlagsAsync(int id, UpdateTrackedComputerFlagsRequest request)
    {
        await EnsureAuthorizationHeaderAsync();
        var response = await _http.PutAsJsonAsync($"/api/catet/computers/{id}/flags", request);
        if (response.IsSuccessStatusCode)
        {
            return (true, null);
        }

        return (false, await ReadErrorAsync(response));
    }

    public async Task<IReadOnlyList<CatEtLicenseDto>> GetLicensesAsync()
    {
        await EnsureAuthorizationHeaderAsync();
        return await GetAuthedJsonAsync<List<CatEtLicenseDto>>("/api/catet/licenses") ?? [];
    }

    public async Task<(bool Success, string? Error)> CreateLicenseAsync(CreateCatEtLicenseRequest request)
    {
        await EnsureAuthorizationHeaderAsync();
        var response = await _http.PostAsJsonAsync("/api/catet/licenses", request);
        if (response.IsSuccessStatusCode)
        {
            return (true, null);
        }

        return (false, await ReadErrorAsync(response));
    }

    public async Task<(bool Success, string? Error)> UpdateLicenseAsync(int id, UpdateCatEtLicenseRequest request)
    {
        await EnsureAuthorizationHeaderAsync();
        var response = await _http.PutAsJsonAsync($"/api/catet/licenses/{id}", request);
        if (response.IsSuccessStatusCode)
        {
            return (true, null);
        }

        return (false, await ReadErrorAsync(response));
    }

    public async Task<(bool Success, string? Error)> MarkActivatedAsync(int id)
    {
        await EnsureAuthorizationHeaderAsync();
        var response = await _http.PostAsync($"/api/catet/licenses/{id}/activate", null);
        if (response.IsSuccessStatusCode)
        {
            return (true, null);
        }

        return (false, await ReadErrorAsync(response));
    }

    public async Task<IReadOnlyList<CatEtActivationEventDto>> GetLicenseEventsAsync(int id)
    {
        await EnsureAuthorizationHeaderAsync();
        return await GetAuthedJsonAsync<List<CatEtActivationEventDto>>($"/api/catet/licenses/{id}/events") ?? [];
    }

    public async Task<IReadOnlyList<CatEtActivationActivityRowDto>> GetActivityEventsAsync()
    {
        await EnsureAuthorizationHeaderAsync();
        return await GetAuthedJsonAsync<List<CatEtActivationActivityRowDto>>("/api/catet/activity") ?? [];
    }

    public async Task<(bool Success, string? Error)> ResetActivationAsync(int id, ResetActivationRequest request)
    {
        await EnsureAuthorizationHeaderAsync();
        var response = await _http.PostAsJsonAsync($"/api/catet/licenses/{id}/reset", request);
        if (response.IsSuccessStatusCode)
        {
            return (true, null);
        }

        return (false, await ReadErrorAsync(response));
    }

    public async Task<(bool Success, string? Error)> DeleteLicenseAsync(int id)
    {
        await EnsureAuthorizationHeaderAsync();
        var response = await _http.DeleteAsync($"/api/catet/licenses/{id}");
        if (response.IsSuccessStatusCode)
        {
            return (true, null);
        }

        return (false, await ReadErrorAsync(response));
    }

    public async Task<(bool Success, ImportCatEtLicensesResult? Result, string? Error)> ImportLicensesAsync(Stream fileStream, string fileName, bool forceOverwrite)
    {
        await EnsureAuthorizationHeaderAsync();
        using var content = new MultipartFormDataContent();
        using var fileContent = new StreamContent(fileStream);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        content.Add(fileContent, "file", fileName);
        content.Add(new StringContent(forceOverwrite ? "true" : "false"), "forceOverwrite");

        var response = await _http.PostAsync("/api/catet/licenses/import", content);
        if (!response.IsSuccessStatusCode)
        {
            return (false, null, await ReadErrorAsync(response));
        }

        var result = await response.Content.ReadFromJsonAsync<ImportCatEtLicensesResult>();
        return result is null
            ? (false, null, "Import completed but response was empty.")
            : (true, result, null);
    }

    public async Task<IntegrationSettingsDto> GetIntegrationSettingsAsync()
    {
        await EnsureAuthorizationHeaderAsync();
        return await GetAuthedJsonAsync<IntegrationSettingsDto>("/api/integrations/settings")
           ?? new IntegrationSettingsDto(
               new NinjaIntegrationConfigDto(string.Empty, string.Empty, false, "monitoring", "/ws/oauth/token", "/v2/devices", 200),
               new MicrosoftGraphIntegrationConfigDto(string.Empty, string.Empty, false, "https://graph.microsoft.com", "https://management.azure.com", 999, []),
               new PrinterTelemetryIntegrationConfigDto(false, null));
    }

    public async Task<(bool Success, string? Error)> SavePrinterTelemetryIntegrationSettingsAsync(UpdatePrinterTelemetryIntegrationConfigRequest request)
    {
        await EnsureAuthorizationHeaderAsync();
        var response = await _http.PutAsJsonAsync("/api/integrations/settings/printer-telemetry", request);
        if (response.IsSuccessStatusCode)
        {
            return (true, null);
        }

        return (false, await ReadErrorAsync(response));
    }

    public async Task<(bool Success, string? Error)> SaveNinjaIntegrationSettingsAsync(UpdateNinjaIntegrationConfigRequest request)
    {
        await EnsureAuthorizationHeaderAsync();
        var response = await _http.PutAsJsonAsync("/api/integrations/settings/ninja", request);
        if (response.IsSuccessStatusCode)
        {
            return (true, null);
        }

        return (false, await ReadErrorAsync(response));
    }

    public async Task<(bool Success, string? Error)> SaveMicrosoftGraphIntegrationSettingsAsync(UpdateMicrosoftGraphIntegrationConfigRequest request)
    {
        await EnsureAuthorizationHeaderAsync();
        var response = await _http.PutAsJsonAsync("/api/integrations/settings/microsoft-graph", request);
        if (response.IsSuccessStatusCode)
        {
            return (true, null);
        }

        return (false, await ReadErrorAsync(response));
    }

    public async Task<IntegrationSyncStatusSnapshotDto> GetIntegrationSyncStatusAsync()
    {
        await EnsureAuthorizationHeaderAsync();
        return await GetAuthedJsonAsync<IntegrationSyncStatusSnapshotDto>("/api/integrations/sync-status")
           ?? new IntegrationSyncStatusSnapshotDto(
               new IntegrationSyncStatusDto("Ninja", false, "Never", null, null, null, 0, 0, null, null, []),
               new IntegrationSyncStatusDto("Microsoft", false, "Never", null, null, null, 0, 0, null, null, []));
    }

    public async Task<(bool Success, TriggerIntegrationSyncResponseDto? Result, string? Error)> TriggerNinjaSyncNowAsync()
    {
        await EnsureAuthorizationHeaderAsync();
        var response = await _http.PostAsync("/api/integrations/sync-now/ninja", null);
        if (!response.IsSuccessStatusCode)
        {
            return (false, null, await ReadErrorAsync(response));
        }

        var result = await response.Content.ReadFromJsonAsync<TriggerIntegrationSyncResponseDto>();
        return result is null
            ? (false, null, "Ninja sync triggered but response was empty.")
            : (true, result, null);
    }

    public async Task<(bool Success, TriggerIntegrationSyncResponseDto? Result, string? Error)> TriggerMicrosoftSyncNowAsync()
    {
        await EnsureAuthorizationHeaderAsync();
        var response = await _http.PostAsync("/api/integrations/sync-now/microsoft", null);
        if (!response.IsSuccessStatusCode)
        {
            return (false, null, await ReadErrorAsync(response));
        }

        var result = await response.Content.ReadFromJsonAsync<TriggerIntegrationSyncResponseDto>();
        return result is null
            ? (false, null, "Microsoft sync triggered but response was empty.")
            : (true, result, null);
    }

    public async Task<IReadOnlyList<DeletedRecordDto>> GetDeletedRecordsAsync()
    {
        await EnsureAuthorizationHeaderAsync();
        return await GetAuthedJsonAsync<List<DeletedRecordDto>>("/api/admin/deleted-records") ?? [];
    }

    public async Task<(bool Success, string? Error)> HardDeleteRecordAsync(string recordType, int id)
    {
        await EnsureAuthorizationHeaderAsync();
        var response = await _http.DeleteAsync($"/api/admin/{recordType}/{id}/hard-delete");
        if (response.IsSuccessStatusCode)
        {
            return (true, null);
        }

        return (false, await ReadErrorAsync(response));
    }

    private static async Task<string> ReadErrorAsync(HttpResponseMessage response)
    {
        var raw = await response.Content.ReadAsStringAsync();

        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("message", out var messageElement))
            {
                return messageElement.GetString() ?? "Request failed.";
            }
        }
        catch
        {
            // ignore parse failure and fall back below
        }

        return string.IsNullOrWhiteSpace(raw)
            ? $"Request failed with status {(int)response.StatusCode}."
            : raw;
    }
}
