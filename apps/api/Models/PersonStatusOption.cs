namespace api.Models;

public static class PersonStatusOption
{
    public const int Unknown = 0;
    public const int Fmla = 1;
    public const int Inactive = 2;
    public const int Active = 3;

    public static readonly IReadOnlyList<int> All = [Unknown, Fmla, Inactive, Active];
    public static readonly IReadOnlyList<int> CellPhoneAllowanceEligible = [Unknown, Active];

    public static bool IsValid(int? value)
    {
        return value is Unknown or Fmla or Inactive or Active;
    }

    public static bool IsEligibleForCellPhoneAllowance(int value)
    {
        return value is Unknown or Active;
    }

    public static string GetDisplayName(int value)
    {
        return value switch
        {
            Unknown => "Unknown",
            Fmla => "FMLA",
            Inactive => "Inactive",
            Active => "Active",
            _ => string.Empty
        };
    }
}
