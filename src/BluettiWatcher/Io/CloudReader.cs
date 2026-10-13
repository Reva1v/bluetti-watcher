using System.Globalization;
using System.Net;
using System.Text.Json;
using BluettiWatcher.Decision;

namespace BluettiWatcher.Io;

/// <summary>Bluetooth first; when it does not answer, the cloud (design.md §4 Cloud fallback). Each reading carries its source.</summary>
internal sealed class FallbackReader(IStationReader bluetooth, IStationReader? cloud) : IStationReader
{
    public async Task<Reading?> ReadAsync(CancellationToken ct)
    {
        var r = await bluetooth.ReadAsync(ct);
        if (r is not null) return r with { Source = "bluetooth" };
        if (cloud is null) return null;
        var c = await cloud.ReadAsync(ct);
        return c is null ? null : c with { Source = "cloud" };
    }
}

/// <summary>The cloud token file: <c>{access_token, refresh_token, token_type, expires_in, created_at}</c>, created_at in unix seconds.</summary>
internal sealed record CloudToken(string Access, string Refresh, string TokenType, long ExpiresIn, long CreatedAt)
{
    public DateTimeOffset ExpiresAt => DateTimeOffset.FromUnixTimeSeconds(Math.Clamp(CreatedAt + ExpiresIn, 0, 253_402_300_799));

    /// <summary>Never the tokens: a record would print them.</summary>
    public override string ToString() => $"CloudToken {{ expires {ExpiresAt:yyyy-MM-dd'T'HH:mm:ss'Z'} }}";
}

/// <summary>
/// The BLUETTI cloud as the fallback reading (design.md §4 Cloud fallback), through the API of the official Home Assistant integration.
/// READ ONLY, FOREVER, as the Bluetooth path: the only calls are the device list (GET) and the token refresh (POST); no
/// control, fulfillment or bind call exists here. No token ever reaches a log line: a failure names its kind only.
/// </summary>
internal sealed class BluettiCloudReader(StationConfig config, HttpMessageHandler handler, TimeProvider time, Action<string> log) : IStationReader, IDisposable
{
    private const string DevicesUrl = "https://gw.bluettipower.com/api/bluiotdata/ha/v2/devices";
    internal const string TokenUrl = "https://sso.bluettipower.com/oauth2/token";
    private const string AppKey = "19974C15BCCBC91B21A8BA71BF5";
    private const string AppVer = "1.0.5";
    internal const string ClientId = "HomeAssistant";
    internal const string ClientSecret = "SG9tZUFzc2lzdGFudA==";
    private const int Expired = 805;
    private static readonly TimeSpan RefreshAhead = TimeSpan.FromDays(3);
    private static readonly TimeSpan EarlyRefreshHold = TimeSpan.FromHours(1);

    private readonly HttpClient _http = new(handler, disposeHandler: false) { Timeout = System.Threading.Timeout.InfiniteTimeSpan, MaxResponseContentBufferSize = 1 << 20 };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset? _earlyRefreshFailedAt;
    /// <summary>A token pair the server issued that could not be written yet: used, and saved again each poll until it is.</summary>
    private CloudToken? _unsaved;
    private double? _staleSoc;
    private DateTimeOffset _staleSocSince;

    /// <summary>The cap on each request.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);

    public void Dispose()
    {
        _http.Dispose();
        _gate.Dispose();
    }

