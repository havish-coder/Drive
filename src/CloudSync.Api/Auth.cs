using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using CloudSync.Infrastructure.Metadata;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;

namespace CloudSync.Api;

public sealed record Credentials(string? UserName, string? Password);

/// <summary>Accounts: PBKDF2 password hashes, a cookie session for the browser, bearer tokens for the CLI.</summary>
public static partial class Auth
{
    public const string Bearer = "Bearer";
    private const int Iterations = 600_000; // OWASP guidance for PBKDF2-SHA256

    // Verified against when the user doesn't exist, so "no such user" and "wrong password" take the same time.
    private static readonly string DummyHash = HashPassword(Guid.NewGuid().ToString());

    public static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }

    public static bool VerifyPassword(string password, string stored)
    {
        var parts = stored.Split('.');
        var expected = Convert.FromBase64String(parts[2]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromBase64String(parts[1]), int.Parse(parts[0]),
            HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    public static string UserId(this HttpContext ctx) => ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    public static ClaimsPrincipal Principal(string userId, string scheme) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], scheme));

    public static IServiceCollection AddCloudSyncAuth(this IServiceCollection services)
    {
        services.AddAuthentication(o => o.DefaultScheme = "cookie-or-bearer")
            .AddPolicyScheme("cookie-or-bearer", null, o => o.ForwardDefaultSelector = ctx =>
                ctx.Request.Headers.Authorization.Count > 0 ? Bearer : CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(o =>
            {
                o.Cookie.Name = "cloudsync";
                o.Cookie.SameSite = SameSiteMode.Strict; // CSRF defence: never sent on cross-site requests
                o.Events.OnRedirectToLogin = c => { c.Response.StatusCode = 401; return Task.CompletedTask; };
                o.Events.OnRedirectToAccessDenied = c => { c.Response.StatusCode = 403; return Task.CompletedTask; };
            })
            .AddScheme<AuthenticationSchemeOptions, TokenHandler>(Bearer, null);
        return services.AddAuthorization();
    }

    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/api/auth");

        auth.MapPost("/register", async (Credentials c, HttpContext ctx, SqliteDrive drive) =>
        {
            var user = Normalize(c.UserName);
            if (!UserNamePattern().IsMatch(user)) return Results.BadRequest(new { error = "Username: 3-32 characters, letters, digits, . _ -" });
            if ((c.Password ?? "").Length < 8) return Results.BadRequest(new { error = "Password must be at least 8 characters." });
            if (!await drive.CreateUserAsync(user, HashPassword(c.Password!))) return Results.Conflict(new { error = "That username is taken." });
            await SignInAsync(ctx, user);
            return Results.Ok(new { userName = user });
        });

        auth.MapPost("/login", async (Credentials c, HttpContext ctx, SqliteDrive drive) =>
        {
            if (await CheckAsync(c, drive) is not { } user) return Results.Unauthorized();
            await SignInAsync(ctx, user);
            return Results.Ok(new { userName = user });
        });

        auth.MapPost("/token", async (Credentials c, SqliteDrive drive) =>
            await CheckAsync(c, drive) is { } user ? Results.Ok(new { token = await drive.CreateTokenAsync(user) }) : Results.Unauthorized());

        auth.MapPost("/logout", (HttpContext ctx) => ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme));
        auth.MapGet("/me", (HttpContext ctx) => Results.Ok(new { userName = ctx.UserId() })).RequireAuthorization();
    }

    private static async Task<string?> CheckAsync(Credentials c, SqliteDrive drive)
    {
        var user = Normalize(c.UserName);
        var hash = await drive.PasswordHashAsync(user);
        return VerifyPassword(c.Password ?? "", hash ?? DummyHash) && hash is not null ? user : null;
    }

    private static Task SignInAsync(HttpContext ctx, string user) =>
        ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, Principal(user, CookieAuthenticationDefaults.AuthenticationScheme));

    private static string Normalize(string? userName) => (userName ?? "").Trim().ToLowerInvariant();

    [GeneratedRegex("^[a-z0-9._-]{3,32}$")]
    private static partial Regex UserNamePattern();
}

/// <summary>"Authorization: Bearer &lt;token&gt;" for the CLI. Tokens are stored hashed.</summary>
public sealed class TokenHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, SqliteDrive drive)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return AuthenticateResult.NoResult();
        return await drive.UserForTokenAsync(header[7..].Trim()) is { } user
            ? AuthenticateResult.Success(new AuthenticationTicket(Auth.Principal(user, Scheme.Name), Scheme.Name))
            : AuthenticateResult.Fail("Invalid token.");
    }
}
