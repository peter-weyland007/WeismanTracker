using System.Globalization;
using System.Text.Json;
using api.Contracts;
using api.Models;

namespace api.Printers;

public static class PrinterTelemetrySupport
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string ResolveIdentityKey(IngestPrinterTelemetryRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Printer);

        if (!string.IsNullOrWhiteSpace(request.Printer.SerialNumber))
        {
            return $"serial:{NormalizeKeyPart(request.Printer.SerialNumber)}";
        }

        if (!string.IsNullOrWhiteSpace(request.Printer.Hostname))
        {
            return $"host:{NormalizeKeyPart(request.Printer.Hostname)}";
        }

        if (!string.IsNullOrWhiteSpace(request.Printer.IpAddress))
        {
            return $"ip:{NormalizeKeyPart(request.Printer.IpAddress)}";
        }

        return $"name:{NormalizeKeyPart(request.Printer.Name)}";
    }

    public static void ApplySnapshot(PrinterTelemetryRecord record, IngestPrinterTelemetryRequest request, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Printer);

        var normalizedConsumables = NormalizeConsumables(request.Consumables);

        record.IdentityKey = ResolveIdentityKey(request);
        record.CollectorId = NullIfWhiteSpace(request.CollectorId);
        record.Name = FirstNonEmpty(request.Printer.Name, request.Printer.Hostname, request.Printer.IpAddress, record.Name, "Unnamed printer")!;
        record.Hostname = NullIfWhiteSpace(request.Printer.Hostname);
        record.IpAddress = NullIfWhiteSpace(request.Printer.IpAddress);
        record.Manufacturer = NullIfWhiteSpace(request.Printer.Manufacturer);
        record.Model = NullIfWhiteSpace(request.Printer.Model);
        record.SerialNumber = NullIfWhiteSpace(request.Printer.SerialNumber);
        record.Status = FirstNonEmpty(request.Status?.State, record.Status, "Unknown")!;
        record.CurrentAlert = NullIfWhiteSpace(request.Status?.Alert);
        record.TotalPages = request.Usage?.TotalPages;
        record.MonoPages = request.Usage?.MonoPages;
        record.ColorPages = request.Usage?.ColorPages;
        record.ConsumablesJson = normalizedConsumables.Count == 0 ? null : JsonSerializer.Serialize(normalizedConsumables, JsonOptions);
        record.ConsumableSummary = BuildConsumableSummary(normalizedConsumables);
        record.LastCapturedAtUtc = request.CapturedAtUtc;
        record.LastIngestedAtUtc = nowUtc;
        record.UpdatedAtUtc = nowUtc;
        if (record.CreatedAtUtc == default)
        {
            record.CreatedAtUtc = nowUtc;
        }
    }

    public static PrinterTelemetryDto ToDto(PrinterTelemetryRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        var consumables = DeserializeConsumables(record.ConsumablesJson);
        return new PrinterTelemetryDto(
            record.Id,
            record.CollectorId,
            record.Name,
            record.Hostname,
            record.IpAddress,
            record.Manufacturer,
            record.Model,
            record.SerialNumber,
            record.Status,
            record.CurrentAlert,
            record.TotalPages,
            record.MonoPages,
            record.ColorPages,
            string.IsNullOrWhiteSpace(record.ConsumableSummary) ? BuildConsumableSummary(consumables) : record.ConsumableSummary!,
            consumables,
            record.LastCapturedAtUtc,
            record.LastIngestedAtUtc);
    }

    public static IReadOnlyList<PrinterConsumableDto> DeserializeConsumables(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<PrinterConsumableDto>>(json, JsonOptions) ?? [];
        }
        catch
        {
            return [];
        }
    }

    public static string BuildConsumableSummary(IEnumerable<PrinterConsumableDto>? consumables)
    {
        if (consumables is null)
        {
            return "No consumable data";
        }

        var parts = consumables
            .Where(x => !string.IsNullOrWhiteSpace(x.Name))
            .Select(x =>
            {
                var statusPart = string.IsNullOrWhiteSpace(x.Status) ? null : x.Status!.Trim();
                var percentPart = x.PercentRemaining is null
                    ? null
                    : $"{x.PercentRemaining.Value.ToString("0.#", CultureInfo.InvariantCulture)}%";

                var detail = percentPart switch
                {
                    not null when statusPart is not null => $"{percentPart} ({statusPart})",
                    not null => percentPart,
                    _ => statusPart
                };

                return string.IsNullOrWhiteSpace(detail)
                    ? x.Name.Trim()
                    : $"{x.Name.Trim()} {detail}";
            })
            .ToList();

        return parts.Count == 0 ? "No consumable data" : string.Join(", ", parts);
    }

    private static List<PrinterConsumableDto> NormalizeConsumables(IEnumerable<PrinterConsumableSnapshotDto>? consumables)
        => consumables?
            .Where(x => !string.IsNullOrWhiteSpace(x.Name))
            .Select(x => new PrinterConsumableDto(x.Name.Trim(), x.PercentRemaining, NullIfWhiteSpace(x.Status)))
            .ToList()
        ?? [];

    private static string NormalizeKeyPart(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? "unknown"
            : value.Trim().ToLowerInvariant();

    private static string? NullIfWhiteSpace(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim();
}
