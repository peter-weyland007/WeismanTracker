using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace web.Services;

public class TokenCookieAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IConfiguration configuration)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    private const string TokenCookieName = "auth.token";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Cookies.TryGetValue(TokenCookieName, out var token) || string.IsNullOrWhiteSpace(token))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var signingKey = configuration["Jwt:SigningKey"] ?? "dev-only-change-me-super-secret-signing-key";
        var issuer = configuration["Jwt:Issuer"] ?? "WeismanTracker.Api";
        var audience = configuration["Jwt:Audience"] ?? "WeismanTracker.Web";

        try
        {
            var handler = new JwtSecurityTokenHandler();
            var principal = handler.ValidateToken(token, new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateIssuerSigningKey = true,
                ValidateLifetime = true,
                ValidIssuer = issuer,
                ValidAudience = audience,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
                ClockSkew = TimeSpan.FromMinutes(2),
                NameClaimType = ClaimTypes.Name,
                RoleClaimType = ClaimTypes.Role
            }, out _);

            var ticket = new AuthenticationTicket(principal, Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
        catch (Exception ex)
        {
            return Task.FromResult(AuthenticateResult.Fail(ex));
        }
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        var returnUrl = Request.Path + Request.QueryString;
        Response.Redirect($"/login?returnUrl={Uri.EscapeDataString(returnUrl)}");
        return Task.CompletedTask;
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.Redirect("/access-denied");
        return Task.CompletedTask;
    }
}
