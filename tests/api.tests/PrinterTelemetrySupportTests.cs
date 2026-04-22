using api.Contracts;
using api.Models;
using api.Printers;
using Xunit;

namespace api.tests;

public class PrinterTelemetrySupportTests
{
    [Fact]
    public void ResolveIdentityKey_prefers_serial_number_then_hostname_then_ip()
    {
        var serialRequest = new IngestPrinterTelemetryRequest(
            CollectorId: "collector-a",
            CapturedAtUtc: DateTime.UtcNow,
            Printer: new PrinterIdentitySnapshotDto("Ricoh Front", "ricoh-front", "10.20.51.204", "Ricoh", "MP3353", "RCH123"),
            Status: null,
            Usage: null,
            Consumables: null);

        var hostnameRequest = serialRequest with
        {
            Printer = serialRequest.Printer with
            {
                SerialNumber = null
            }
        };

        var ipRequest = hostnameRequest with
        {
            Printer = hostnameRequest.Printer with
            {
                Hostname = null
            }
        };

        Assert.Equal("serial:rch123", PrinterTelemetrySupport.ResolveIdentityKey(serialRequest));
        Assert.Equal("host:ricoh-front", PrinterTelemetrySupport.ResolveIdentityKey(hostnameRequest));
        Assert.Equal("ip:10.20.51.204", PrinterTelemetrySupport.ResolveIdentityKey(ipRequest));
    }

    [Fact]
    public void ApplySnapshot_updates_record_with_usage_and_consumables()
    {
        var record = new PrinterTelemetryRecord();
        var capturedAtUtc = new DateTime(2026, 4, 21, 16, 45, 0, DateTimeKind.Utc);
        var nowUtc = new DateTime(2026, 4, 21, 16, 46, 0, DateTimeKind.Utc);
        var request = new IngestPrinterTelemetryRequest(
            CollectorId: "collector-a",
            CapturedAtUtc: capturedAtUtc,
            Printer: new PrinterIdentitySnapshotDto("Ricoh Front", "ricoh-front", "10.20.51.204", "Ricoh", "MP3353", "RCH123"),
            Status: new PrinterStatusSnapshotDto("online", null),
            Usage: new PrinterUsageSnapshotDto(123456, 120000, 3456),
            Consumables:
            [
                new PrinterConsumableSnapshotDto("Black Toner", 42, "ok"),
                new PrinterConsumableSnapshotDto("Waste Toner", null, "replace soon")
            ]);

        PrinterTelemetrySupport.ApplySnapshot(record, request, nowUtc);

        Assert.Equal("serial:rch123", record.IdentityKey);
        Assert.Equal("collector-a", record.CollectorId);
        Assert.Equal("Ricoh Front", record.Name);
        Assert.Equal("online", record.Status);
        Assert.Equal(123456, record.TotalPages);
        Assert.Equal(120000, record.MonoPages);
        Assert.Equal(3456, record.ColorPages);
        Assert.Equal(capturedAtUtc, record.LastCapturedAtUtc);
        Assert.Equal(nowUtc, record.LastIngestedAtUtc);
        Assert.Contains("Black Toner 42% (ok)", record.ConsumableSummary);
        Assert.Contains("Waste Toner replace soon", record.ConsumableSummary);

        var dto = PrinterTelemetrySupport.ToDto(record);
        Assert.Equal(2, dto.Consumables.Count);
        Assert.Equal("Black Toner", dto.Consumables[0].Name);
        Assert.Equal(42m, dto.Consumables[0].PercentRemaining);
    }
}
