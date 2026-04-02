using System.Net.Http.Json;
using System.Text.Json;
using web.Models;

namespace web.Services;

public class CatEtApiClient(IHttpClientFactory httpClientFactory)
{
    private readonly HttpClient _http = httpClientFactory.CreateClient("WeismanApi");

    public async Task<IReadOnlyList<TrackedPersonDto>> GetPeopleAsync()
        => await _http.GetFromJsonAsync<List<TrackedPersonDto>>("/api/catet/people") ?? [];

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

    public async Task<IReadOnlyList<TrackedComputerDto>> GetComputersAsync()
        => await _http.GetFromJsonAsync<List<TrackedComputerDto>>("/api/catet/computers") ?? [];

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
