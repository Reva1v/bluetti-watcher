using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using BluettiWatcher.Decision;
using BluettiWatcher.Http;
using BluettiWatcher.Io;
using BluettiWatcher.Storage;
using static BluettiWatcher.Tests.Fx;

namespace BluettiWatcher.Tests;

/// <summary>Answers each request from a list of canned replies (or a function), and records every request.</summary>
internal sealed class CannedHandler : HttpMessageHandler
{
    public List<(HttpMethod Method, string Url, string? Auth, string Body)> Calls { get; } = [];
    public Queue<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> Next { get; } = new();

    public void Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        Next.Enqueue((_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") }));

    public void Throw(Exception e) => Next.Enqueue((_, _) => throw e);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        Calls.Add((request.Method, request.RequestUri!.ToString(), request.Headers.TryGetValues("Authorization", out var a) ? a.Single() : null, body));
        if (Next.Count == 0) throw new InvalidOperationException("no canned reply left");
        return await Next.Dequeue()(request, cancellationToken);
    }
}

public sealed class CloudReaderTests : IDisposable
{
    private const string Access = "ACCESS-SECRET-7f3a";
    private const string Refresh = "REFRESH-SECRET-91bc";
    private const string Devices = "https://gw.bluettipower.com/api/bluiotdata/ha/v2/devices";
    private const string TokenUrl = "https://sso.bluettipower.com/oauth2/token";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bluetti-cloud-test-" + Guid.NewGuid().ToString("N"));
    private readonly CannedHandler _http = new();
    private readonly List<string> _log = [];
    private readonly ManualClock _clock = new(T0);

    public CloudReaderTests() => Directory.CreateDirectory(_dir);

    /// <summary>
    /// Every request of every test went through the canned handler (the reader's only client is built on it): each one is
    /// checked against the allow-list here (AC5), and every log line for tokens (AC8). xUnit fails the test on a throw here.
    /// </summary>
    public void Dispose()
    {
        try { AssertClean(); }
        finally
        {
            _cloud?.Dispose();
            _http.Dispose();
            if (File.Exists(TokenPath)) File.SetAttributes(TokenPath, FileAttributes.Normal);
            Directory.Delete(_dir, recursive: true);
        }
    }

    /// <summary>The two calls the cloud client may make (design.md §4 AC5).</summary>
    internal static bool Allowed(HttpMethod method, string url) =>
        (method == HttpMethod.Get && url == Devices) || (method == HttpMethod.Post && url == TokenUrl);

    [Fact]
    public void The_allow_list_refuses_any_other_call()
    {
        Assert.True(Allowed(HttpMethod.Get, Devices));
        Assert.True(Allowed(HttpMethod.Post, TokenUrl));
        Assert.False(Allowed(HttpMethod.Post, Devices));
        Assert.False(Allowed(HttpMethod.Get, TokenUrl));
        Assert.False(Allowed(HttpMethod.Post, "https://gw.bluettipower.com/api/bluiotdata/ha/v1/fulfillment"));
    }

    private string TokenPath => Path.Combine(_dir, "cloud-token.json");

    private StationConfig Config() => new() { DataDir = _dir };

    private void Token(long createdAt, long expiresIn = 2678400, string access = Access, string refresh = Refresh) =>
        File.WriteAllText(TokenPath, $$"""{"access_token":"{{access}}","refresh_token":"{{refresh}}","token_type":"bearer","expires_in":{{expiresIn}},"created_at":{{createdAt}}}""");

    private long Now => _clock.GetUtcNow().ToUnixTimeSeconds();

    private BluettiCloudReader? _cloud;

    /// <summary>One reader per test: its early-refresh hold lives across reads.</summary>
    private BluettiCloudReader Cloud => _cloud ??= new(Config(), _http, _clock, _log.Add);

    private static string State(string soc = "71", string grid = "0", string? acLoad = "169", string ctrlAc = "1") =>
        "[" + string.Join(",", new[]
        {
            $$"""{"fnCode":"SOC","fnValue":"{{soc}}"}""", $$"""{"fnCode":"GridAllTotalPower","fnValue":"{{grid}}"}""",
            $$"""{"fnCode":"SetCtrlAc","fnValue":"{{ctrlAc}}"}""", """{"fnCode":"PVAllTotalPower","fnValue":"5"}""",
            """{"fnCode":"DCLoadAllTotalPower","fnValue":"7"}""",
            acLoad is null ? null : $$"""{"fnCode":"ACLoadAllTotalPower","fnValue":"{{acLoad}}"}""",
        }.Where(s => s is not null)) + "]";

