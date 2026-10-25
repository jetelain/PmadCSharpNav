using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;

namespace Pmad.CSharpNav.Server.Security;

public sealed class DaemonSecurityOptions
{
    public string ExpectedToken { get; }

    public DaemonSecurityOptions(string? token = null)
    {
        ExpectedToken = token ?? GenerateSecureToken();
    }

    public static string GenerateSecureToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

public sealed class DaemonSecurityMiddleware
{
    private readonly RequestDelegate _next;
    private readonly DaemonSecurityOptions _options;

    public DaemonSecurityMiddleware(RequestDelegate next, DaemonSecurityOptions options)
    {
        _next = next;
        _options = options;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // 1. Origin header check: reject any request with an Origin header (CSRF protection)
        if (context.Request.Headers.ContainsKey("Origin"))
        {
            context.Response.StatusCode = (int)HttpStatusCode.Forbidden;
            await context.Response.WriteAsync("Forbidden: Browser Origin headers not accepted.");
            return;
        }

        // 2. Host check: only loopback allowed
        var host = context.Request.Host.Host;
        if (!IsLoopbackHost(host))
        {
            context.Response.StatusCode = (int)HttpStatusCode.Forbidden;
            await context.Response.WriteAsync("Forbidden: Host must be loopback.");
            return;
        }

        // 3. Authorization Bearer Token check
        var authHeader = context.Request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(authHeader) ||
            !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
            await context.Response.WriteAsync("Unauthorized: Missing or invalid Bearer token.");
            return;
        }

        var token = authHeader["Bearer ".Length..].Trim();
        if (!CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(token),
                System.Text.Encoding.UTF8.GetBytes(_options.ExpectedToken)))
        {
            context.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
            await context.Response.WriteAsync("Unauthorized: Token mismatch.");
            return;
        }

        await _next(context);
    }

    private static bool IsLoopbackHost(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(host, "[::1]", StringComparison.OrdinalIgnoreCase)) return true;

        if (IPAddress.TryParse(host, out var ip))
        {
            return IPAddress.IsLoopback(ip);
        }

        return false;
    }
}
