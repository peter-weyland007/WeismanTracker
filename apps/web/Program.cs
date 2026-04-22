using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Components.Authorization;
using System.Net.Http.Headers;
using MudBlazor.Services;
using web.Components;
using web.Services;
using web.Swagger;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddMudServices();
builder.Services.AddAuthentication("TokenCookie")
    .AddScheme<AuthenticationSchemeOptions, TokenCookieAuthenticationHandler>("TokenCookie", _ => { });
builder.Services.AddAuthorization(options =>
{
    foreach (var permission in AppPermissions.All)
    {
        options.AddPolicy(permission, policy => policy.RequireAssertion(context => context.User.HasPermission(permission)));
    }
});
builder.Services.AddAuthorizationCore(options =>
{
    foreach (var permission in AppPermissions.All)
    {
        options.AddPolicy(permission, policy => policy.RequireAssertion(context => context.User.HasPermission(permission)));
    }
});
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<AuthTokenStore>();
builder.Services.AddScoped<ApiAuthenticationStateProvider>();
builder.Services.AddScoped<AuthenticationStateProvider>(sp => sp.GetRequiredService<ApiAuthenticationStateProvider>());
builder.Services.AddScoped<AuthHeaderHandler>();
builder.Services.AddHttpClient("WeismanApi", client =>
{
    var baseUrl = builder.Configuration["WeismanApi:BaseUrl"] ?? "http://127.0.0.1:5199";
    client.BaseAddress = new Uri(baseUrl);
}).AddHttpMessageHandler<AuthHeaderHandler>();
builder.Services.AddScoped<CatEtApiClient>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();

app.MapGet(EmbeddedSwaggerSupport.OpenApiProxyPath, async (HttpContext context, IHttpClientFactory httpClientFactory) =>
{
    var client = httpClientFactory.CreateClient("WeismanApi");
    using var request = new HttpRequestMessage(HttpMethod.Get, "/openapi/v1.json");

    if (context.Request.Cookies.TryGetValue("auth.token", out var token) && !string.IsNullOrWhiteSpace(token))
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Uri.UnescapeDataString(token));
    }

    using var response = await client.SendAsync(request, context.RequestAborted);
    var content = await response.Content.ReadAsStringAsync(context.RequestAborted);
    return Results.Content(
        content,
        response.Content.Headers.ContentType?.ToString() ?? "application/json",
        statusCode: (int)response.StatusCode);
})
.RequireAuthorization(AppPermissions.Integrations);

app.MapGet(EmbeddedSwaggerSupport.FramePath, () =>
    Results.Content(
        EmbeddedSwaggerSupport.BuildFrameHtml(EmbeddedSwaggerSupport.OpenApiProxyPath, "WeismanTracker API Docs"),
        "text/html"))
.RequireAuthorization(AppPermissions.Integrations);

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