    private static string Answer(string stateList, string online = "1", string model = "AC200PL-AC200PL", int msgCode = 0) =>
        $$"""{"msgCode":{{msgCode}},"message":"OK","data":[{"sn":"SERIAL-1","online":"{{online}}","model":"{{model}}","stateList":{{stateList}}}]}""";

    private void AssertClean()
    {
        foreach (var line in _log)
        {
            Assert.DoesNotContain(Access, line, StringComparison.Ordinal);
            Assert.DoesNotContain(Refresh, line, StringComparison.Ordinal);
            Assert.DoesNotContain("NEW-ACCESS", line, StringComparison.Ordinal);
            Assert.DoesNotContain("NEW-REFRESH", line, StringComparison.Ordinal);
        }
        foreach (var c in _http.Calls)
            Assert.True(Allowed(c.Method, c.Url), $"{c.Method} {c.Url} is not allowed");
    }

    // design.md §4 AC4: a token pair the server issued is never thrown away
    [Fact]
    public async Task A_refreshed_token_that_cannot_be_written_is_used_and_saved_next_poll()
    {
        Token(Now - 2678400 + 86400);
        File.SetAttributes(TokenPath, FileAttributes.ReadOnly);   // the replace fails
        Refreshed();
        _http.Json(Answer(State()));
        Assert.NotNull(await Cloud.ReadAsync(CancellationToken.None));
        Assert.Equal("NEW-ACCESS", _http.Calls[1].Auth);
        Assert.Contains(_log, l => l.Contains("could not be written", StringComparison.Ordinal));
        Assert.Equal(Access, TokenFile().GetProperty("access_token").GetString());

        File.SetAttributes(TokenPath, FileAttributes.Normal);
        _log.Clear();
        _clock.Now = T0.AddMinutes(1);
        _http.Json(Answer(State(soc: "70")));
        Assert.NotNull(await Cloud.ReadAsync(CancellationToken.None));
        Assert.Equal(3, _http.Calls.Count);   // no second refresh: the kept pair is fresh
        Assert.Equal("NEW-ACCESS", _http.Calls[2].Auth);
        var f = TokenFile();
        Assert.Equal("NEW-ACCESS", f.GetProperty("access_token").GetString());
        Assert.Equal("NEW-REFRESH", f.GetProperty("refresh_token").GetString());
        Assert.Contains(_log, l => l.Contains("written", StringComparison.Ordinal));
    }

    // design.md §4 AC9: a frozen cloud cache is a miss
    [Fact]
    public async Task An_unchanged_soc_under_load_off_mains_for_30_minutes_is_stale()
    {
        Token(Now);
        _http.Json(Answer(State(soc: "71")));
        Assert.NotNull(await Cloud.ReadAsync(CancellationToken.None));
        _clock.Now = T0.AddMinutes(29);
        _http.Json(Answer(State(soc: "71")));
        Assert.NotNull(await Cloud.ReadAsync(CancellationToken.None));
        _clock.Now = T0.AddMinutes(30);
        _http.Json(Answer(State(soc: "71")));
        Assert.Null(await Cloud.ReadAsync(CancellationToken.None));
        Assert.Contains("cloud reading stale", Assert.Single(_log), StringComparison.Ordinal);
        _clock.Now = T0.AddMinutes(31);
        _http.Json(Answer(State(soc: "70")));
        Assert.Equal(70, (await Cloud.ReadAsync(CancellationToken.None))?.Soc);
    }

    [Theory]
    [InlineData("0", "99")]    // a light load may hold the SOC for long
    [InlineData("250", "169")] // on mains the SOC may hold at full
    public async Task An_unchanged_soc_on_mains_or_light_load_is_not_stale(string grid, string acLoad)
    {
        Token(Now);
        _http.Json(Answer(State(grid: grid, acLoad: acLoad)));
        Assert.NotNull(await Cloud.ReadAsync(CancellationToken.None));
        _clock.Now = T0.AddMinutes(45);
        _http.Json(Answer(State(grid: grid, acLoad: acLoad)));
        Assert.NotNull(await Cloud.ReadAsync(CancellationToken.None));
        Assert.Empty(_log);
    }

