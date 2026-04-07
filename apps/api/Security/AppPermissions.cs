using api.Models;

namespace api.Security;

public static class AppPermissions
{
    public const string People = "people";
    public const string Computers = "computers";
    public const string MobileDevices = "mobile-devices";
    public const string OtherDevices = "other-devices";
    public const string CellPhoneAllowance = "cell-phone-allowance";
    public const string ActivationKeys = "activation-keys";
    public const string ActivityTracker = "activity-tracker";
    public const string Integrations = "integrations";
    public const string HardDelete = "hard-delete";
    public const string UserAccess = "user-access";

    public static readonly IReadOnlyList<string> All =
    [
        People,
        Computers,
        MobileDevices,
        OtherDevices,
        CellPhoneAllowance,
        ActivationKeys,
        ActivityTracker,
        Integrations,
        HardDelete,
        UserAccess
    ];

    public static IReadOnlyList<string> DefaultForRole(UserRole role) => role switch
    {
        UserRole.Admin => All,
        UserRole.Manager =>
        [
            People,
            Computers,
            MobileDevices,
            OtherDevices,
            CellPhoneAllowance,
            ActivationKeys,
            ActivityTracker
        ],
        UserRole.Viewer =>
        [
            People,
            Computers,
            MobileDevices,
            OtherDevices,
            CellPhoneAllowance,
            ActivationKeys,
            ActivityTracker
        ],
        _ => []
    };
}
