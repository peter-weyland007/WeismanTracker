using System.Security.Claims;
using api.Models;
using Microsoft.AspNetCore.Identity;

namespace api.Security;

public static class AppUserPasswordService
{
    private static readonly PasswordHasher<AppUser> Hasher = new();
    private const string IdentityV3HashPrefix = "AQAAAA";

    public static string HashPassword(AppUser user, string password)
        => Hasher.HashPassword(user, password);

    public static bool VerifyPassword(AppUser user, string? storedPassword, string providedPassword)
    {
        if (string.IsNullOrWhiteSpace(storedPassword) || string.IsNullOrWhiteSpace(providedPassword))
        {
            return false;
        }

        if (LooksHashed(storedPassword))
        {
            var result = Hasher.VerifyHashedPassword(user, storedPassword, providedPassword);
            return result != PasswordVerificationResult.Failed;
        }

        return string.Equals(storedPassword, providedPassword, StringComparison.Ordinal);
    }

    public static bool NeedsRehash(string? storedPassword)
        => !string.IsNullOrWhiteSpace(storedPassword) && !LooksHashed(storedPassword);

    public static int? GetCurrentUserId(ClaimsPrincipal principal)
    {
        var raw = principal.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? principal.FindFirstValue(ClaimTypes.Name)
            ?? principal.FindFirstValue("sub");

        return int.TryParse(raw, out var userId) ? userId : null;
    }

    private static bool LooksHashed(string password)
        => password.StartsWith(IdentityV3HashPrefix, StringComparison.Ordinal);
}
