using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using BluettiWatcher.Http;
using BluettiWatcher.Storage;
using static BluettiWatcher.Tests.Fx;

namespace BluettiWatcher.Tests;

public sealed class ApiTests : IDisposable
{
    private const int Port = 18940;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bluetti-watcher-test-" + Guid.NewGuid().ToString("N"));
    private readonly StationStore _store;
    private readonly FakeActions _actions = new();
    private readonly StationService _service;
    private readonly StationApi _api;

    public ApiTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new StationStore(Path.Combine(_dir, "station.db"));
        var cfg = new StationConfig { DataDir = _dir, Port = Port };
        var clock = new ManualClock(T0);
        _service = new StationService(cfg, new QueueReader(), _actions, new FakeHibernator(true), new FixedFlag(Station), _store, clock, _ => { });
        _api = new StationApi(cfg, _service, _store, clock, T0);
    }

    public void Dispose()
    {
        _service.Dispose();
        _store.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private static HttpRequest Req(string method, string path, string? body = null, params (string, string)[] headers)
    {
        var h = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in headers) h[k] = v;
        string q = path.Contains('?', StringComparison.Ordinal) ? path[(path.IndexOf('?', StringComparison.Ordinal) + 1)..] : "";
        var query = q.Split('&', StringSplitOptions.RemoveEmptyEntries).Select(kv => kv.Split('=')).ToDictionary(a => a[0], a => a.Length > 1 ? a[1] : "");
        return new HttpRequest(method, path.Split('?')[0], query, h, Encoding.UTF8.GetBytes(body ?? ""));
    }

    private const string Eco = """{ "mode": "eco" }""";
    private static readonly (string, string) Host = ("Host", $"127.0.0.1:{Port}");
    private static readonly (string, string) XStation = ("X-Station", "1");

    private Task<HttpResponse> Post(params (string, string)[] headers) => _api.HandleAsync(Req("POST", "/api/mode", Eco, headers), CancellationToken.None);

    // api.md Guard
    [Fact]
    public async Task Post_without_the_header_is_refused()
    {
        var r = await Post(Host);
        Assert.Equal(403, r.Status);
        Assert.Empty(_actions.Calls);
    }

    // api.md Guard
    [Fact]
    public async Task Post_with_a_wrong_header_value_is_refused() => Assert.Equal(403, (await Post(Host, ("X-Station", "true"))).Status);

    // api.md Guard
    [Theory]
    [InlineData("evil.example")]
    [InlineData("127.0.0.1")]
    [InlineData("127.0.0.1:18941")]
    [InlineData("localhost.evil.example:18940")]
    [InlineData("localhost:18941")]   // api.md Guard: localhost only on the service's own port
    [InlineData("localhost")]
    [InlineData("")]
    public async Task Post_with_a_foreign_Host_is_refused(string host)
    {
        var r = await Post(("Host", host), XStation);
        Assert.Equal(403, r.Status);
        Assert.Empty(_actions.Calls);
    }

    // api.md Guard
    [Fact]
    public async Task Post_without_Host_is_refused() => Assert.Equal(403, (await Post(XStation)).Status);

    // api.md Guard
    [Theory]
    [InlineData("http://evil.example")]
    [InlineData("null")]
    [InlineData("http://127.0.0.1:18941")]
    [InlineData("https://127.0.0.1:18940")]
    public async Task Post_with_a_foreign_Origin_is_refused(string origin)
    {
        var r = await Post(Host, XStation, ("Origin", origin));
        Assert.Equal(403, r.Status);
        Assert.Empty(_actions.Calls);
    }

    // api.md Guard
    [Theory]
    [InlineData("http://127.0.0.1:18940")]
    [InlineData("http://localhost:18940")]
    public async Task Post_with_the_own_Origin_is_accepted(string origin)
    {
        var r = await Post(Host, XStation, ("Origin", origin));
        Assert.Equal(202, r.Status);
        Assert.Empty(_actions.Calls);   // the service loop runs the command, not the request
        Assert.Equal(Decision.ManualMode.Eco, _store.LoadState().Manual);
    }

    // api.md Guard
    [Fact]
    public async Task Post_without_Origin_and_with_localhost_Host_is_accepted() =>
        Assert.Equal(202, (await Post(("Host", $"localhost:{Port}"), XStation)).Status);

    [Theory]
    [InlineData("""{ "mode": "turbo" }""")]
    [InlineData("""not json""")]
    [InlineData("")]
    public async Task Post_with_a_bad_body_is_400(string body) =>
        Assert.Equal(400, (await _api.HandleAsync(Req("POST", "/api/mode", body, Host, XStation), CancellationToken.None)).Status);

    // api.md Guard
    [Fact]
    public async Task Get_on_the_write_route_is_405() =>
        Assert.Equal(405, (await _api.HandleAsync(Req("GET", "/api/mode", null, Host, XStation), CancellationToken.None)).Status);

    // api.md Guard
    [Fact]
    public async Task Get_with_a_foreign_Host_is_refused() =>
        Assert.Equal(403, (await _api.HandleAsync(Req("GET", "/api/now", null, ("Host", "evil.example:18940")), CancellationToken.None)).Status);

    // api.md Guard
    [Fact]
    public void Repeated_Host_header_is_a_bad_request() =>
        Assert.Equal(400, MiniHttpServer.ParseHead("POST /api/mode HTTP/1.1\r\nHost: evil.example\r\nHost: 127.0.0.1:18940").Bad?.Status);

    // api.md /api/history and /api/events
    [Fact]
    public async Task Page_and_api_routes_answer()
    {
        _store.AddReading(T0.AddMinutes(-1), Bat(80), ReadingStatus.Accepted, "");
        var page = await _api.HandleAsync(Req("GET", "/", null, Host), CancellationToken.None);
        Assert.Equal(200, page.Status);
        Assert.Contains("/api/now", Encoding.UTF8.GetString(page.Body), StringComparison.Ordinal);
        foreach (var path in new[] { "/api/now", "/api/health", "/api/history?step=60", "/api/events" })
        {
            var r = await _api.HandleAsync(Req("GET", path, null, Host), CancellationToken.None);
            Assert.Equal(200, r.Status);
            using var doc = JsonDocument.Parse(r.Body);
        }
        var hist = await _api.HandleAsync(Req("GET", "/api/history?step=60", null, Host), CancellationToken.None);
        using var h = JsonDocument.Parse(hist.Body);
        Assert.Equal(80, h.RootElement.GetProperty("rows")[0].GetProperty("soc").GetDouble());
        Assert.Equal(400, (await _api.HandleAsync(Req("GET", "/api/history?from=yesterday", null, Host), CancellationToken.None)).Status);
        Assert.Equal(404, (await _api.HandleAsync(Req("GET", "/nope", null, Host), CancellationToken.None)).Status);
    }

    // design.md §5 Mains, eco and manual control
    [Fact]
    public async Task Max_with_an_emergency_on_is_shown_as_held_back()
    {
        var flag = new FixedFlag(Station);
        var clock = new ManualClock(T0);
        var cfg = new StationConfig { DataDir = _dir, Port = Port };
        using var s = new StationService(cfg, new QueueReader { Default = Bat(80) }, new FakeActions(), new FakeHibernator(true), flag, _store, clock, _ => { });
        await s.SetModeAsync(Decision.ManualMode.Max, CancellationToken.None);
        flag.View = Flag("emergency", "station");
        await s.PollAsync(CancellationToken.None);
        var r = await new StationApi(cfg, s, _store, clock, T0).HandleAsync(Req("GET", "/api/now", null, Host), CancellationToken.None);
        using var doc = JsonDocument.Parse(r.Body);
        Assert.StartsWith("Max held back: battery emergency", doc.RootElement.GetProperty("note").GetString(), StringComparison.Ordinal);
    }
    // design.md §6 Battery levels and hibernate
    [Fact]
    public async Task Hibernate_held_back_by_a_rebase_is_in_now_and_in_the_note()
    {
        var clock = new ManualClock(T0);
        var cfg = new StationConfig { DataDir = _dir, Port = Port };
        var reader = new QueueReader { Default = Bat(22) };
        using var s = new StationService(cfg, reader, new FakeActions(), new FakeHibernator(true), new FixedFlag(Station), _store, clock, _ => { });
        for (int minute = 0; minute <= 17; minute++)
        {
            if (minute == 6) reader.Default = Bat(4);
            clock.Now = T0.AddMinutes(minute);
            await s.PollAsync(CancellationToken.None);
        }
        var r = await new StationApi(cfg, s, _store, clock, T0).HandleAsync(Req("GET", "/api/now", null, Host), CancellationToken.None);
        using var doc = JsonDocument.Parse(r.Body);
        Assert.False(string.IsNullOrEmpty(doc.RootElement.GetProperty("hibernateDelayedUntil").GetString()));
        Assert.Contains("hibernate held back until", doc.RootElement.GetProperty("note").GetString(), StringComparison.Ordinal);
    }

    // design.md §5 Mains, eco and manual control
    [Fact]
    public async Task Stored_mode_refusal_is_in_modeError_after_a_restart_and_not_repeated_in_the_note()
    {
        var st = _store.LoadState();
        st.ModeError = StationService.MaxHeldBack;
        _store.SaveState(st);
        var cfg = new StationConfig { DataDir = _dir, Port = Port };
        var clock = new ManualClock(T0);
        using var s = new StationService(cfg, new QueueReader(), new FakeActions(), new FakeHibernator(true), new FixedFlag(Station), _store, clock, _ => { });
        var r = await new StationApi(cfg, s, _store, clock, T0).HandleAsync(Req("GET", "/api/now", null, Host), CancellationToken.None);
        using var doc = JsonDocument.Parse(r.Body);
        Assert.Equal(StationService.MaxHeldBack, doc.RootElement.GetProperty("modeError").GetString());
        // one channel: the page shows modeError beside the note; the note never repeats it
        Assert.DoesNotContain(StationService.MaxHeldBack, doc.RootElement.GetProperty("note").GetString(), StringComparison.Ordinal);
    }

    // api.md Guard: every route, GET included, needs the Host on the service's own port.
    [Theory]
    [InlineData("localhost:18941")]
    [InlineData("127.0.0.1:18941")]
    public async Task Get_with_the_right_host_on_another_port_is_refused(string host) =>
        Assert.Equal(403, (await _api.HandleAsync(Req("GET", "/api/now", null, ("Host", host)), CancellationToken.None)).Status);

    // api.md Guard: POST /api/mode is the only write route: a POST elsewhere is 405 and changes nothing.
    [Theory]
    [InlineData("/api/now")]
    [InlineData("/api/health")]
    [InlineData("/")]
    public async Task Post_to_any_other_route_is_405_and_changes_nothing(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var before = Encoding.UTF8.GetString(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(_store.LoadState(), StationJson.Default.StationState));
        var r = await _api.HandleAsync(Req("POST", path, Eco, Host, XStation), CancellationToken.None);
        Assert.Equal(405, r.Status);
        Assert.Empty(_actions.Calls);
        Assert.Equal(before, Encoding.UTF8.GetString(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(_store.LoadState(), StationJson.Default.StationState)));
        Assert.Empty(_store.Events(0, long.MaxValue));
    }

    // design.md §5 Mains, eco and manual control: the page has the Auto / Eco / Max buttons and posts the choice to /api/mode with X-Station: 1.
    [Fact]
    public async Task Page_has_Auto_Eco_Max_buttons_posting_to_api_mode_with_the_header()
    {
        string page = Encoding.UTF8.GetString((await _api.HandleAsync(Req("GET", "/", null, Host), CancellationToken.None)).Body);
        Assert.Contains("data-m=\"auto\"", page, StringComparison.Ordinal);
        Assert.Contains("data-m=\"eco\"", page, StringComparison.Ordinal);
        Assert.Contains("data-m=\"max\"", page, StringComparison.Ordinal);
        Assert.Contains("fetch('/api/mode', { method: 'POST'", page, StringComparison.Ordinal);
        Assert.Contains("'X-Station': '1'", page, StringComparison.Ordinal);
    }

    private (StationService Service, StationApi Api, QueueReader Reader) Fresh(FakeActions? actions = null)
    {
        var clock = new ManualClock(T0);
        var cfg = new StationConfig { DataDir = _dir, Port = Port };
        var reader = new QueueReader();
        var s = new StationService(cfg, reader, actions ?? new FakeActions(), new FakeHibernator(true), new FixedFlag(Station), _store, clock, _ => { });
        return (s, new StationApi(cfg, s, _store, clock, T0), reader);
    }

    private static async Task<JsonDocument> GetJson(StationApi api, string path) =>
        JsonDocument.Parse((await api.HandleAsync(Req("GET", path, null, Host), CancellationToken.None)).Body);

    // design.md §7 Battery time: /api/now gives the hours of battery left at the current load on battery, null on mains.
    [Fact]
    public async Task Now_has_hours_left_on_battery_and_null_on_mains()
    {
        var (s, api, reader) = Fresh();
        using (s)
        {
            reader.Next.Enqueue(Bat(100));
            await s.PollAsync(CancellationToken.None);
            using (var doc = await GetJson(api, "/api/now")) Assert.Equal(8.9, doc.RootElement.GetProperty("hoursLeft").GetDouble());
            reader.Next.Enqueue(Grid(100));
            await s.PollAsync(CancellationToken.None);
            using (var doc = await GetJson(api, "/api/now")) Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("hoursLeft").ValueKind);
        }
    }

    // api.md /api/history and /api/events: /api/history and /api/events answer only what lies in [from, to] (ISO or Unix ms).
    [Fact]
    public async Task History_and_events_keep_only_the_from_to_window()
    {
        foreach (int m in new[] { -30, -20, -10, -1 })
        {
            _store.AddReading(T0.AddMinutes(m), Bat(80 + m), ReadingStatus.Accepted, "");
            _store.AddEvent(T0.AddMinutes(m), "mode", $"at {m}");
        }
        long from = T0.AddMinutes(-25).ToUnixTimeMilliseconds(), to = T0.AddMinutes(-10).ToUnixTimeMilliseconds();
        using (var h = await GetJson(_api, $"/api/history?from={from}&to={to}&step=60"))
            Assert.Equal([60.0, 70.0], h.RootElement.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("soc").GetDouble()));
        using (var e = await GetJson(_api, $"/api/events?from={from}&to={to}"))
            Assert.Equal(["at -20", "at -10"], e.RootElement.GetProperty("events").EnumerateArray().Select(x => x.GetProperty("detail").GetString()));
        string isoFrom = T0.AddMinutes(-15).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
        using (var e = await GetJson(_api, $"/api/events?from={isoFrom}"))   // to defaults to now
            Assert.Equal(["at -10", "at -1"], e.RootElement.GetProperty("events").EnumerateArray().Select(x => x.GetProperty("detail").GetString()));
    }

    // design.md §6 Battery levels and hibernate: each failing action is an event the page reads from /api/events.
    [Fact]
    public async Task Failed_action_is_in_api_events()
    {
        var (s, api, reader) = Fresh(new FakeActions { FailBattery = true });
        using (s)
        {
            reader.Next.Enqueue(Bat(80));
            await s.PollAsync(CancellationToken.None);
            using var doc = await GetJson(api, "/api/events");
            var failed = Assert.Single(doc.RootElement.GetProperty("events").EnumerateArray(), x => x.GetProperty("kind").GetString() == "action-failed");
            Assert.StartsWith("battery failed:", failed.GetProperty("detail").GetString(), StringComparison.Ordinal);
        }
    }

    // design.md §7 Battery time
    [Fact]
    public void Hours_left_at_the_current_load_and_none_on_mains()
    {
        Assert.Equal(8.9, StationApi.HoursLeft(Bat(100), new StationConfig()));   // 2304 Wh x 0.85 / 220 W
        Assert.Null(StationApi.HoursLeft(Grid(100), new StationConfig()));
    }

    // design.md §11 DTEK module, rule 24
    [Fact]
    public void Hours_to_the_hibernate_level_at_the_current_load()
    {
        Assert.Equal(8.5, StationApi.HoursToHibernate(Bat(100), new StationConfig()));   // 2304 Wh x 95 % x 0.85 / 220 W
        Assert.Equal(0, StationApi.HoursToHibernate(Bat(4), new StationConfig()));        // under the level: none left
        Assert.Null(StationApi.HoursToHibernate(Grid(100), new StationConfig()));
    }

    // api.md Guard
    [Fact]
    public async Task Over_a_socket_the_guard_holds_and_no_CORS_header_is_sent()
    {
        int port;
        using (var probe = new TcpListener(IPAddress.Loopback, 0)) { probe.Start(); port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop(); }
        var cfg = new StationConfig { DataDir = _dir, Port = port };
        var api = new StationApi(cfg, _service, _store, TimeProvider.System, T0);
        using var server = new MiniHttpServer(IPAddress.Loopback, port, api.HandleAsync, _ => { });
        server.Start();
        using var cts = new CancellationTokenSource();
        var run = server.RunAsync(cts.Token);

        string Send(string raw)
        {
            using var c = new TcpClient();
            c.Connect(IPAddress.Loopback, port);
            var s = c.GetStream();
            s.Write(Encoding.ASCII.GetBytes(raw));
            using var rd = new StreamReader(s);
            return rd.ReadToEnd();
        }

        string refused = Send($"POST /api/mode HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nOrigin: http://evil.example\r\nX-Station: 1\r\nContent-Length: {Eco.Length}\r\n\r\n{Eco}");
        string ok = Send($"POST /api/mode HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nX-Station: 1\r\nContent-Length: {Eco.Length}\r\n\r\n{Eco}");
        string preflight = Send($"OPTIONS /api/mode HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nOrigin: http://evil.example\r\n\r\n");
        await cts.CancelAsync();
        await run;

        Assert.StartsWith("HTTP/1.1 403", refused, StringComparison.Ordinal);
        Assert.StartsWith("HTTP/1.1 202 Accepted\r\n", ok, StringComparison.Ordinal);
        Assert.StartsWith("HTTP/1.1 405", preflight, StringComparison.Ordinal);
        Assert.DoesNotContain("Access-Control-Allow", refused + ok + preflight, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("BYPASS_OUTPUT_D", "bypass output (D)")]
    [InlineData("BYPASS_OUTPUT_C", "bypass output (C)")]
    [InlineData("INVERTER_OUTPUT", "inverter output")]
    [InlineData("STOP", "stop")]
    [InlineData(null, "")]
    public void Mode_label_keeps_the_variant_letter(string? mode, string label) => Assert.Equal(label, StationApi.ModeLabel(mode));

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    private static string Get(int port, string path)
    {
        using var c = new TcpClient();
        c.Connect(IPAddress.Loopback, port);
        c.ReceiveTimeout = 15000;
        var s = c.GetStream();
        s.Write(Encoding.ASCII.GetBytes($"GET {path} HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\n\r\n"));
        using var rd = new StreamReader(s);
        return rd.ReadToEnd();
    }

    // api.md /api/history and /api/events
    [Theory]
    [InlineData("/api/history?from=99999999999999999")]
    [InlineData("/api/history?to=-62135596800000")]
    [InlineData("/api/events?to=99999999999999999")]
    public async Task Out_of_range_times_are_400_over_a_socket(string path)
    {
        int port = FreePort();
        var api = new StationApi(new StationConfig { DataDir = _dir, Port = port }, _service, _store, TimeProvider.System, T0);
        using var server = new MiniHttpServer(IPAddress.Loopback, port, api.HandleAsync, _ => { });
        server.Start();
        using var cts = new CancellationTokenSource();
        var run = server.RunAsync(cts.Token);
        string answer = Get(port, path);
        await cts.CancelAsync();
        await run;
        Assert.StartsWith("HTTP/1.1 400", answer, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("input", 400)]
    [InlineData("bug", 500)]
    public async Task A_throwing_handler_is_answered_and_logged(string kind, int status)
    {
        int port = FreePort();
        var log = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var server = new MiniHttpServer(IPAddress.Loopback, port,
            (_, _) => kind == "input" ? throw new ArgumentOutOfRangeException(nameof(kind)) : throw new InvalidOperationException("boom"), log.Enqueue);
        server.Start();
        using var cts = new CancellationTokenSource();
        var run = server.RunAsync(cts.Token);
        string answer = Get(port, "/api/now");
        await cts.CancelAsync();
        await run;
        Assert.StartsWith($"HTTP/1.1 {status}", answer, StringComparison.Ordinal);
        Assert.Contains(log, l => l.Contains(kind == "input" ? "ArgumentOutOfRangeException" : "InvalidOperationException", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Ten_idle_sockets_do_not_delay_a_real_request()
    {
        int port = FreePort();
        var api = new StationApi(new StationConfig { DataDir = _dir, Port = port }, _service, _store, TimeProvider.System, T0);
        using var server = new MiniHttpServer(IPAddress.Loopback, port, api.HandleAsync, _ => { });
        server.Start();
        using var cts = new CancellationTokenSource();
        var run = server.RunAsync(cts.Token);
        var idle = Enumerable.Range(0, 10).Select(_ => { var c = new TcpClient(); c.Connect(IPAddress.Loopback, port); return c; }).ToList();
        try
        {
            await Task.Delay(200);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            string answer = Get(port, "/api/health");
            sw.Stop();
            Assert.StartsWith("HTTP/1.1 200", answer, StringComparison.Ordinal);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1), $"took {sw.Elapsed}");
        }
        finally
        {
            foreach (var c in idle) c.Dispose();
            await cts.CancelAsync();
            await run;
        }
    }

    [Fact]
    public async Task An_idle_socket_is_closed_after_the_header_timeout()
    {
        int port = FreePort();
        using var server = new MiniHttpServer(IPAddress.Loopback, port, (_, _) => Task.FromResult(HttpResponse.Text(200, "ok")), _ => { },
            headTimeout: TimeSpan.FromMilliseconds(500));
        server.Start();
        using var cts = new CancellationTokenSource();
        var run = server.RunAsync(cts.Token);
        using var c = new TcpClient();
        await c.ConnectAsync(IPAddress.Loopback, port);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int n;
        try { n = await c.GetStream().ReadAsync(new byte[16]).AsTask().WaitAsync(TimeSpan.FromSeconds(4)); }
        catch (IOException) { n = 0; }   // a reset is a close too
        sw.Stop();
        await cts.CancelAsync();
        await run;
        Assert.Equal(0, n);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"took {sw.Elapsed}");
    }
}
