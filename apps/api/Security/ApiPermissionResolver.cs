using Microsoft.AspNetCore.Http;

namespace api.Security;

public static class ApiPermissionResolver
{
    // Contract:
    // - null => public endpoint
    // - [] => authenticated user, no feature permission required
    // - [perm1, perm2] => authenticated user needs at least one matching permission
    public static IReadOnlyList<string>? Resolve(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        var method = context.Request.Method;

        if (!path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (path.StartsWith("/api/auth/", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(path, "/api/health", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (path.StartsWith("/api/profile", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        if (path.StartsWith("/api/admin/users", StringComparison.OrdinalIgnoreCase))
        {
            return [AppPermissions.UserAccess];
        }

        if (path.StartsWith("/api/admin/deleted-records", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("/hard-delete", StringComparison.OrdinalIgnoreCase))
        {
            return [AppPermissions.HardDelete];
        }

        if (path.StartsWith("/api/integrations/", StringComparison.OrdinalIgnoreCase))
        {
            return [AppPermissions.Integrations];
        }

        if (path.StartsWith("/api/allowances/cell-phone", StringComparison.OrdinalIgnoreCase))
        {
            return [AppPermissions.CellPhoneAllowance];
        }

        if (path.StartsWith("/api/catet/activity", StringComparison.OrdinalIgnoreCase))
        {
            return [AppPermissions.ActivityTracker];
        }

        if (path.StartsWith("/api/catet/licenses", StringComparison.OrdinalIgnoreCase))
        {
            return [AppPermissions.ActivationKeys];
        }

        if (path.StartsWith("/api/catet/people", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/api/entities/users", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/api/resource-definitions", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/api/entities/user", StringComparison.OrdinalIgnoreCase))
        {
            if (HttpMethods.IsGet(method))
            {
                return [
                    AppPermissions.People,
                    AppPermissions.Computers,
                    AppPermissions.MobileDevices,
                    AppPermissions.OtherDevices,
                    AppPermissions.CellPhoneAllowance
                ];
            }

            return [AppPermissions.People];
        }

        if (path.StartsWith("/api/catet/computers", StringComparison.OrdinalIgnoreCase))
        {
            var category = context.Request.Query["category"].ToString();
            if (string.Equals(category, "mobile", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(category, "phone", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(category, "tablet", StringComparison.OrdinalIgnoreCase))
            {
                return [AppPermissions.MobileDevices];
            }

            if (string.Equals(category, "other", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(category, "other device", StringComparison.OrdinalIgnoreCase))
            {
                return [AppPermissions.OtherDevices];
            }

            return [AppPermissions.Computers];
        }

        return null;
    }
}
