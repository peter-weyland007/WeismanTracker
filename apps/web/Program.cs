using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Components.Authorization;
using MudBlazor.Services;
using web.Components;
using web.Services;

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

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
