using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using MudBlazor.Services;
using PersonalServerManager.Components;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using PersonalServerManager.Security;
using PersonalServerManager.Stash;
using PersonalServerManager.Hydrus;
using PersonalServerManager.Search;

var builder = WebApplication.CreateBuilder(args);

// Add MudBlazor services
builder.Services.AddMudServices();

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "PersonalServerManager.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.LoginPath = "/account";
        options.SlidingExpiration = true;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
    });
builder.Services.AddAuthorization();
builder.Services.AddAntiforgery();
builder.Services.AddSingleton<IPasswordHasher<string>, PasswordHasher<string>>();

var dataDirectory = Environment.GetEnvironmentVariable("PSM_DATA_DIRECTORY");
if (string.IsNullOrWhiteSpace(dataDirectory))
{
    dataDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PersonalServerManager");
}

Directory.CreateDirectory(dataDirectory);
if (!OperatingSystem.IsWindows())
{
    throw new PlatformNotSupportedException("Personal Server Manager currently requires Windows DPAPI for protected local data.");
}

builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDirectory, "keys")))
    .ProtectKeysWithDpapi();
builder.Services.AddSingleton<IStashInstanceStore>(services => new JsonStashInstanceStore(
    Path.Combine(dataDirectory, "stash-instances.json"),
    services.GetRequiredService<IDataProtectionProvider>().CreateProtector("StashApiKey.v1")));
builder.Services.AddSingleton<IAdminCredentialStore>(services => new JsonAdminCredentialStore(
    Path.Combine(dataDirectory, "owner-password.dat"),
    services.GetRequiredService<IDataProtectionProvider>().CreateProtector("OwnerPasswordHash.v1")));
builder.Services.AddHttpClient<IStashGraphQlClient, StashGraphQlClient>(client =>
    client.Timeout = Timeout.InfiniteTimeSpan);
builder.Services.AddSingleton<IHydrusClient>(_ => new HydrusClient());
builder.Services.AddSingleton<IHydrusInstanceStore>(services => new JsonHydrusInstanceStore(
    Path.Combine(dataDirectory, "hydrus-instances.json"),
    services.GetRequiredService<IDataProtectionProvider>().CreateProtector("HydrusApiKey.v1")));
builder.Services.AddTransient<LibrarySearchService>();
builder.Services.AddHttpClient<PreviewService>(client => client.Timeout = Timeout.InfiniteTimeSpan)
    .ConfigurePrimaryHttpMessageHandler(PreviewService.CreateHttpHandler);
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
    options.ForwardLimit = 1;

    var knownProxies = builder.Configuration.GetSection("ForwardedHeaders:KnownProxies").Get<string[]>() ?? [];
    foreach (var value in knownProxies)
    {
        if (!System.Net.IPAddress.TryParse(value, out var address))
        {
            throw new InvalidOperationException($"ForwardedHeaders:KnownProxies contains an invalid IP address: {value}");
        }

        options.KnownProxies.Add(address);
    }
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("account", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            AutoReplenishment = true,
            PermitLimit = 10,
            QueueLimit = 0,
            Window = TimeSpan.FromMinutes(1)
        }));
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

app.UseForwardedHeaders();
app.UseHttpsRedirection();
app.Use(async (context, next) =>
{
    if (context.Connection.RemoteIpAddress is not { } address || !System.Net.IPAddress.IsLoopback(address))
    {
        if (!context.Request.IsHttps)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("HTTPS is required for non-loopback access.");
            return;
        }
    }

    await next();
});

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.UseAntiforgery();
app.MapAccountEndpoints();
app.MapPreviewEndpoints();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .RequireAuthorization();

app.Run();
