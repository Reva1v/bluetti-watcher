namespace BluettiWatcher.Http;

/// <summary>
/// Who may talk to the service. Every request: Host exactly <c>127.0.0.1:&lt;port&gt;</c> or <c>localhost:&lt;port&gt;</c>
/// (no DNS rebinding). The one write route, POST /api/mode, also needs the header <c>X-Station: 1</c> (a custom header a
/// foreign page cannot send without a CORS preflight, which is never allowed) and an Origin that is absent or the
/// service's own.
/// </summary>
internal static class RequestGuard
{
    public static bool HostOk(HttpRequest r, int port) =>
        r.Headers.TryGetValue("host", out var host)
        && (string.Equals(host, $"127.0.0.1:{port}", StringComparison.Ordinal)
            || string.Equals(host, $"localhost:{port}", StringComparison.OrdinalIgnoreCase));

    /// <summary>null = allowed; else why it is refused (403).</summary>
    public static string? WriteRefusal(HttpRequest r, int port)
    {
        if (!HostOk(r, port)) return "foreign Host";
        if (!r.Headers.TryGetValue("x-station", out var x) || !string.Equals(x, "1", StringComparison.Ordinal)) return "no X-Station: 1 header";
        if (r.Headers.TryGetValue("origin", out var origin)
            && !string.Equals(origin, $"http://127.0.0.1:{port}", StringComparison.Ordinal)
            && !string.Equals(origin, $"http://localhost:{port}", StringComparison.OrdinalIgnoreCase))
            return "foreign Origin";
        return null;
    }
}
