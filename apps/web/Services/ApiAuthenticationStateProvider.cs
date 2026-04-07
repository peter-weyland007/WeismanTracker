using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

namespace web.Services;

public class ApiAuthenticationStateProvider(AuthTokenStore tokenStore) : AuthenticationStateProvider
{
    private static readonly ClaimsPrincipal Anonymous = new(new ClaimsIdentity());
    private static readonly JwtSecurityTokenHandler TokenHandler = new();

    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        var token = await tokenStore.GetTokenAsync();
        if (string.IsNullOrWhiteSpace(token))
        {
            return new AuthenticationState(Anonymous);
        }

        try
        {
            var jwt = TokenHandler.ReadJwtToken(token);
            if (jwt.ValidTo <= DateTime.UtcNow)
            {
                await tokenStore.ClearTokenAsync();
                return new AuthenticationState(Anonymous);
            }

            var identity = new ClaimsIdentity(jwt.Claims, authenticationType: "jwt", nameType: ClaimTypes.Name, roleType: ClaimTypes.Role);
            return new AuthenticationState(new ClaimsPrincipal(identity));
        }
        catch
        {
            await tokenStore.ClearTokenAsync();
            return new AuthenticationState(Anonymous);
        }
    }

    public async Task SignInAsync(string token)
    {
        await tokenStore.SetTokenAsync(token);
        NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
    }

    public async Task SignOutAsync()
    {
        await tokenStore.ClearTokenAsync();
        NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
    }
}
