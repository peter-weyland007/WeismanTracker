using System.Security.Claims;

namespace web.Services;

public static class ClaimsPrincipalExtensions
{
    public static bool HasPermission(this ClaimsPrincipal principal, string permission)
    {
        return principal.IsInRole("Admin") || principal.Claims.Any(c => c.Type == "perm" && string.Equals(c.Value, permission, StringComparison.OrdinalIgnoreCase));
    }
}
