using System.Security.Claims;
using System.Text.Json;
using api.Models;

namespace api.Security;

public static class AppUserPermissionExtensions
{
    public static IReadOnlyList<string> GetEffectivePermissions(this AppUser user)
    {
        if (user.Role == UserRole.Admin)
        {
            return AppPermissions.All;
        }

        var permissions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var permission in AppPermissions.DefaultForRole(user.Role))
        {
            permissions.Add(permission);
        }

        foreach (var permission in ParsePermissions(user.PermissionsJson))
        {
            permissions.Add(permission);
        }

        return permissions.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static bool HasPermission(this ClaimsPrincipal principal, string permission)
    {
        if (principal.IsInRole(UserRole.Admin.ToString()))
        {
            return true;
        }

        return principal.Claims.Any(c => c.Type == "perm" && string.Equals(c.Value, permission, StringComparison.OrdinalIgnoreCase));
    }

    public static string SerializePermissions(IEnumerable<string> permissions)
    {
        var normalized = permissions
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return JsonSerializer.Serialize(normalized);
    }

    public static IReadOnlyList<string> ParsePermissions(string? permissionsJson)
    {
        if (string.IsNullOrWhiteSpace(permissionsJson))
        {
            return [];
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<List<string>>(permissionsJson);
            return parsed?
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList() ?? [];
        }
        catch
        {
            return [];
        }
    }
}
