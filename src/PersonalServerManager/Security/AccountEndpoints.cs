using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;

namespace PersonalServerManager.Security;

public static class AccountEndpoints
{
    public static void MapAccountEndpoints(this WebApplication app)
    {
        app.MapGet("/account", async (HttpContext context, IAntiforgery antiforgery, IAdminCredentialStore credentials) =>
        {
            if (context.User.Identity?.IsAuthenticated == true)
            {
                return Results.Redirect("/");
            }

            var enrollment = await credentials.GetPasswordHashAsync(context.RequestAborted) is null;
            if (enrollment && !IsLoopback(context))
            {
                return Results.Content(AccountPage("Enrollment must be completed from the manager PC.", true, null), "text/html", statusCode: (int)HttpStatusCode.Forbidden);
            }

            var tokens = antiforgery.GetAndStoreTokens(context);
            return Results.Content(AccountPage(null, enrollment, tokens.RequestToken), "text/html");
        }).AllowAnonymous();

        app.MapPost("/account/enroll", async (HttpContext context, IAntiforgery antiforgery, IAdminCredentialStore credentials, IPasswordHasher<string> hasher) =>
        {
            await antiforgery.ValidateRequestAsync(context);
            if (!IsLoopback(context))
            {
                return Results.StatusCode((int)HttpStatusCode.Forbidden);
            }

            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            var password = form["password"].ToString();
            var confirmation = form["confirmation"].ToString();
            if (password.Length < 14 || !string.Equals(password, confirmation, StringComparison.Ordinal))
            {
                return Results.Content(AccountPage("Use a password of at least 14 characters and enter it the same way twice.", true,
                    antiforgery.GetAndStoreTokens(context).RequestToken), "text/html", statusCode: (int)HttpStatusCode.BadRequest);
            }

            var hash = hasher.HashPassword("PersonalServerManager", password);
            if (!await credentials.TryCreateAsync(hash, context.RequestAborted))
            {
                return Results.Content(AccountPage("Enrollment is already complete. Sign in instead.", false,
                    antiforgery.GetAndStoreTokens(context).RequestToken), "text/html", statusCode: (int)HttpStatusCode.Conflict);
            }

            await SignInAsync(context);
            return Results.Redirect("/");
        }).AllowAnonymous().RequireRateLimiting("account");

        app.MapPost("/account/login", async (HttpContext context, IAntiforgery antiforgery, IAdminCredentialStore credentials, IPasswordHasher<string> hasher) =>
        {
            await antiforgery.ValidateRequestAsync(context);
            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            var storedHash = await credentials.GetPasswordHashAsync(context.RequestAborted);
            var password = form["password"].ToString();
            if (storedHash is null || hasher.VerifyHashedPassword("PersonalServerManager", storedHash, password) == PasswordVerificationResult.Failed)
            {
                return Results.Content(AccountPage("The password was not accepted.", false,
                    antiforgery.GetAndStoreTokens(context).RequestToken), "text/html", statusCode: (int)HttpStatusCode.Unauthorized);
            }

            await SignInAsync(context);
            return Results.Redirect("/");
        }).AllowAnonymous().RequireRateLimiting("account");

        app.MapPost("/account/logout", async (HttpContext context, IAntiforgery antiforgery) =>
        {
            await antiforgery.ValidateRequestAsync(context);
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Redirect("/account");
        }).RequireAuthorization();
    }

    private static async Task SignInAsync(HttpContext context)
    {
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "owner"), new Claim(ClaimTypes.Name, "Owner")],
            CookieAuthenticationDefaults.AuthenticationScheme);
        await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
    }

    private static bool IsLoopback(HttpContext context) =>
        context.Connection.RemoteIpAddress is { } address && IPAddress.IsLoopback(address);

    private static string AccountPage(string? error, bool enrollment, string? token)
    {
        var encodedError = error is null ? string.Empty : $"<p class=\"error\">{HtmlEncoder.Default.Encode(error)}</p>";
        var encodedToken = HtmlEncoder.Default.Encode(token ?? string.Empty);
        var action = enrollment ? "/account/enroll" : "/account/login";
        var title = enrollment ? "Create your owner password" : "Sign in to your server";
        var fields = enrollment
            ? "<label>Confirm password<input name=\"confirmation\" type=\"password\" autocomplete=\"new-password\" required></label>"
            : string.Empty;
        var submit = enrollment ? "Create account" : "Sign in";

        return $$"""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{{title}} · Personal Server Manager</title>
            <style>
            :root{color-scheme:light;--ink:#172a2a;--muted:#627574;--line:#cbd6d1;--paper:#f4f4ed;--panel:#fffefa;--accent:#176d62;--alert:#a33d2f}
            *{box-sizing:border-box}body{margin:0;min-height:100vh;display:grid;place-items:center;background:radial-gradient(ellipse at 80% 10%,#dce8df 0,transparent 42%),var(--paper);font:16px/1.5 Georgia,serif;color:var(--ink)}
            main{width:min(440px,calc(100% - 32px));padding:36px;background:var(--panel);border:1px solid var(--line);border-top:5px solid var(--accent);box-shadow:0 18px 48px #132a2414}p{color:var(--muted)}h1{font-size:30px;line-height:1.15;margin:6px 0 24px}.eyebrow{font:700 11px/1.2 system-ui,sans-serif;letter-spacing:1px;text-transform:uppercase;color:var(--accent)}
            label{display:grid;gap:7px;margin:17px 0;font:600 13px system-ui,sans-serif}input{width:100%;padding:12px;border:1px solid var(--line);background:#fff;font:16px system-ui,sans-serif;color:var(--ink)}button{width:100%;padding:13px;border:0;background:var(--accent);color:white;font:700 14px system-ui,sans-serif;cursor:pointer}.error{padding:10px 12px;border-left:3px solid var(--alert);background:#fff0eb;color:var(--alert)}
            </style></head><body><main><div class="eyebrow">Personal Server Manager</div><h1>{{title}}</h1>
            {{(enrollment ? "<p>This one-time setup is available only from the manager PC.</p>" : "<p>Local access · Owner account</p>")}}
            {{(encodedError.Length == 0 ? "" : encodedError)}}
            <form method="post" action="{{action}}"><input type="hidden" name="__RequestVerificationToken" value="{{encodedToken}}">
            <label>Password<input name="password" type="password" autocomplete="{{(enrollment ? "new-password" : "current-password")}}" required></label>{{fields}}
            <button type="submit">{{submit}}</button></form></main></body></html>
            """;
    }
}