    [Fact]
    public async Task The_stale_limits_come_from_the_config()
    {
        Token(Now);
        _cloud = new(new StationConfig { DataDir = _dir, StaleLoadW = 50, StaleAfterMin = 5 }, _http, _clock, _log.Add);
        _http.Json(Answer(State(acLoad: "60")));
        Assert.NotNull(await Cloud.ReadAsync(CancellationToken.None));
        _clock.Now = T0.AddMinutes(5);
        _http.Json(Answer(State(acLoad: "60")));
        Assert.Null(await Cloud.ReadAsync(CancellationToken.None));
        Assert.Equal(100, new StationConfig().StaleLoadW);
        Assert.Equal(30, new StationConfig().StaleAfterMin);
    }

    // design.md §4 AC3: a torn or zeroed token file (a hard power loss) is named
    [Theory]
    [InlineData("")]
    [InlineData("\0\0\0\0")]
    [InlineData("{\"access_token\":\"ACC")]
    public async Task A_token_file_that_is_not_json_is_logged_as_such(string content)
    {
        await File.WriteAllTextAsync(TokenPath, content);
        Assert.Null(await Cloud.ReadAsync(CancellationToken.None));
        Assert.Contains("token file is not JSON", Assert.Single(_log), StringComparison.Ordinal);
        Assert.Empty(_http.Calls);
    }

    // design.md §4 AC8
    [Fact]
    public void A_token_never_prints()
    {
        string s = new CloudToken(Access, Refresh, "bearer", 1, 2).ToString();
        Assert.DoesNotContain(Access, s, StringComparison.Ordinal);
        Assert.DoesNotContain(Refresh, s, StringComparison.Ordinal);
    }

    // design.md §4 AC2
    [Fact]
    public async Task A_station_on_battery_is_a_reading_from_the_cloud()
    {
        Token(Now);
        _http.Json(Answer(State()));
        var r = await Cloud.ReadAsync(CancellationToken.None);
        Assert.Equal(new Reading(71, 0, 169, "INVERTER_OUTPUT", Source: "cloud"), r);
        var call = Assert.Single(_http.Calls);
        Assert.Equal(Access, call.Auth);   // raw, no "Bearer"
        Assert.Equal("", StationDecision.Check(r!, new StationState(), T0, Defaults()));
        AssertClean();
    }

    // design.md §4 AC2, AC2a
    [Theory]
    [InlineData("250", "169", "1", "CLOUD_MAINS")]
    [InlineData("250", "0", "0", "CLOUD_MAINS")]
    [InlineData("0", "0", "1", null)]
    [InlineData("0", "169", "0", null)]
    public async Task The_mode_is_derived_or_unknown(string grid, string acLoad, string ctrlAc, string? mode)
    {
        Token(Now);
        _http.Json(Answer(State(grid: grid, acLoad: acLoad, ctrlAc: ctrlAc)));
        var r = await Cloud.ReadAsync(CancellationToken.None);
        Assert.NotNull(r);
        Assert.Equal(mode, r.Mode);
        Assert.Equal(double.Parse(grid, CultureInfo.InvariantCulture), r.InAc);
        Assert.Null(r.InDc);
        Assert.Null(r.OutDc);
        AssertClean();
    }

    // design.md §4 AC2: no AC output fnCode = OutAc null, and no derived inverter mode
    [Fact]
    public async Task No_ac_load_code_leaves_out_ac_null()
    {
        Token(Now);
        _http.Json(Answer(State(acLoad: null)));
        var r = await Cloud.ReadAsync(CancellationToken.None);
        Assert.NotNull(r);
        Assert.Null(r.OutAc);
        Assert.Null(r.Mode);
        Assert.Equal(71, r.Soc);
    }

    // design.md §4 AC3
    [Theory]
    [InlineData("offline")]
    [InlineData("msgCode")]
    [InlineData("soc-text")]
    [InlineData("soc-missing")]
    [InlineData("other-model")]
    [InlineData("http-500")]
    [InlineData("http-throws")]
    [InlineData("not-json")]
    public async Task A_bad_answer_is_a_miss_with_one_log_line(string kind)
    {
        Token(Now);
        switch (kind)
        {
            case "offline": _http.Json(Answer(State(), online: "0")); break;
            case "msgCode": _http.Json(Answer(State(), msgCode: 500)); break;
            case "soc-text": _http.Json(Answer(State(soc: "n/a"))); break;
            case "soc-missing": _http.Json(Answer("""[{"fnCode":"GridAllTotalPower","fnValue":"0"}]""")); break;
            case "other-model": _http.Json(Answer(State(), model: "EB3A-EB3A")); break;
            case "http-500": _http.Json($$"""{"echo":"{{Access}}"}""", HttpStatusCode.InternalServerError); break;
            case "http-throws": _http.Throw(new HttpRequestException($"connection reset, token {Access}")); break;
            case "not-json": _http.Json($"<html>{Access}</html>"); break;
        }
        Assert.Null(await Cloud.ReadAsync(CancellationToken.None));
        var line = Assert.Single(_log);
        Assert.StartsWith("cloud:", line, StringComparison.Ordinal);
        AssertClean();
    }

