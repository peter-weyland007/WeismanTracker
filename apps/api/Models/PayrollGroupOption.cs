namespace api.Models;

public static class PayrollGroupOption
{
    public const int Hourly = 2;
    public const int Salary = 3;

    public static readonly IReadOnlyList<int> All = [Hourly, Salary];

    public static bool IsValid(int? value)
    {
        return value is Hourly or Salary;
    }

    public static string GetDisplayName(int? value)
    {
        return value switch
        {
            Hourly => "Hourly",
            Salary => "Salary",
            _ => string.Empty
        };
    }
}