    public async Task<Reading?> ReadAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var (r, why) = await ReadOnceAsync(ct);
            if (r is null) log($"cloud: no reading - {why}");
            return r;
        }
        finally { _gate.Release(); }
    }

    private async Task<(Reading? Reading, string Why)> ReadOnceAsync(CancellationToken ct)
    {
        var (answer, why) = await DevicesWithTokenAsync(ct);
        return answer is null ? (null, why) : Stale(answer, time.GetUtcNow()) is { } stale ? (null, stale) : (answer, "");
    }

    /// <summary>
    /// design.md §4 AC9: the cloud may serve a frozen cache with the device online. Off mains under a real load the SOC must move:
    /// unchanged for staleAfterMin it is a miss. The clock of an SOC value starts when the cloud first gave it.
    /// </summary>
    private string? Stale(Reading r, DateTimeOffset now)
    {
        if (r.Soc != _staleSoc) { _staleSoc = r.Soc; _staleSocSince = now; }
        if (config.StaleAfterMin <= 0 || r.InAc != 0 || !(r.OutAc >= config.StaleLoadW)) return null;
        var held = now - _staleSocSince;
        return held >= TimeSpan.FromMinutes(config.StaleAfterMin)
            ? Invariant($"cloud reading stale: SOC {r.Soc} % unchanged for {(int)held.TotalMinutes} min at {r.OutAc} W out, no AC input")
            : null;
    }

    private (CloudToken? Token, string Why) CurrentToken()
    {
        if (_unsaved is not { } kept) return LoadToken();
        try
        {
            Save(config.CloudTokenFile, kept);
            _unsaved = null;
            log("cloud: the refreshed token is written now");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log($"cloud: the refreshed token still could not be written ({e.GetType().Name}); kept in memory");
        }
        return (kept, "");
    }

    private async Task<(Reading? Reading, string Why)> DevicesWithTokenAsync(CancellationToken ct)
    {
        var (token, why) = CurrentToken();
        if (token is null) return (null, why);
        bool refreshed = false;
        var now = time.GetUtcNow();
        if (token.ExpiresAt - now < RefreshAhead && !(_earlyRefreshFailedAt is { } failedAt && now - failedAt < EarlyRefreshHold))
        {
            refreshed = true;
            var (fresh, failed) = await RefreshAsync(token, ct);
            if (fresh is not null) token = fresh;
            else
            {
                // The token may still be good: read with it, and try the refresh again in an hour, not every poll.
                _earlyRefreshFailedAt = now;
                log($"cloud: the early token refresh failed - {failed}; reading with the current token");
            }
        }
        var answer = await DevicesAsync(token.Access, ct);
        if (answer.MsgCode != Expired) return (answer.Reading, answer.Why);
        if (refreshed) return (null, $"msgCode {Expired} (token expired) after a token refresh");
        var (renewed, refreshWhy) = await RefreshAsync(token, ct);
        if (renewed is null) return (null, $"msgCode {Expired} (token expired) and {refreshWhy}");
        answer = await DevicesAsync(renewed.Access, ct);
        return answer.MsgCode == Expired ? (null, $"msgCode {Expired} (token expired) after a token refresh") : (answer.Reading, answer.Why);
    }

    private async Task<(int? MsgCode, Reading? Reading, string Why)> DevicesAsync(string access, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(DevicesUrl));
        req.Headers.TryAddWithoutValidation("Authorization", access);   // raw, no "Bearer"
        req.Headers.Add("x-os", "open");
        req.Headers.Add("x-app-key", AppKey);
        req.Headers.Add("x-app-ver", AppVer);
        var (status, body, why) = await SendAsync(req, ct);
        if (body is null) return (null, null, $"devices: {why}");
        if (status != HttpStatusCode.OK) return (null, null, Invariant($"devices answered HTTP {(int)status}"));
        try
        {
            using var doc = JsonDocument.Parse(body);
            return Parse(doc.RootElement, config.Type);
        }
        catch (JsonException) { return (null, null, "the devices answer is not JSON"); }
    }

    /// <summary>The first device whose model starts with <paramref name="type"/>; its SOC, AC in and AC out, and a derived mode (design.md §4 AC2a).</summary>
    internal static (int? MsgCode, Reading? Reading, string Why) Parse(JsonElement root, string type)
    {
        if (root.ValueKind != JsonValueKind.Object) return (null, null, "the devices answer is not an object");
        int? code = root.TryGetProperty("msgCode", out var mc) ? Long(mc) is { } l ? (int)l : null : null;
        if (code is null) return (null, null, "the devices answer has no msgCode");
        if (code != 0) return (code, null, Invariant($"msgCode {code}"));
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return (code, null, "the devices answer has no device list");
        foreach (var d in data.EnumerateArray())
        {
            if (d.ValueKind != JsonValueKind.Object || Str(d, "model") is not { } model || !model.StartsWith(type, StringComparison.OrdinalIgnoreCase)) continue;
            if (Str(d, "online") != "1") return (code, null, $"the {type} is offline in the cloud");
            var f = new Dictionary<string, string>(StringComparer.Ordinal);
            if (d.TryGetProperty("stateList", out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var s in list.EnumerateArray())
                    if (s.ValueKind == JsonValueKind.Object && Str(s, "fnCode") is { } k && Str(s, "fnValue") is { } v) f[k] = v;
            double? Num(string k) => f.TryGetValue(k, out var v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) ? n : null;
            if (Num("SOC") is not { } soc) return (code, null, "no numeric SOC in the cloud answer");
            double? inAc = Num("GridAllTotalPower"), outAc = Num("ACLoadAllTotalPower");
            // The cloud has no output mode: mains in = mains on; no AC in while the AC output feeds a load = the inverter; else unknown.
            string? mode = inAc > 0 ? "CLOUD_MAINS" : inAc == 0 && Num("SetCtrlAc") == 1 && outAc > 0 ? "INVERTER_OUTPUT" : null;
            return (code, new Reading(soc, inAc, outAc, mode, Source: "cloud"), "");
        }
        return (code, null, $"no {type} in the cloud account");
    }

    private async Task<(CloudToken? Token, string Why)> RefreshAsync(CloudToken token, CancellationToken ct)
    {
        if (token.Refresh.Length == 0) return (null, "the token file has no refresh token");
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(TokenUrl))
        {
            Content = new FormUrlEncodedContent([
                new("grant_type", "refresh_token"), new("refresh_token", token.Refresh),
                new("client_id", ClientId), new("client_secret", ClientSecret)]),
        };
        var (status, body, why) = await SendAsync(req, ct);
        if (body is null) return (null, $"the token refresh {why}");
        if (status != HttpStatusCode.OK) return (null, Invariant($"the token refresh answered HTTP {(int)status}"));
        CloudToken? fresh;
        try
        {
            using var doc = JsonDocument.Parse(body);
            fresh = Renewed(doc.RootElement, token, time.GetUtcNow().ToUnixTimeSeconds());
        }
        catch (JsonException) { return (null, "the token refresh answer is not JSON"); }
        if (fresh is null) return (null, "the token refresh answer has no access token");
        // The server may have retired the old pair: the new one is used whether or not it reached the disk.
        _unsaved = null;
        try { Save(config.CloudTokenFile, fresh); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _unsaved = fresh;
            log($"cloud: the refreshed token could not be written ({e.GetType().Name}); kept in memory, written again next poll");
        }
        return (fresh, "");
    }

    /// <summary>A standard OAuth2 answer (or one wrapped in <c>data</c>); a missing refresh token, type or lifetime keeps the old one.</summary>
    internal static CloudToken? Renewed(JsonElement root, CloudToken old, long now)
    {
        var t = root.ValueKind == JsonValueKind.Object && !root.TryGetProperty("access_token", out _) && root.TryGetProperty("data", out var data) ? data : root;
        if (t.ValueKind != JsonValueKind.Object || Str(t, "access_token") is not { Length: > 0 } access) return null;
        return new CloudToken(access, Str(t, "refresh_token") is { Length: > 0 } r ? r : old.Refresh, Str(t, "token_type") ?? old.TokenType,
            t.TryGetProperty("expires_in", out var e) && Long(e) is long exp && exp > 0 ? exp : old.ExpiresIn, now);
    }

    private (CloudToken? Token, string Why) LoadToken()
    {
        string text;
        try { text = File.ReadAllText(config.CloudTokenFile); }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return (null, "no token file"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return (null, $"the token file is unreadable ({e.GetType().Name})"); }
        try
        {
            using var doc = JsonDocument.Parse(text);
            var t = doc.RootElement;
            if (t.ValueKind != JsonValueKind.Object || Str(t, "access_token") is not { Length: > 0 } access) return (null, "the token file has no access token");
            long Field(string k) => t.TryGetProperty(k, out var v) ? Long(v) ?? 0 : 0;
            return (new CloudToken(access, Str(t, "refresh_token") ?? "", Str(t, "token_type") ?? "bearer", Field("expires_in"), Field("created_at")), "");
        }
        catch (JsonException) { return (null, $"the token file is not JSON (torn or zeroed?): {config.CloudTokenFile} needs a fresh token from a new login"); }
    }

    /// <summary>Temp file beside it, flushed to the disk, then a replace: a reader (or a hard power loss) never leaves half a token file.</summary>
    internal static void Save(string path, CloudToken t)
    {
        string tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            using (var w = new Utf8JsonWriter(fs))
            {
                w.WriteStartObject();
                w.WriteString("access_token", t.Access);
                w.WriteString("refresh_token", t.Refresh);
                w.WriteString("token_type", t.TokenType);
                w.WriteNumber("expires_in", t.ExpiresIn);
                w.WriteNumber("created_at", t.CreatedAt);
                w.WriteEndObject();
            }
            fs.Flush(flushToDisk: true);
        }
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Body null = no answer; the reason names the failure's kind only, never its message (it may echo the request).</summary>
    private async Task<(HttpStatusCode Status, string? Body, string Why)> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        using var cap = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cap.CancelAfter(Timeout);
        try
        {
            using var resp = await _http.SendAsync(req, cap.Token);
            return (resp.StatusCode, await resp.Content.ReadAsStringAsync(cap.Token), "");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (0, null, Invariant($"timed out after {Timeout.TotalSeconds:0.#} s"));
        }
        catch (HttpRequestException e)
        {
            return (0, null, $"HTTP failed ({e.HttpRequestError})");
        }
    }

    private static string? Str(JsonElement o, string k) => o.TryGetProperty(k, out var v) ? v.ValueKind switch
    {
        JsonValueKind.String => v.GetString(),
        JsonValueKind.Number => v.GetRawText(),
        _ => null,
    } : null;

    private static long? Long(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Number when v.TryGetInt64(out var n) => n,
        JsonValueKind.String when long.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) => n,
        _ => null,
    };

    private static string Invariant(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}