    // design.md §4 AC3: 10 s cap
    [Fact]
    public async Task A_slow_answer_is_a_miss()
    {
        Token(Now);
        Cloud.Timeout = TimeSpan.FromMilliseconds(200);
        _http.Next.Enqueue(async (_, ct) => { await Task.Delay(TimeSpan.FromSeconds(30), ct); return new HttpResponseMessage(HttpStatusCode.OK); });
        Assert.Null(await Cloud.ReadAsync(CancellationToken.None));
        Assert.Contains("timed out", Assert.Single(_log), StringComparison.Ordinal);
    }

    [Fact]
    public void The_cap_is_10_seconds() => Assert.Equal(TimeSpan.FromSeconds(10), Cloud.Timeout);

    // design.md §4 AC3: token file missing or unreadable
    [Theory]
    [InlineData(null)]
    [InlineData("{ not json")]
    [InlineData("""{"refresh_token":"x"}""")]
    public async Task No_usable_token_file_is_a_miss_and_no_call(string? content)
    {
        if (content is not null) await File.WriteAllTextAsync(TokenPath, content);
        Assert.Null(await Cloud.ReadAsync(CancellationToken.None));
        Assert.Empty(_http.Calls);
        Assert.Contains("token", Assert.Single(_log), StringComparison.Ordinal);
    }

    private void Refreshed(string? refresh = "NEW-REFRESH") =>
        _http.Json(refresh is null
            ? """{"access_token":"NEW-ACCESS","token_type":"bearer","expires_in":2678400}"""
            : $$"""{"access_token":"NEW-ACCESS","refresh_token":"{{refresh}}","token_type":"bearer","expires_in":2678400}""");

    private JsonElement TokenFile() => JsonDocument.Parse(File.ReadAllText(TokenPath)).RootElement;

    // design.md §4 AC4: early refresh, written atomically, the new token used
    [Fact]
    public async Task A_token_expiring_within_3_days_is_refreshed_first()
    {
        Token(Now - 2678400 + (2 * 86400));   // 2 days left
        Refreshed();
        _http.Json(Answer(State()));
        Assert.NotNull(await Cloud.ReadAsync(CancellationToken.None));
        Assert.Equal(2, _http.Calls.Count);
        var post = _http.Calls[0];
        Assert.Equal(HttpMethod.Post, post.Method);
        Assert.Contains("grant_type=refresh_token", post.Body, StringComparison.Ordinal);
        Assert.Contains("refresh_token=" + Refresh, post.Body, StringComparison.Ordinal);
        Assert.Contains("client_id=HomeAssistant", post.Body, StringComparison.Ordinal);
        Assert.Equal("NEW-ACCESS", _http.Calls[1].Auth);
        var f = TokenFile();
        Assert.Equal("NEW-ACCESS", f.GetProperty("access_token").GetString());
        Assert.Equal("NEW-REFRESH", f.GetProperty("refresh_token").GetString());
        Assert.Equal(Now, f.GetProperty("created_at").GetInt64());
        Assert.Equal(2678400, f.GetProperty("expires_in").GetInt64());
        Assert.Single(Directory.GetFiles(_dir));   // no temp file left
        AssertClean();
    }

    [Fact]
    public async Task A_token_with_4_days_left_is_not_refreshed()
    {
        Token(Now - 2678400 + (4 * 86400));
        _http.Json(Answer(State()));
        Assert.NotNull(await Cloud.ReadAsync(CancellationToken.None));
        Assert.Equal(HttpMethod.Get, Assert.Single(_http.Calls).Method);
    }

    // design.md §4 AC4: a refresh answer without a refresh token keeps the old one
    [Fact]
    public async Task A_refresh_without_a_new_refresh_token_keeps_the_old()
    {
        Token(Now - 2678400 + 3600);
        Refreshed(refresh: null);
        _http.Json(Answer(State()));
        Assert.NotNull(await Cloud.ReadAsync(CancellationToken.None));
        Assert.Equal(Refresh, TokenFile().GetProperty("refresh_token").GetString());
        AssertClean();
    }

