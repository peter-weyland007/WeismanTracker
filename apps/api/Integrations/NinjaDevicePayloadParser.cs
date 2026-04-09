using System.Globalization;
using System.Text.Json;

namespace api.Integrations;

public static class NinjaDevicePayloadParser
{
    public static (List<NinjaDeviceDto> Devices, string? NextPageToken) ParseDevices(string json)
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
                if (TryGetPropertyIgnoreCase(root, candidate, out var arr) && arr.ValueKind == JsonValueKind.Array)
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
                var device = ParseDevice(item);
                if (!string.IsNullOrWhiteSpace(device.ExternalId))
                {
                    devices.Add(device);
                }
            }
        }

        var nextPageToken = root.ValueKind == JsonValueKind.Object
            ? ReadString(root, "nextPageToken", "nextToken", "cursor")
            : null;

        return (devices, nextPageToken);
    }

    public static NinjaDeviceDto ParseDevice(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return ParseDevice(doc.RootElement);
    }

    public static NinjaDeviceDto Merge(NinjaDeviceDto summary, NinjaDeviceDto? detail)
    {
        if (detail is null)
        {
            return summary;
        }

        return new NinjaDeviceDto(
            ExternalId: Choose(detail.ExternalId, summary.ExternalId) ?? string.Empty,
            DeviceName: Choose(detail.DeviceName, summary.DeviceName),
            SerialNumber: ChooseMeaningfulSerial(detail.SerialNumber, summary.SerialNumber),
            Hostname: Choose(detail.Hostname, summary.Hostname),
            Os: Choose(detail.Os, summary.Os),
            Manufacturer: Choose(detail.Manufacturer, summary.Manufacturer),
            Model: Choose(detail.Model, summary.Model),
            DeviceType: Choose(detail.DeviceType, summary.DeviceType),
            ChassisType: Choose(detail.ChassisType, summary.ChassisType),
            LastSeenAtUtc: detail.LastSeenAtUtc ?? summary.LastSeenAtUtc);
    }

    private static NinjaDeviceDto ParseDevice(JsonElement item)
    {
        var externalId = ReadString(item, "id", "deviceId", "guid", "uid") ?? string.Empty;

        return new NinjaDeviceDto(
            ExternalId: externalId,
            DeviceName: ReadString(item, "displayName", "name", "deviceName", "systemName", "system.name"),
            SerialNumber: ReadMeaningfulSerial(
                item,
                "serialNumber",
                "serial",
                "serialNo",
                "system.serialNumber",
                "system.serial",
                "system.biosSerialNumber",
                "hardware.serialNumber",
                "hardware.serial",
                "bios.serialNumber",
                "bios.systemSerialNumber"),
            Hostname: ReadString(item, "hostname", "dnsName", "computerName", "system.name"),
            Os: ReadString(item, "os.name", "operatingSystem", "os", "nodeClass"),
            Manufacturer: ReadString(
                item,
                "manufacturer",
                "vendor",
                "system.manufacturer",
                "hardware.manufacturer",
                "system.systemManufacturer"),
            Model: ReadString(
                item,
                "model",
                "modelName",
                "system.model",
                "hardware.model",
                "system.productName",
                "hardware.productName"),
            DeviceType: ReadString(
                item,
                "deviceType",
                "deviceClass",
                "type",
                "class",
                "hardware.deviceType",
                "system.deviceType",
                "system.formFactor"),
            ChassisType: ReadString(
                item,
                "chassisType",
                "formFactor",
                "chassis",
                "system.chassisType",
                "system.formFactor",
                "hardware.chassisType",
                "hardware.formFactor"),
            LastSeenAtUtc: ReadDateTime(item, "lastSeen", "lastSeenAt", "lastContact", "lastUpdate"));
    }

    private static string? ReadString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryResolvePath(element, name, out var value))
            {
                if (value.ValueKind == JsonValueKind.String)
                {
                    return value.GetString();
                }

                if (value.ValueKind == JsonValueKind.Number || value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False)
                {
                    return value.GetRawText();
                }
            }
        }

        return null;
    }

    private static DateTime? ReadDateTime(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryResolvePath(element, name, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number)
            {
                if (value.TryGetInt64(out var seconds))
                {
                    return DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
                }

                if (value.TryGetDouble(out var secondsDouble))
                {
                    return DateTimeOffset.FromUnixTimeMilliseconds((long)(secondsDouble * 1000)).UtcDateTime;
                }
            }

            if (value.ValueKind == JsonValueKind.String)
            {
                var raw = value.GetString();
                if (DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
                {
                    return parsed;
                }

                if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var epochSeconds))
                {
                    return DateTimeOffset.FromUnixTimeMilliseconds((long)(epochSeconds * 1000)).UtcDateTime;
                }
            }
        }

        return null;
    }

    private static string? ReadMeaningfulSerial(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            var normalized = NormalizeSerial(ReadString(element, name));
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                return normalized;
            }
        }

        return null;
    }

    private static string? NormalizeSerial(string? raw)
    {
        var trimmed = raw?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return null;
        }

        if (trimmed.Equals("none", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("unknown", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("system serial number", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("chassis serial number", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("to be filled by o.e.m.", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("default string", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return trimmed;
    }

    private static string? Choose(string? preferred, string? fallback)
        => !string.IsNullOrWhiteSpace(preferred) ? preferred : fallback;

    private static string? ChooseMeaningfulSerial(string? preferred, string? fallback)
        => NormalizeSerial(preferred) ?? NormalizeSerial(fallback);

    private static bool TryResolvePath(JsonElement element, string path, out JsonElement value)
    {
        value = element;
        foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!TryGetPropertyIgnoreCase(value, segment, out value))
            {
                value = default;
                return false;
            }
        }

        return true;
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            value = default;
            return false;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (property.NameEquals(name) || property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}
