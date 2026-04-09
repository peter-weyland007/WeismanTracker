using api.Assets;
using api.Integrations;
using Xunit;

namespace api.tests;

public sealed class TrackedComputerMetadataTests
{
    [Theory]
    [InlineData("  abc-123  ", "ABC-123")]
    [InlineData("sn 9988", "SN 9988")]
    [InlineData(null, null)]
    [InlineData("   ", null)]
    public void NormalizeSerialNumber_trims_and_uppercases(string? raw, string? expected)
    {
        Assert.Equal(expected, TrackedComputerMetadata.NormalizeSerialNumber(raw));
    }

    [Theory]
    [InlineData("Dell Latitude 7450", null, null, "Laptop")]
    [InlineData("OPTIPLEX-001", null, null, "Desktop")]
    [InlineData(null, "Windows 11 Pro", "MacBook Pro", "Laptop")]
    [InlineData(null, "Windows 11 Pro", "Precision Tower 5820", "Desktop")]
    [InlineData(null, null, null, "Unknown")]
    public void InferVariant_detects_common_laptop_and_desktop_patterns(string? hostname, string? os, string? model, string expected)
    {
        Assert.Equal(expected, TrackedComputerMetadata.InferVariant(hostname, os, model));
    }

    [Theory]
    [InlineData("Notebook", null, null, "Unknown Workstation", "Laptop")]
    [InlineData("Rack Server", null, null, "Generic Host", "Server")]
    [InlineData(null, "Desktop", "Dell", "Latitude 7450", "Desktop")]
    [InlineData(null, "Virtual Machine", "Dell", "Latitude 7450", "Virtual Machine")]
    [InlineData(null, null, "Dell", "Latitude 7450", "Laptop")]
    public void InferVariantFromStructuredSignals_prioritizes_chassis_and_device_type_over_model_keywords(
        string? chassisType,
        string? deviceType,
        string? manufacturer,
        string? model,
        string expected)
    {
        Assert.Equal(expected, TrackedComputerMetadata.InferVariantFromStructuredSignals(chassisType, deviceType, manufacturer, model));
    }

    [Theory]
    [InlineData(null, "Laptop", "Laptop")]
    [InlineData("", "Desktop", "Desktop")]
    [InlineData("Unknown", "Laptop", "Laptop")]
    [InlineData("Desktop", "Laptop", "Desktop")]
    [InlineData("Laptop", "Desktop", "Laptop")]
    public void ChooseVariantForSync_preserves_existing_manual_value_when_present(string? existingVariant, string? detectedVariant, string expected)
    {
        Assert.Equal(expected, TrackedComputerMetadata.ChooseVariantForSync(existingVariant, detectedVariant));
    }

    [Fact]
    public void NinjaDevicePayloadParser_reads_nested_serial_manufacturer_model_and_variant_signals()
    {
        const string json = """
        {
          "items": [
            {
              "id": "device-1",
              "displayName": "WKS-001",
              "hostname": "WKS-001.contoso.local",
              "operatingSystem": "Windows 11 Pro",
              "system": {
                "manufacturer": "Dell",
                "model": "Latitude 7450",
                "chassisType": "Notebook",
                "serialNumber": "abc-123"
              },
              "deviceType": "Laptop"
            }
          ],
          "nextPageToken": "cursor-2"
        }
        """;

        var (devices, nextPageToken) = NinjaDevicePayloadParser.ParseDevices(json);

        var device = Assert.Single(devices);
        Assert.Equal("device-1", device.ExternalId);
        Assert.Equal("ABC-123", TrackedComputerMetadata.NormalizeSerialNumber(device.SerialNumber));
        Assert.Equal("Dell", device.Manufacturer);
        Assert.Equal("Latitude 7450", device.Model);
        Assert.Equal("Notebook", device.ChassisType);
        Assert.Equal("Laptop", device.DeviceType);
        Assert.Equal("cursor-2", nextPageToken);
    }

    [Fact]
    public void NinjaDevicePayloadParser_reads_list_endpoint_aliases_used_by_real_ninja_payloads()
    {
        const string json = """
        [
          {
            "id": 2,
            "systemName": "HUNTERDC01",
            "dnsName": "HunterDC01.hunterind.com",
            "nodeClass": "WINDOWS_SERVER",
            "lastContact": 1775761481.102
          }
        ]
        """;

        var (devices, nextPageToken) = NinjaDevicePayloadParser.ParseDevices(json);

        var device = Assert.Single(devices);
        Assert.Equal("2", device.ExternalId);
        Assert.Equal("HUNTERDC01", device.DeviceName);
        Assert.Equal("HunterDC01.hunterind.com", device.Hostname);
        Assert.Equal("WINDOWS_SERVER", device.Os);
        Assert.Null(nextPageToken);
    }

    [Fact]
    public void MergePrefersDetail_over_summary_when_ninja_detail_payload_contains_serial_and_system_metadata()
    {
        var summary = new NinjaDeviceDto(
            ExternalId: "2",
            DeviceName: "HUNTERDC01",
            SerialNumber: null,
            Hostname: "HunterDC01.hunterind.com",
            Os: "WINDOWS_SERVER",
            Manufacturer: null,
            Model: null,
            DeviceType: null,
            ChassisType: null,
            LastSeenAtUtc: null);

        const string json = """
        {
          "id": 2,
          "systemName": "HUNTERDC01",
          "dnsName": "HunterDC01.hunterind.com",
          "os": {
            "name": "Windows Server 2016 Standard Edition"
          },
          "system": {
            "manufacturer": "VMware, Inc.",
            "model": "VMware Virtual Platform",
            "biosSerialNumber": "vmware-serial",
            "serialNumber": "None",
            "virtualMachine": true,
            "chassisType": "UNKNOWN"
          },
          "deviceType": "AgentDevice",
          "lastContact": 1775761481.102
        }
        """;

        var detail = NinjaDevicePayloadParser.ParseDevice(json);
        var merged = NinjaDevicePayloadParser.Merge(summary, detail);

        Assert.Equal("vmware-serial", merged.SerialNumber);
        Assert.Equal("VMware, Inc.", merged.Manufacturer);
        Assert.Equal("VMware Virtual Platform", merged.Model);
        Assert.Equal("AgentDevice", merged.DeviceType);
        Assert.Equal("UNKNOWN", merged.ChassisType);
        Assert.Equal("Windows Server 2016 Standard Edition", merged.Os);
    }

    [Fact]
    public void ParseDevice_ignores_placeholder_serial_values_and_prefers_next_meaningful_serial()
    {
        const string json = """
        {
          "id": 42,
          "system": {
            "serialNumber": "Chassis Serial Number",
            "biosSerialNumber": "real-serial-42"
          }
        }
        """;

        var detail = NinjaDevicePayloadParser.ParseDevice(json);

        Assert.Equal("real-serial-42", detail.SerialNumber);
    }
}
