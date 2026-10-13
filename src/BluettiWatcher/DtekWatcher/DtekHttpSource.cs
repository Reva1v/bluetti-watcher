using System.Net;
using System.Text;

namespace BluettiWatcher.DtekWatcher;

/// <summary>
/// The only outbound network of the service: HTTPS to the dataset's host and to the site's host, nowhere else. A redirect
/// is followed only inside the host that was asked, TLS validation is the platform's, each request has 30 s, a body over
/// 4 MB is refused. A site session is the page (its cookies and token) and, when asked, the address request; nothing but
/// the dataset's validator is kept between reads, and that by the caller.
/// </summary>
internal sealed class DtekHttpSource : IDtekSource
{
    public const int MaxBody = 4 * 1024 * 1024;
    private const int MaxRedirects = 3;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36";

    private readonly DtekConfig _config;
    private readonly HttpMessageHandler _handler;
    private readonly HttpClient _http;
    private readonly Uri _dataset;
    private readonly Uri _page;
    private readonly Uri _ajax;

    /// <summary>Tests hand in a handler, so no test touches the network; the service passes none.</summary>
    public DtekHttpSource(DtekConfig config, HttpMessageHandler? handler = null)
    {
        _config = config;
        // No automatic redirect, no cookie store, no proxy: every hop is this class's own.
        _handler = handler ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(20),
            MaxConnectionsPerServer = 1,
        };
        _http = new HttpClient(_handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        _dataset = config.DatasetUri() ?? throw new ArgumentException("dtekWatcher.datasetUrl must be an https address", nameof(config));
        _page = new Uri($"https://{config.Host}/ua/shutdowns");
        _ajax = new Uri($"https://{config.Host}/ua/ajax");
    }

    public void Dispose()
    {
        _http.Dispose();
        _handler.Dispose();
    }

    /// <summary>A request the source will not make or an answer it will not read.</summary>
    private sealed class RefusedException : Exception
    {
        public RefusedException() { }
        public RefusedException(string message) : base(message) { }
        public RefusedException(string message, Exception innerException) : base(message, innerException) { }
    }

    private static bool Same(Uri uri, string host) => uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && string.Equals(uri.Host, host, StringComparison.OrdinalIgnoreCase);

    /// <summary>The failure as a value; null for the caller's own cancellation, which goes on up.</summary>
    private static (DtekStatus Status, string Detail)? Failure(Exception e, CancellationToken ct) => e switch
    {
        RefusedException => (DtekStatus.Refused, e.Message),
        OperationCanceledException when !ct.IsCancellationRequested => (DtekStatus.Network, "no answer in 30 s"),
        HttpRequestException or IOException => (DtekStatus.Network, e.Message),
        OperationCanceledException => null,
        // Anything nobody expected (a corrupt compressed body, text that is no valid string): still a failed read, never a throw.
        _ => (DtekStatus.Refused, $"{e.GetType().Name}: {e.Message}"),
    };

    /// <summary>A request to <paramref name="host"/> and to no other: checked before anything leaves.</summary>
    private static HttpRequestMessage Request(HttpMethod method, Uri uri, string host, Dictionary<string, string>? cookies = null)
    {
        if (!Same(uri, host)) throw new RefusedException($"a request to {uri.Scheme}://{uri.Authority} refused: only https://{host} is read here");
        string cookie = cookies is null ? "" : string.Join("; ", cookies.Select(c => $"{c.Key}={c.Value}"));
        var req = new HttpRequestMessage(method, uri);
        req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        if (cookie.Length > 0) req.Headers.TryAddWithoutValidation("Cookie", cookie);
        return req;
    }

    /// <summary>The address a redirect names, when the answer is one; refused when it leaves the host.</summary>
    private static Uri? Redirect(HttpResponseMessage res, Uri from, string host)
    {
        if ((int)res.StatusCode is not (>= 300 and < 400) || res.StatusCode == HttpStatusCode.NotModified || res.Headers.Location is not { } to) return null;
        var next = to.IsAbsoluteUri ? to : new Uri(from, to);
        return Same(next, host) ? next : throw new RefusedException($"a redirect to {next.Scheme}://{next.Authority} refused: only https://{host} is read here");
    }

    private static async Task<string> BodyAsync(HttpResponseMessage res, string what, CancellationToken ct)
    {
        if (res.Content.Headers.ContentLength > MaxBody) throw new RefusedException($"{what} is over {MaxBody / 1024 / 1024} MB");
        var body = await res.Content.ReadAsStreamAsync(ct);
        await using (body)
        {
            using var reader = new StreamReader(new CappedStream(body, MaxBody, what), Encoding.UTF8);
            return await reader.ReadToEndAsync(ct);
        }
    }