    // design.md §4 AC4: 805 = refresh once, retry once
    [Fact]
    public async Task Expired_805_refreshes_and_retries_once()
    {
        Token(Now);
        _http.Json(Answer("[]", msgCode: 805));
        Refreshed();
        _http.Json(Answer(State()));
        var r = await Cloud.ReadAsync(CancellationToken.None);
        Assert.Equal(71, r?.Soc);
        Assert.Equal([HttpMethod.Get, HttpMethod.Post, HttpMethod.Get], _http.Calls.Select(c => c.Method));
        Assert.Equal("NEW-ACCESS", _http.Calls[2].Auth);
        AssertClean();
    }

    [Fact]
    public async Task Expired_805_twice_is_a_miss_after_one_retry()
    {
        Token(Now);
        _http.Json(Answer("[]", msgCode: 805));
        Refreshed();
        _http.Json(Answer("[]", msgCode: 805));
        Assert.Null(await Cloud.ReadAsync(CancellationToken.None));
        Assert.Equal(3, _http.Calls.Count);
        Assert.Single(_log);
        AssertClean();
    }

    // design.md §4 AC4: a failed refresh after 805 = a miss, logged
    [Theory]
    [InlineData("""{"error":"invalid_grant"}""", HttpStatusCode.BadRequest)]
    [InlineData("""{"token_type":"bearer"}""", HttpStatusCode.OK)]
    public async Task A_failed_refresh_after_805_is_a_miss(string body, HttpStatusCode status)
    {
        Token(Now);
        _http.Json(Answer("[]", msgCode: 805));
        _http.Json(body, status);
        Assert.Null(await Cloud.ReadAsync(CancellationToken.None));
        Assert.Contains("refresh", Assert.Single(_log), StringComparison.Ordinal);
        Assert.Equal(Access, TokenFile().GetProperty("access_token").GetString());
        AssertClean();
    }

    // design.md §4 AC4: a failed early refresh uses the current token; the next early try waits an hour
    [Fact]
    public async Task A_failed_early_refresh_reads_with_the_current_token_and_waits_an_hour()
    {
        Token(Now - 2678400 + 86400);
        var reader = Cloud;
        _http.Json("""{"error":"server"}""", HttpStatusCode.BadGateway);
        _http.Json(Answer(State()));
        Assert.NotNull(await reader.ReadAsync(CancellationToken.None));
        Assert.Equal(Access, _http.Calls[1].Auth);
        Assert.Contains("refresh", Assert.Single(_log), StringComparison.Ordinal);

        _clock.Now = T0.AddMinutes(30);
        _http.Json(Answer(State(soc: "70")));   // a moving SOC: not stale (AC9)
        Assert.NotNull(await reader.ReadAsync(CancellationToken.None));
        Assert.Equal(3, _http.Calls.Count);   // no second POST within the hour

        _clock.Now = T0.AddMinutes(61);
        Refreshed();
        _http.Json(Answer(State(soc: "69")));
        Assert.NotNull(await reader.ReadAsync(CancellationToken.None));
        Assert.Equal(HttpMethod.Post, _http.Calls[3].Method);
        AssertClean();
    }

    // design.md §4 AC5: the only URLs in the client are the two allowed ones
    [Fact]
    public void The_client_holds_only_the_allowed_urls()
    {
        var urls = typeof(BluettiCloudReader).GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.FieldType == typeof(string)).Select(f => (string?)f.GetValue(null))
            .Where(v => v is not null && v.Contains("://", StringComparison.Ordinal)).Select(v => v!).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal([Devices, TokenUrl], urls);
    }

    // design.md §4 AC1
    [Fact]
    public async Task Bluetooth_answering_never_calls_the_cloud()
    {
        var bt = new QueueReader { Default = new Reading(50, 0, 100, "INVERTER_OUTPUT") };
        var cloud = new QueueReader { Default = new Reading(70, 0, 100, "INVERTER_OUTPUT") };
        var r = await new FallbackReader(bt, cloud).ReadAsync(CancellationToken.None);
        Assert.Equal(50, r?.Soc);
        Assert.Equal("bluetooth", r?.Source);
        Assert.Equal(0, cloud.Reads);
    }

