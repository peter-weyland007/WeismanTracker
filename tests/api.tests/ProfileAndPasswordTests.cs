using System.Security.Claims;
using api.Models;
using api.Security;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace api.tests;

public sealed class ProfileAndPasswordTests
{
    [Fact]
    public void Resolve_requires_authenticated_user_for_profile_endpoints()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/api/profile";

        var permissions = ApiPermissionResolver.Resolve(context);

        Assert.NotNull(permissions);
        Assert.Empty(permissions!);
    }

    [Fact]
    public void HashPassword_produces_non_plaintext_hash_that_verifies()
    {
        var user = new AppUser { Username = "mark" };

        var hash = AppUserPasswordService.HashPassword(user, "Sup3rSecret!");

        Assert.NotEqual("Sup3rSecret!", hash);
        Assert.True(AppUserPasswordService.VerifyPassword(user, hash, "Sup3rSecret!"));
        Assert.False(AppUserPasswordService.VerifyPassword(user, hash, "wrong-password"));
    }

    [Fact]
    public void VerifyPassword_accepts_legacy_plaintext_passwords()
    {
        var user = new AppUser { Username = "legacy-user" };

        Assert.True(AppUserPasswordService.VerifyPassword(user, "admin", "admin"));
        Assert.False(AppUserPasswordService.VerifyPassword(user, "admin", "different"));
    }

    [Fact]
    public void HashPassword_preserves_leading_and_trailing_spaces()
    {
        var user = new AppUser { Username = "spaces" };
        const string password = "  spaced-secret  ";

        var hash = AppUserPasswordService.HashPassword(user, password);

        Assert.True(AppUserPasswordService.VerifyPassword(user, hash, password));
        Assert.False(AppUserPasswordService.VerifyPassword(user, hash, password.Trim()));
    }

    [Fact]
    public void GetCurrentUserId_reads_name_identifier_claim()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, "42"),
            new Claim(ClaimTypes.Name, "mark")
        ], authenticationType: "jwt"));

        var userId = AppUserPasswordService.GetCurrentUserId(principal);

        Assert.Equal(42, userId);
    }
}
