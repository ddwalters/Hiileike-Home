using HiileikeHome.Components;
using HiileikeHome.Lore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddHubOptions(options => options.MaximumReceiveMessageSize = 512 * 1024);
builder.Services.AddSingleton<LoreStore>();

// Authentik login
var authority = builder.Configuration["Authentik:Authority"];
var clientId = builder.Configuration["Authentik:ClientId"];
var authentikConfigured = !string.IsNullOrWhiteSpace(authority) && !string.IsNullOrWhiteSpace(clientId);

if (builder.Configuration["DataProtection:KeysPath"] is { Length: > 0 } keysPath)
{
    builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(keysPath));
}

var authentication = builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.ExpireTimeSpan = TimeSpan.FromDays(30);
        options.SlidingExpiration = false;
        options.Events.OnRedirectToLogin = context =>
        {
            if (context.Request.Path.StartsWithSegments("/lore/pictures"))
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            else
                context.Response.Redirect(context.RedirectUri);

            return Task.CompletedTask;
        };
    });

if (authentikConfigured)
{
    authentication.AddOpenIdConnect(options =>
    {
        options.Authority = authority;
        options.ClientId = clientId;
        options.ResponseType = OpenIdConnectResponseType.Code;
        options.ResponseMode = OpenIdConnectResponseMode.Query;
    });
}

builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();

var app = builder.Build();

// Behind the proxy: use the public address
if (Uri.TryCreate(builder.Configuration["Authentik:PublicBaseUrl"], UriKind.Absolute, out var publicUrl))
{
    app.Use((context, next) =>
    {
        context.Request.Scheme = publicUrl.Scheme;
        context.Request.Host = new HostString(publicUrl.Authority);
        return next();
    });
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();

app.MapGet("/login", (string? returnUrl) => authentikConfigured
    ? Results.Challenge(new AuthenticationProperties
    {
        RedirectUri = RedirectHttpResult.IsLocalUrl(returnUrl) ? returnUrl : "/",
        IsPersistent = true
    }, [OpenIdConnectDefaults.AuthenticationScheme])
    : Results.Text("Sign-in isn't set up.", statusCode: StatusCodes.Status503ServiceUnavailable));

app.MapPost("/logout", async (HttpContext context) =>
{
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/");
});

app.MapGet("/lore/pictures/{id:guid}", (Guid id, LoreStore store, HttpContext context) =>
{
    if (store.OpenPicture(id) is not { } picture)
        return Results.NotFound();

    context.Response.Headers.CacheControl = "private, no-store";
    context.Response.Headers.XContentTypeOptions = "nosniff";
    return Results.File(picture.Stream, picture.ContentType);
}).RequireAuthorization();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