    // design.md §4 AC2
    [Fact]
    public async Task Bluetooth_silent_falls_back_to_the_cloud()
    {
        var bt = new QueueReader();
        var cloud = new QueueReader { Default = new Reading(70, 0, 100, "INVERTER_OUTPUT") };
        var r = await new FallbackReader(bt, cloud).ReadAsync(CancellationToken.None);
        Assert.Equal(70, r?.Soc);
        Assert.Equal("cloud", r?.Source);
        Assert.Equal(1, cloud.Reads);
        Assert.Null(await new FallbackReader(bt, null).ReadAsync(CancellationToken.None));
    }

    // design.md §4 Cloud fallback: on when the token file exists, unless cloudEnabled is false
    [Fact]
    public void The_cloud_is_on_when_the_token_file_exists()
    {
        Assert.False(Config().CloudOn);
        Token(Now);
        Assert.True(Config().CloudOn);
        Assert.Equal(TokenPath, Config().CloudTokenFile);
        File.WriteAllText(Path.Combine(_dir, "config.json"), """{ "cloudEnabled": false }""");
        Assert.False(StationConfig.Load(["--data-dir", _dir]).CloudOn);
        File.WriteAllText(Path.Combine(_dir, "config.json"), """{ "cloudTokenPath": "C:\\x\\t.json" }""");
        Assert.Equal(@"C:\x\t.json", StationConfig.Load(["--data-dir", _dir]).CloudTokenFile);
    }
}

/// <summary>design.md §4 AC6: the source of an accepted reading reaches the log and /api/health.</summary>
public sealed class CloudSourceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bluetti-source-test-" + Guid.NewGuid().ToString("N"));
    private readonly StationStore _store;
    private readonly QueueReader _reader = new();
    private readonly List<string> _log = [];
    private readonly ManualClock _clock = new(T0);
    private readonly StationService _service;
    private readonly StationApi _api;

    public CloudSourceTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new StationStore(Path.Combine(_dir, "station.db"));
        var cfg = new StationConfig { DataDir = _dir };
        _service = new StationService(cfg, _reader, new FakeActions(), new FakeHibernator(true), new FixedFlag(FlagView.Normal), _store, _clock, _log.Add);
        _api = new StationApi(cfg, _service, _store, _clock, T0);
    }

    public void Dispose()
    {
        _service.Dispose();
        _store.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private async Task<string?> LastSource()
    {
        var r = await _api.HandleAsync(new HttpRequest("GET", "/api/health", new Dictionary<string, string>(), new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Host"] = "127.0.0.1:18940" }, []), CancellationToken.None);
        Assert.Equal(200, r.Status);
        var p = JsonDocument.Parse(r.Body).RootElement.GetProperty("lastSource");
        return p.ValueKind == JsonValueKind.Null ? null : p.GetString();
    }

    [Fact]
    public async Task The_source_is_logged_and_in_health()
    {
        Assert.Null(await LastSource());
        _reader.Next.Enqueue(new Reading(60, 300, 100, "CLOUD_MAINS", Source: "cloud"));
        await _service.PollAsync(CancellationToken.None);
        Assert.Equal("cloud", await LastSource());
        Assert.Contains(_log, l => l.Contains("from the cloud", StringComparison.Ordinal));

        _log.Clear();
        _clock.Now = T0.AddMinutes(1);
        _reader.Next.Enqueue(new Reading(60, 300, 100, "BYPASS_OUTPUT_C", Source: "bluetooth"));
        await _service.PollAsync(CancellationToken.None);
        Assert.Equal("bluetooth", await LastSource());
        Assert.Contains(_log, l => l.Contains("from bluetooth", StringComparison.Ordinal));

        _log.Clear();
        _clock.Now = T0.AddMinutes(2);
        _reader.Next.Enqueue(new Reading(60, 300, 100, "BYPASS_OUTPUT_C", Source: "bluetooth"));
        await _service.PollAsync(CancellationToken.None);
        Assert.DoesNotContain(_log, l => l.Contains("bluetooth", StringComparison.Ordinal));   // Bluetooth again: no line per reading
    }

    [Fact]
    public async Task A_rejected_reading_names_its_source()
    {
        _reader.Next.Enqueue(new Reading(60, 300, 100, "CLOUD_MAINS", Source: "cloud"));
        await _service.PollAsync(CancellationToken.None);
        _clock.Now = T0.AddMinutes(1);
        _reader.Next.Enqueue(new Reading(10, 300, 100, "CLOUD_MAINS", Source: "cloud"));   // 50 points in a minute
        await _service.PollAsync(CancellationToken.None);
        Assert.Contains(_log, l => l.StartsWith("reading rejected", StringComparison.Ordinal) && l.Contains("cloud", StringComparison.Ordinal));
    }
}
