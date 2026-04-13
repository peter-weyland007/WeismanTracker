using ClosedXML.Excel;
using api.Models;

namespace api.Exports;

public sealed record CellPhoneAllowanceExportRow(
    string? EmployeeNumber,
    int? PayrollGroup,
    DateTime? ApprovedDate,
    string PersonName,
    string MobilePhoneNumber);

public static class CellPhoneAllowanceExcelExporter
{
    public static byte[] BuildWorkbook(IEnumerable<CellPhoneAllowanceExportRow> rows)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Cell Phone Allowance");

        sheet.Cell(1, 1).Value = "Employee Number";
        sheet.Cell(1, 2).Value = "Payroll Group";
        sheet.Cell(1, 3).Value = "Approved Date";
        sheet.Cell(1, 4).Value = "User";
        sheet.Cell(1, 5).Value = "Mobile Phone";

        var rowNumber = 2;
        foreach (var row in rows)
        {
            sheet.Cell(rowNumber, 1).Value = row.EmployeeNumber ?? string.Empty;
            sheet.Cell(rowNumber, 2).Value = row.PayrollGroup?.ToString() ?? string.Empty;
            sheet.Cell(rowNumber, 3).Value = row.ApprovedDate?.ToString("yyyy-MM-dd") ?? string.Empty;
            sheet.Cell(rowNumber, 4).Value = row.PersonName;
            sheet.Cell(rowNumber, 5).Value = row.MobilePhoneNumber;
            rowNumber++;
        }

        var headerRange = sheet.Range(1, 1, 1, 5);
        headerRange.Style.Font.Bold = true;
        headerRange.Style.Fill.BackgroundColor = XLColor.LightGray;
        sheet.Columns().AdjustToContents();
        sheet.SheetView.FreezeRows(1);

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }
}