    public async Task<DtekDatasetResult> DatasetAsync(string? etag, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(RequestTimeout);
            var uri = _dataset;
            for (int hop = 0; ; hop++)
            {
                var (next, result) = await DatasetHopAsync(uri, etag, timeout.Token);
                if (result is not null) return result;
                if (hop >= MaxRedirects) throw new RefusedException("too many redirects");
                uri = next!;
            }
        }
        catch (Exception e) when (Failure(e, ct) is { } failed) { return new(failed.Status, null, null, false, failed.Detail); }
    }

    private async Task<(Uri? Next, DtekDatasetResult? Result)> DatasetHopAsync(Uri uri, string? etag, CancellationToken ct)
    {
        using var req = Request(HttpMethod.Get, uri, _dataset.Host);
        req.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain;q=0.9, */*;q=0.1");
        if (!string.IsNullOrEmpty(etag)) req.Headers.TryAddWithoutValidation("If-None-Match", etag);
        using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (res.StatusCode == HttpStatusCode.NotModified) return (null, new(DtekStatus.Ok, null, etag, true, ""));
        if (Redirect(res, uri, _dataset.Host) is { } next) return (next, null);
        if (!res.IsSuccessStatusCode) return (null, new((int)res.StatusCode >= 500 ? DtekStatus.Network : DtekStatus.Format, null, null, false, $"HTTP {(int)res.StatusCode}"));
        string body = await BodyAsync(res, "the dataset", ct);
        return (null, DtekParser.ParseDataset(body, res.Headers.ETag?.ToString()));
    }

    public async Task<DtekFetch> SiteAsync(bool withHouse, CancellationToken ct)
    {
        var cookies = new Dictionary<string, string>(StringComparer.Ordinal);
        DtekPageResult page;
        try { page = await PageAsync(cookies, ct); }
        catch (Exception e) when (Failure(e, ct) is { } failed) { return new DtekFetch(new(failed.Status, null, null, failed.Detail), null); }
        if (!withHouse || !_config.HasAddress || page is not { Status: DtekStatus.Ok, Csrf: { } csrf, Schedule: { } schedule }) return new DtekFetch(page, null);
        try { return new DtekFetch(page, await HouseAsync(cookies, csrf, schedule.Update, ct)); }
        catch (Exception e) when (Failure(e, ct) is { } failed) { return new DtekFetch(page, new(failed.Status, null, failed.Detail)); }
    }

    private static void Keep(HttpResponseMessage res, Dictionary<string, string> cookies)
    {
        if (!res.Headers.TryGetValues("Set-Cookie", out var set)) return;
        foreach (var line in set)
        {
            string pair = line.Split(';', 2)[0];
            int eq = pair.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0) cookies[pair[..eq].Trim()] = pair[(eq + 1)..].Trim();
        }
    }

    private static bool Blocking(HttpStatusCode status) => status is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests;

    private async Task<DtekPageResult> PageAsync(Dictionary<string, string> cookies, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeout);
        var uri = _page;
        for (int hop = 0; ; hop++)
        {
            var (next, result) = await PageHopAsync(uri, cookies, timeout.Token);
            if (result is not null) return result;
            if (hop >= MaxRedirects) throw new RefusedException("too many redirects");
            uri = next!;
        }
    }

    /// <summary>One GET: the page as read, or the address a redirect names (already checked to be the own host).</summary>
    private async Task<(Uri? Next, DtekPageResult? Result)> PageHopAsync(Uri uri, Dictionary<string, string> cookies, CancellationToken ct)
    {
        using var req = Request(HttpMethod.Get, uri, _config.Host, cookies);
        req.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        req.Headers.TryAddWithoutValidation("Accept-Language", "uk-UA,uk;q=0.9,en;q=0.8");
        using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        Keep(res, cookies);
        if (Redirect(res, uri, _config.Host) is { } next) return (next, null);
        if (res.Content.Headers.ContentLength > MaxBody) throw new RefusedException($"the page is over {MaxBody / 1024 / 1024} MB");
        var body = await res.Content.ReadAsStreamAsync(ct);
        await using (body)
        {
            // Line by line: only the token and the schedule line are kept, never the page.
            using var reader = new StreamReader(new CappedStream(body, MaxBody, "the page"), Encoding.UTF8);
            var parsed = await DtekParser.ParsePageAsync(reader, ct);
            if (res.IsSuccessStatusCode || parsed.Status == DtekStatus.Blocked) return (null, parsed);
            string http = $"HTTP {(int)res.StatusCode}";
            if (Blocking(res.StatusCode)) return (null, new(DtekStatus.Blocked, null, null, http));
            return (null, new((int)res.StatusCode >= 500 ? DtekStatus.Network : DtekStatus.Format, null, null, http));
        }
    }

    private async Task<DtekHouseResult> HouseAsync(Dictionary<string, string> cookies, string csrf, string update, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeout);
        using var req = Request(HttpMethod.Post, _ajax, _config.Host, cookies);
        req.Headers.TryAddWithoutValidation("Accept", "application/json, text/javascript, */*; q=0.01");
        req.Headers.TryAddWithoutValidation("Accept-Language", "uk-UA,uk;q=0.9,en;q=0.8");
        req.Headers.TryAddWithoutValidation("X-CSRF-Token", csrf);
        req.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
        req.Headers.TryAddWithoutValidation("Referer", _page.AbsoluteUri);
        req.Headers.TryAddWithoutValidation("Origin", $"https://{_config.Host}");
        req.Content = new FormUrlEncodedContent(
        [
            new("method", "getHomeNum"),
            new("data[0][name]", "city"), new("data[0][value]", _config.City),
            new("data[1][name]", "street"), new("data[1][value]", _config.Street),
            new("data[2][name]", "updateFact"), new("data[2][value]", update),
        ]);
        using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        string text = await BodyAsync(res, "the address answer", timeout.Token);
        var parsed = DtekParser.ParseHouse(text, _config.House);
        if (res.IsSuccessStatusCode || parsed.Status == DtekStatus.Blocked) return parsed;
        string http = $"HTTP {(int)res.StatusCode}";
        if (Blocking(res.StatusCode)) return new(DtekStatus.Blocked, null, http);
        return new((int)res.StatusCode >= 500 ? DtekStatus.Network : DtekStatus.Format, null, http);
    }

    /// <summary>Reads through, and refuses once more than the cap has come.</summary>
    private sealed class CappedStream(Stream inner, int cap, string what) : Stream
    {
        private long _read;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }

        private int Count(int n)
        {
            _read += n;
            return _read > cap ? throw new RefusedException($"{what} is over {cap / 1024 / 1024} MB") : n;
        }

        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Count(await inner.ReadAsync(buffer, cancellationToken));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
