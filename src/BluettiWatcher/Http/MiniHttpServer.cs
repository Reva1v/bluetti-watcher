using System.Net;
using System.Net.Sockets;
using System.Text;

namespace BluettiWatcher.Http;

internal sealed record HttpRequest(string Method, string Path, IReadOnlyDictionary<string, string> Query,
    IReadOnlyDictionary<string, string> Headers, byte[] Body);

internal sealed record HttpResponse(int Status, string ContentType, byte[] Body)
{
    public static HttpResponse Text(int status, string text) => new(status, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(text + "\n"));
    public static HttpResponse Json(int status, byte[] body) => new(status, "application/json; charset=utf-8", body);
}

/// <summary>
/// A minimal HTTP/1.1 server on a TcpListener: one request per connection (Connection: close), headers up to 16 KB, a body
/// up to 4 KB, the request head within 5 s (an idle connection is closed), 10 s per connection, at most 32 at a time; a
/// handler exception is answered 400 (bad input) or 500 and logged. No URL ACL and no admin rights: it binds 127.0.0.1 only.
/// </summary>
internal sealed class MiniHttpServer(IPAddress address, int port, Func<HttpRequest, CancellationToken, Task<HttpResponse>> handler, Action<string> log,
    TimeSpan? headTimeout = null) : IDisposable
{
    public void Dispose()
    {
        _listener?.Dispose();
        _slots.Dispose();
    }

    private const int MaxHead = 16 * 1024;
    private const int MaxBody = 4 * 1024;
    private const int Slots = 32;
    private readonly SemaphoreSlim _slots = new(Slots, Slots);
    private readonly TimeSpan _headTimeout = headTimeout ?? TimeSpan.FromSeconds(5);
    private TcpListener? _listener;

    /// <summary>Binds; throws SocketException when the port is taken.</summary>
    public void Start()
    {
        _listener = new TcpListener(address, port);
        _listener.Server.ExclusiveAddressUse = true;
        _listener.Start(16);
    }

    public int BoundPort => ((IPEndPoint)_listener!.LocalEndpoint).Port;

    public async Task RunAsync(CancellationToken ct)
    {
        using var reg = ct.Register(() => _listener?.Stop());
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener!.AcceptTcpClientAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            await _slots.WaitAsync(ct);
            _ = Task.Run(async () =>
            {
                try { await ServeAsync(client, ct); }
                finally { _slots.Release(); client.Dispose(); }
            }, CancellationToken.None);
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var stream = client.GetStream();
            HttpRequest? req;
            HttpResponse? bad;
            // An idle connection holds a slot only until the header timeout, then it is closed.
            using (var head = CancellationTokenSource.CreateLinkedTokenSource(cts.Token))
            {
                head.CancelAfter(_headTimeout);
                (req, bad) = await ReadRequestAsync(stream, head.Token);
            }
            HttpResponse resp;
            try { resp = bad ?? await handler(req!, cts.Token); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Every handler failure is answered: bad input 400, anything else 500; both logged.
                bool input = e is ArgumentException or FormatException or OverflowException;
                log($"http: {req!.Method} {req.Path} failed with {(input ? 400 : 500)}: {e.GetType().Name}: {e.Message}");
                resp = input ? HttpResponse.Text(400, "bad request") : HttpResponse.Text(500, "internal error");
            }
            await WriteAsync(stream, resp, cts.Token);
        }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException)
        {
            log($"http: connection dropped ({e.GetType().Name})");
        }
    }

    internal static async Task<(HttpRequest? Request, HttpResponse? Bad)> ReadRequestAsync(Stream stream, CancellationToken ct)
    {
        var buf = new byte[MaxHead + MaxBody];
        int len = 0, headEnd = -1;
        while (headEnd < 0)
        {
            if (len >= MaxHead) return (null, HttpResponse.Text(431, "headers too large"));
            int n = await stream.ReadAsync(buf.AsMemory(len, MaxHead - len), ct);
            if (n == 0) return (null, HttpResponse.Text(400, "incomplete request"));
            len += n;
            headEnd = buf.AsSpan(0, len).IndexOf("\r\n\r\n"u8);
        }
        var (req, bad) = ParseHead(Encoding.ASCII.GetString(buf, 0, headEnd));
        if (bad is not null) return (null, bad);
        int bodyLen = 0;
        if (req!.Headers.TryGetValue("content-length", out var cl) && (!int.TryParse(cl, out bodyLen) || bodyLen < 0))
            return (null, HttpResponse.Text(400, "bad content-length"));
        if (bodyLen > MaxBody) return (null, HttpResponse.Text(413, "body too large"));
        int have = len - (headEnd + 4);
        var body = new byte[bodyLen];
        Array.Copy(buf, headEnd + 4, body, 0, Math.Min(have, bodyLen));
        while (have < bodyLen)
        {
            int n = await stream.ReadAsync(body.AsMemory(have, bodyLen - have), ct);
            if (n == 0) return (null, HttpResponse.Text(400, "incomplete body"));
            have += n;
        }
        return (req with { Body = body }, null);
    }

    /// <summary>The request line and headers. A repeated Host, Origin or X-Station header is a bad request.</summary>
    internal static (HttpRequest? Request, HttpResponse? Bad) ParseHead(string head)
    {
        var lines = head.Split("\r\n");
        var parts = lines[0].Split(' ');
        if (parts.Length != 3 || !parts[2].StartsWith("HTTP/1.", StringComparison.Ordinal)) return (null, HttpResponse.Text(400, "bad request line"));
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            int colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0) return (null, HttpResponse.Text(400, "bad header"));
            string name = line[..colon].Trim(), value = line[(colon + 1)..].Trim();
            if (headers.ContainsKey(name))
            {
                if (name.Equals("host", StringComparison.OrdinalIgnoreCase) || name.Equals("origin", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("x-station", StringComparison.OrdinalIgnoreCase) || name.Equals("content-length", StringComparison.OrdinalIgnoreCase))
                    return (null, HttpResponse.Text(400, $"repeated {name}"));
                headers[name] += ", " + value;
            }
            else headers[name] = value;
        }
        string target = parts[1];
        int q = target.IndexOf('?', StringComparison.Ordinal);
        string path = q < 0 ? target : target[..q];
        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        if (q >= 0)
        {
            foreach (var kv in target[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = kv.IndexOf('=', StringComparison.Ordinal);
                string k = Uri.UnescapeDataString(eq < 0 ? kv : kv[..eq]);
                query[k] = eq < 0 ? "" : Uri.UnescapeDataString(kv[(eq + 1)..].Replace('+', ' '));
            }
        }
        return (new HttpRequest(parts[0], path, query, headers, []), null);
    }

    private static string Reason(int status) => status switch
    {
        200 => "OK", 202 => "Accepted", 400 => "Bad Request", 403 => "Forbidden", 404 => "Not Found", 405 => "Method Not Allowed", 409 => "Conflict",
        413 => "Payload Too Large", 431 => "Request Header Fields Too Large", 500 => "Internal Server Error", 502 => "Bad Gateway",
        _ => "Status",
    };

    internal static async Task WriteAsync(Stream stream, HttpResponse r, CancellationToken ct)
    {
        string head = $"HTTP/1.1 {r.Status} {Reason(r.Status)}\r\nContent-Type: {r.ContentType}\r\nContent-Length: {r.Body.Length}\r\n"
            + "Cache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nX-Frame-Options: DENY\r\nReferrer-Policy: no-referrer\r\n"
            + "Content-Security-Policy: default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'\r\n"
            + "Connection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct);
        await stream.WriteAsync(r.Body, ct);
        await stream.FlushAsync(ct);
    }
}
