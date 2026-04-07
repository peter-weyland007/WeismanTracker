using Microsoft.JSInterop;

namespace web.Services;

public class AuthTokenStore(IJSRuntime jsRuntime, IHttpContextAccessor httpContextAccessor)
{
    private const string TokenKey = "auth.token";
    private string? _cachedToken;

    public bool LastReadFailed { get; private set; }

    public async Task<string?> GetTokenAsync()
    {
        if (!string.IsNullOrWhiteSpace(_cachedToken))
        {
            LastReadFailed = false;
            return _cachedToken;
        }

        try
        {
            var token = await jsRuntime.InvokeAsync<string?>("authTokenStore.getToken", TokenKey);
            if (!string.IsNullOrWhiteSpace(token))
            {
                LastReadFailed = false;
                _cachedToken = token;
                return _cachedToken;
            }
        }
        catch
        {
            LastReadFailed = true;
        }

        var cookieToken = httpContextAccessor.HttpContext?.Request.Cookies[TokenKey];
        if (!string.IsNullOrWhiteSpace(cookieToken))
        {
            LastReadFailed = false;
            _cachedToken = Uri.UnescapeDataString(cookieToken);
            return _cachedToken;
        }

        return null;
    }

    public async Task SetTokenAsync(string token)
    {
        LastReadFailed = false;
        _cachedToken = token;
        await jsRuntime.InvokeVoidAsync("authTokenStore.setToken", TokenKey, token);
    }

    public async Task ClearTokenAsync()
    {
        LastReadFailed = false;
        _cachedToken = null;

        try
        {
            await jsRuntime.InvokeVoidAsync("authTokenStore.clearToken", TokenKey);
        }
        catch
        {
            // ignore cleanup failures during disconnected circuits
        }
    }
}
