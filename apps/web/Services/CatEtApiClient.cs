using System.Net.Http.Json;
using System.Text.Json;
using web.Models;

namespace web.Services;

public class CatEtApiClient(IHttpClientFactory httpClientFactory)
{
    private readonly HttpClient _http = httpClientFactory.CreateClient("WeismanApi");

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

        return await _http.GetFromJsonAsync<PagedResultDto<TrackedPersonDto>>(url)
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
        var response = await _http.PostAsJsonAsync("/api/catet/people", request);
        if (response.IsSuccessStatusCode)
        {
            return (true, null);
        }

        return (false, await ReadErrorAsync(response));
    }

    public async Task<(bool Success, string? Error)> UpdatePersonAsync(int id, CreateTrackedPersonRequest request)
    {
        var response = await _http.PutAsJsonAsync($"/api/catet/people/{id}", request);
        if (response.IsSuccessStatusCode)
        {
            return (true, null);
        }

        return (false, await ReadErrorAsync(response));
    }

    public async Task<(bool Success, string? Error)> DeletePersonAsync(int id)
    {
        var response = await _http.DeleteAsync($"/api/catet/people/{id}");
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

        return await _http.GetFromJsonAsync<PagedResultDto<TrackedComputerDto>>(url)
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

    public async Task<(bool Success, string? Error)> CreateComputerAsync(CreateTrackedComputerRequest request)
    {
        var response = await _http.PostAsJsonAsync("/api/catet/computers", request);
        if (response.IsSuccessStatusCode)
        {
            return (true, null);
        }

        return (false, await ReadErrorAsync(response));
    }

    public async Task<(bool Success, string? Error)> UpdateComputerAsync(int id, CreateTrackedComputerRequest request)
    {
        var response = await _http.PutAsJsonAsync($"/api/catet/computers/{id}", request);
        if (response.IsSuccessStatusCode)
        {
            return (true, null);
        }

        return (false, await ReadErrorAsync(response));
    }

    public async Task<(bool Success, string? Error)> DeleteComputerAsync(int id)
    {
        var response = await _http.DeleteAsync($"/api/catet/computers/{id}");
        if (response.IsSuccessStatusCode)
        {
            return (true, null);
        }

        return (false, await ReadErrorAsync(response));
    }

    public async Task<(bool Success, string? Error)> UpdateComputerFlagsAsync(int id, UpdateTrackedComputerFlagsRequest request)
    {
        var response = await _http.PutAsJsonAsync($"/api/catet/computers/{id}/flags", request);
        if (response.IsSuccessStatusCode)
        {
            return (true, null);
        }

        return (false, await ReadErrorAsync(response));
    }

    public async Task<IReadOnlyList<CatEtLicenseDto>> GetLicensesAsync()
        => await _http.GetFromJsonAsync<List<CatEtLicenseDto>>("/api/catet/licenses") ?? [];

    public async Task<(bool Success, string? Error)> CreateLicenseAsync(CreateCatEtLicenseRequest request)
    {
        var response = await _http.PostAsJsonAsync("/api/catet/licenses", request);
        if (response.IsSuccessStatusCode)
        {
            return (true, null);
        }

        return (false, await ReadErrorAsync(response));
    }

    public async Task<(bool Success, string? Error)> UpdateLicenseAsync(int id, UpdateCatEtLicenseRequest request)
    {
        var response = await _http.PutAsJsonAsync($"/api/catet/licenses/{id}", request);
        if (response.IsSuccessStatusCode)
        {
            return (true, null);
        }

        return (false, await ReadErrorAsync(response));
    }

    public async Task<(bool Success, string? Error)> MarkActivatedAsync(int id)
    {
        var response = await _http.PostAsync($"/api/catet/licenses/{id}/activate", null);
        if (response.IsSuccessStatusCode)
        {
            return (true, null);
        }

        return (false, await ReadErrorAsync(response));
    }

    public async Task<IReadOnlyList<CatEtActivationEventDto>> GetLicenseEventsAsync(int id)
        => await _http.GetFromJsonAsync<List<CatEtActivationEventDto>>($"/api/catet/licenses/{id}/events") ?? [];

    public async Task<IReadOnlyList<CatEtActivationActivityRowDto>> GetActivityEventsAsync()
        => await _http.GetFromJsonAsync<List<CatEtActivationActivityRowDto>>("/api/catet/activity") ?? [];

    public async Task<(bool Success, string? Error)> ResetActivationAsync(int id, ResetActivationRequest request)
    {
        var response = await _http.PostAsJsonAsync($"/api/catet/licenses/{id}/reset", request);
        if (response.IsSuccessStatusCode)
        {
            return (true, null);
        }

        return (false, await ReadErrorAsync(response));
    }

    public async Task<(bool Success, string? Error)> DeleteLicenseAsync(int id)
    {
        var response = await _http.DeleteAsync($"/api/catet/licenses/{id}");
        if (response.IsSuccessStatusCode)
        {
            return (true, null);
        }

        return (false, await ReadErrorAsync(response));
    }

    public async Task<(bool Success, ImportCatEtLicensesResult? Result, string? Error)> ImportLicensesAsync(Stream fileStream, string fileName, bool forceOverwrite)
    {
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
        => await _http.GetFromJsonAsync<IntegrationSettingsDto>("/api/integrations/settings")
           ?? new IntegrationSettingsDto(
               new NinjaIntegrationConfigDto(string.Empty, string.Empty, false, "monitoring", "/ws/oauth/token", "/v2/devices", 200),
               new MicrosoftGraphIntegrationConfigDto(string.Empty, string.Empty, false, "https://graph.microsoft.com", "https://management.azure.com", 999, []));

    public async Task<(bool Success, string? Error)> SaveNinjaIntegrationSettingsAsync(UpdateNinjaIntegrationConfigRequest request)
    {
        var response = await _http.PutAsJsonAsync("/api/integrations/settings/ninja", request);
        if (response.IsSuccessStatusCode)
        {
            return (true, null);
        }

        return (false, await ReadErrorAsync(response));
    }

    public async Task<(bool Success, string? Error)> SaveMicrosoftGraphIntegrationSettingsAsync(UpdateMicrosoftGraphIntegrationConfigRequest request)
    {
        var response = await _http.PutAsJsonAsync("/api/integrations/settings/microsoft-graph", request);
        if (response.IsSuccessStatusCode)
        {
            return (true, null);
        }

        return (false, await ReadErrorAsync(response));
    }

    public async Task<IntegrationSyncStatusSnapshotDto> GetIntegrationSyncStatusAsync()
        => await _http.GetFromJsonAsync<IntegrationSyncStatusSnapshotDto>("/api/integrations/sync-status")
           ?? new IntegrationSyncStatusSnapshotDto(
               new IntegrationSyncStatusDto("Ninja", false, "Never", null, null, null, 0, 0, null, null, []),
               new IntegrationSyncStatusDto("Microsoft", false, "Never", null, null, null, 0, 0, null, null, []));

    public async Task<(bool Success, TriggerIntegrationSyncResponseDto? Result, string? Error)> TriggerNinjaSyncNowAsync()
    {
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
        => await _http.GetFromJsonAsync<List<DeletedRecordDto>>("/api/admin/deleted-records") ?? [];

    public async Task<(bool Success, string? Error)> HardDeleteRecordAsync(string recordType, int id)
    {
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
