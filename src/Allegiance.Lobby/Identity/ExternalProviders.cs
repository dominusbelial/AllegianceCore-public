using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;

namespace Allegiance.Lobby.Identity;

public static class ExternalProviders
{
    public const string ExternalCookie = "external";
    public static void AddIdentityProviders(this IServiceCollection services, IdentitySettings settings)
    {
        // Persistent protection keys keep provider callback state usable over a lobby restart.
        services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(settings.SigningKeyFile))!, "protection")));
        var auth = services.AddAuthentication(ExternalCookie).AddCookie(ExternalCookie, o =>
        {
            o.Cookie.Name = "allegiance.external"; o.Cookie.HttpOnly = true;
            o.Cookie.SameSite = SameSiteMode.Lax; o.ExpireTimeSpan = TimeSpan.FromMinutes(5);
            o.Cookie.SecurePolicy = settings.PublicUrl.StartsWith("https:", StringComparison.Ordinal) ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
        });
        if (settings.GitHub.Enabled) auth.AddGitHub("GitHub", o =>
        {
            o.ClientId = settings.GitHub.ClientId; o.ClientSecret = settings.GitHub.ClientSecret; o.SignInScheme = ExternalCookie;
        });
        if (settings.Google.Enabled) auth.AddGoogle("Google", o =>
        {
            o.ClientId = settings.Google.ClientId; o.ClientSecret = settings.Google.ClientSecret; o.SignInScheme = ExternalCookie;
        });
        if (settings.Steam.Enabled) auth.AddSteam("Steam", o =>
        {
            o.ApplicationKey = settings.Steam.ClientSecret; o.SignInScheme = ExternalCookie;
        });
    }
    public static bool Enabled(IdentitySettings s, string provider) => provider switch
    {
        "GitHub" => s.GitHub.Enabled, "Google" => s.Google.Enabled, "Steam" => s.Steam.Enabled, _ => false,
    };
    public static void MapExternalProviders(this RouteGroupBuilder group, IdentitySettings settings)
    {
        group.MapGet("/external/{provider}", (string provider) => Enabled(settings, provider)
            ? Results.Challenge(new AuthenticationProperties { RedirectUri = "/identity.html", Items = { ["provider"] = provider } }, [provider])
            : Results.NotFound());
        group.MapPost("/external/complete", async (RegisterOptionsRequest req, HttpContext ctx, IdentityStore store) =>
        {
            var auth = await ctx.AuthenticateAsync(ExternalCookie);
            if (!auth.Succeeded || auth.Properties is null || !auth.Properties.Items.TryGetValue("provider", out var provider) || provider is null
                || !Enabled(settings, provider) || auth.Principal.FindFirstValue(ClaimTypes.NameIdentifier) is not { } subject)
                return Results.Unauthorized();
            var account = store.ExternalAccount(provider, subject, req.Callsign);
            await ctx.SignOutAsync(ExternalCookie);
            IdentityEndpoints.SetCookie(ctx, settings, store.Session(account.Id));
            return Results.Ok(account);
        });
    }
}
