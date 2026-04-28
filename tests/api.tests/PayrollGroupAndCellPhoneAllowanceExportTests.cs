using ClosedXML.Excel;
using api.Exports;
using api.Models;
using Xunit;

namespace api.tests;

public sealed class PayrollGroupAndCellPhoneAllowanceExportTests
{
    [Theory]
    [InlineData(2, "Hourly")]
    [InlineData(3, "Salary")]
    public void PayrollGroupOption_returns_expected_display_name(int value, string expected)
    {
        Assert.Equal(expected, PayrollGroupOption.GetDisplayName(value));
    }

    [Theory]
    [InlineData(3, "Active")]
    [InlineData(2, "Inactive")]
    [InlineData(1, "FMLA")]
    [InlineData(0, "Unknown")]
    public void PersonStatusOption_returns_expected_display_name(int value, string expected)
    {
        Assert.Equal(expected, PersonStatusOption.GetDisplayName(value));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void PayrollGroupOption_accepts_supported_values(int value)
    {
        Assert.True(PayrollGroupOption.IsValid(value));
    }

    [Theory]
    [InlineData(PersonStatusOption.Active)]
    [InlineData(PersonStatusOption.Inactive)]
    [InlineData(PersonStatusOption.Fmla)]
    [InlineData(PersonStatusOption.Unknown)]
    public void PersonStatusOption_accepts_supported_values(int value)
    {
        Assert.True(PersonStatusOption.IsValid(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    public void PayrollGroupOption_rejects_unsupported_values(int? value)
    {
        Assert.False(PayrollGroupOption.IsValid(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(-1)]
    [InlineData(4)]
    public void PersonStatusOption_rejects_unsupported_values(int? value)
    {
        Assert.False(PersonStatusOption.IsValid(value));
    }

    [Theory]
    [InlineData(PersonStatusOption.Active, true)]
    [InlineData(PersonStatusOption.Unknown, true)]
    [InlineData(PersonStatusOption.Inactive, false)]
    [InlineData(PersonStatusOption.Fmla, false)]
    public void PersonStatusOption_reports_cell_phone_allowance_eligibility(int value, bool expected)
    {
        Assert.Equal(expected, PersonStatusOption.IsEligibleForCellPhoneAllowance(value));
    }

    [Fact]
    public void BuildWorkbook_includes_required_export_columns_and_values()
    {
        var approvedAt = new DateTime(2026, 4, 13);
        var rows = new[]
        {
            new CellPhoneAllowanceExportRow(
                "1001",
                PayrollGroupOption.Hourly,
                approvedAt,
                "Ada Lovelace",
                "555-0100")
        };

        var bytes = CellPhoneAllowanceExcelExporter.BuildWorkbook(rows);

        using var stream = new MemoryStream(bytes);
        using var workbook = new XLWorkbook(stream);
        var sheet = workbook.Worksheet(1);

        Assert.Equal("Employee Number", sheet.Cell(1, 1).GetString());
        Assert.Equal("Payroll Group", sheet.Cell(1, 2).GetString());
        Assert.Equal("Approved Date", sheet.Cell(1, 3).GetString());
        Assert.Equal("User", sheet.Cell(1, 4).GetString());
        Assert.Equal("Mobile Phone", sheet.Cell(1, 5).GetString());
        Assert.Equal(string.Empty, sheet.Cell(1, 6).GetString());

        Assert.Equal("1001", sheet.Cell(2, 1).GetString());
        Assert.Equal("2", sheet.Cell(2, 2).GetString());
        Assert.Equal("2026-04-13", sheet.Cell(2, 3).GetString());
        Assert.Equal("Ada Lovelace", sheet.Cell(2, 4).GetString());
        Assert.Equal("555-0100", sheet.Cell(2, 5).GetString());
    }
}
