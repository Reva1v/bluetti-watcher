using System.Text.Json;
using BluettiWatcher.Decision;
using BluettiWatcher.DtekWatcher;
using BluettiWatcher.Http;
using BluettiWatcher.Storage;
using static BluettiWatcher.Tests.DtekWatcher.Dx;

namespace BluettiWatcher.Tests.DtekWatcher;

/// <summary>A module that throws from everything, and a store that can be made to.</summary>
internal sealed class ThrowingWatcher : IDtekWatcher
{
    public string Message { get; set; } = "boom";
    public bool Throws { get; set; } = true;
    public Task RunAsync(CancellationToken ct) => Throws ? throw new InvalidOperationException(Message) : Task.CompletedTask;
    public void AfterPoll(StationState state) { if (Throws) throw new ArgumentOutOfRangeException(nameof(state), Message); }
    public DtekView View(bool cut, double? hoursLeft, double? hoursToHibernate = null) => Throws ? throw new InvalidOperationException(Message) : DtekView.Broken(new PageNote(PageCodes.DtekModuleError, "fine"));
    public void Dispose() { }
}

internal sealed class FlakyStore(StationStore inner) : IDtekStore
{
    public bool FailWrites { get; set; }
    public bool FailReads { get; set; }
    public string? GetMeta(string key) => FailReads ? throw new Microsoft.Data.Sqlite.SqliteException("database is locked", 5) : inner.GetMeta(key);

    public void SetMeta(string key, string value)
    {
        if (FailWrites) throw new Microsoft.Data.Sqlite.SqliteException("database is locked", 5);
        inner.SetMeta(key, value);
    }

    public void AddEvent(DateTimeOffset at, string kind, string detail)
    {
        if (FailWrites) throw new Microsoft.Data.Sqlite.SqliteException("database is locked", 5);
        inner.AddEvent(at, kind, detail);
    }
}

public sealed class DtekWatcherHardeningTests : IDisposable
{
    private const int Port = 18940;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bluetti-watcher-test-" + Guid.NewGuid().ToString("N"));
    private readonly StationStore _store;
    private readonly FlakyStore _flaky;
    private readonly ManualClock _clock = new(At("2026-10-07 12:00"));
    private readonly FakeSource _source = new();
    private readonly List<string> _log = [];

    public DtekWatcherHardeningTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new StationStore(Path.Combine(_dir, "station.db"));
        _flaky = new FlakyStore(_store);
    }

    public void Dispose()
    {
        _source.Dispose();
        _store.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private StationConfig Cfg(DtekConfig? d = null)
    {
        var c = d ?? Config(queues: House);
        if (d is null) c.Site = false;
        return new StationConfig { DataDir = _dir, Port = Port, DtekWatcher = c };
    }

    private DtekWatcherService Watcher(DtekConfig? d = null, Func<IDtekSource>? source = null) => new(Cfg(d), source ?? (() => _source), _flaky, _clock, _log.Add);

    private DtekWatcherGuard Guard(Func<IDtekWatcher> make, StationConfig? cfg = null) => new(cfg ?? Cfg(), _flaky, _clock, _log.Add, make);

    private async Task<bool> TickAt(DtekWatcherService w, string wall)
    {
        _clock.Now = At(wall);
        return await w.TickAsync(CancellationToken.None);
    }

    private List<EventRow> Events(string kind = "dtek-watcher") => _store.Events(0, long.MaxValue).Where(e => e.Kind.StartsWith(kind, StringComparison.Ordinal)).ToList();

    // design.md §11 DTEK module, rule 19
    [Fact]
    public async Task A_module_that_throws_from_every_call_never_reaches_the_host()
    {
        using var broken = new ThrowingWatcher();
        using var guard = Guard(() => broken);
        for (int k = 0; k < 5; k++) guard.AfterPoll(new StationState());   // the poll loop's call: returns
        await guard.RunAsync(CancellationToken.None);
        var v = guard.View(true, 1);
        Assert.Equal("none", v.State);
        Assert.Contains("module error: InvalidOperationException", v.Note, StringComparison.Ordinal);
        // one event for the streak, each distinct message logged once
        var failed = Assert.Single(Events());
        Assert.Equal(("dtek-watcher-failed", "module: ArgumentOutOfRangeException"), (failed.Kind, failed.Detail));
        Assert.Single(_log, l => l.Contains("after a poll failed", StringComparison.Ordinal));
        // an hour later the same message is said again; a new streak after a good call gets its event
        _clock.Now = _clock.Now.AddMinutes(61);
        guard.AfterPoll(new StationState());
        Assert.Equal(2, _log.Count(l => l.Contains("after a poll failed", StringComparison.Ordinal)));
        Assert.Single(Events());
        broken.Throws = false;
        guard.AfterPoll(new StationState());
        broken.Throws = true;
        guard.AfterPoll(new StationState());
        Assert.Equal(2, Events().Count);
    }

    // design.md §11 DTEK module, rule 19
    [Fact]
    public async Task A_module_that_cannot_be_built_leaves_a_host_that_runs_and_a_view_that_says_so()
    {
        using var guard = Guard(() => throw new InvalidOperationException("no state"));
        guard.AfterPoll(new StationState());
        await guard.RunAsync(CancellationToken.None);
        Assert.Equal(("none", "the module did not start: see the log"), (guard.View(false, null).State, guard.View(false, null).Note));
        Assert.Equal("module: InvalidOperationException", Assert.Single(Events()).Detail);
        // a module that is off stays off, whatever happened
        using var off = Guard(() => throw new InvalidOperationException("x"), new StationConfig { DataDir = _dir });
        Assert.Equal("off", off.View(false, null).State);
    }

    // design.md §11 DTEK module, rule 19: telling about a fault through a store that fails too
    [Fact]
    public void A_fault_with_the_store_failing_as_well_is_still_swallowed()
    {
        using var broken = new ThrowingWatcher();
        _flaky.FailWrites = true;
        using var guard = Guard(() => broken);
        guard.AfterPoll(new StationState());
        Assert.Equal("none", guard.View(false, null).State);
        Assert.Empty(Events());
    }

    // design.md §11 DTEK module, rule 19
    [Fact]
    public async Task Api_now_answers_200_with_the_station_data_when_the_module_throws()
    {
        using var broken = new ThrowingWatcher();
        using var guard = Guard(() => broken);
        var cfg = Cfg();
        using var service = new StationService(cfg, new QueueReader { Default = Fx.Grid(80) }, new FakeActions(), new FakeHibernator(true), new FixedFlag(Fx.Station), _store, _clock, _ => { });
        await service.PollAsync(CancellationToken.None);
        guard.AfterPoll(service.State);
        var api = new StationApi(cfg, service, _store, _clock, _clock.Now, guard);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Host"] = $"127.0.0.1:{Port}" };
        var r = await api.HandleAsync(new HttpRequest("GET", "/api/now", new Dictionary<string, string>(), headers, []), CancellationToken.None);
        Assert.Equal(200, r.Status);
        using var doc = JsonDocument.Parse(r.Body);
        Assert.Equal(80, doc.RootElement.GetProperty("last").GetProperty("soc").GetDouble());
        Assert.Equal("none", doc.RootElement.GetProperty("dtekWatcher").GetProperty("state").GetString());
    }

    // design.md §11 DTEK module, rule 19
    [Fact]
    public async Task A_store_that_fails_on_save_keeps_the_state_in_memory_and_saves_it_at_the_next_change()
    {
        using var w = Watcher();
        _flaky.FailWrites = true;
        await TickAt(w, "2026-10-07 12:00");   // the read is applied, the save fails
        w.AfterPoll(new StationState { LastAt = _clock.Now, OnSince = _clock.Now });
        w.AfterPoll(new StationState { Cut = true, OffSince = _clock.Now, LastAt = _clock.Now });   // the cut start saves too
        Assert.Equal(("ok", At("2026-10-07 17:00")), (w.View(false, null).State, w.View(false, null).NextStart!.Value));
        Assert.Null(_store.GetMeta("dtekWatcher"));
        Assert.Single(_log, l => l.Contains("state not saved", StringComparison.Ordinal));
        _flaky.FailWrites = false;
        Assert.False(await TickAt(w, "2026-10-07 12:05"));   // nothing is due, but the look saves what is owed
        Assert.Contains("\"schedule\"", _store.GetMeta("dtekWatcher"), StringComparison.Ordinal);
    }

    // design.md §11 DTEK module, rule 19
    [Fact]
    public void A_null_module_section_in_config_json_is_the_default_off()
    {
        string file = Path.Combine(_dir, "config.json");
        File.WriteAllText(file, """{ "dtekWatcher": null }""");
        var cfg = StationConfig.Load(["--config", file]);
        Assert.False(cfg.DtekWatcher.Enabled);
        using var w = new DtekWatcherService(cfg, () => _source, _flaky, _clock, _log.Add);
        w.AfterPoll(new StationState { Cut = true });
        Assert.Equal("off", w.View(false, null).State);
        File.WriteAllText(file, """{ "dtekWatcher": { "enabled": true, "queues": null, "city": null, "host": null, "datasetUrl": null, "group": null } }""");
        var odd = StationConfig.Load(["--config", file]).DtekWatcher;
        Assert.NotNull(odd.Problem());
        Assert.False(odd.HasAddress);
        Assert.Equal("off", DtekOutlook.Compute(odd, new DtekSnapshot(), false, null, _clock, Kyiv).State);
    }

    // design.md §11 DTEK module, rule 19
    [Theory]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]
    [InlineData("""{"schedule":"yes","match":7}""")]
    [InlineData("""{"schedule":{"days":[{"start":253402214400,"queues":{"GPV1.1":"111111111111111111111111"}}]}}""")]
    [InlineData("""{"schedule":{"days":[{"start":-5,"queues":{"GPV1.1":"111111111111111111111111"}}]}}""")]
    [InlineData("""{"schedule":{"days":[{"start":1791320400,"queues":{"GPV1.1":"short"}}]}}""")]
    [InlineData("""{"schedule":{"days":[{"start":1791320400,"queues":null}]},"house":{"queues":[null]},"openCut":{"held":{"days":null}}}""")]
    [InlineData("null")]
    public void A_damaged_or_poisoned_saved_state_starts_the_module_fresh(string row)
    {
        _store.SetMeta("dtekWatcher", row);
        using var w = Watcher();
        var v = w.View(true, 3);
        Assert.Equal(("none", ""), (v.State, v.Update));
        w.AfterPoll(new StationState { Cut = true, OffSince = _clock.Now, LastAt = _clock.Now });
    }

    // design.md §11 DTEK module, rule 19: an unreadable store at startup
    [Fact]
    public void A_store_that_cannot_be_read_at_startup_starts_the_module_fresh()
    {
        _flaky.FailReads = true;
        using var w = Watcher();
        Assert.Equal("none", w.View(false, null).State);
    }

    /// <summary>A source that throws what no read is expected to throw.</summary>
    private sealed class ThrowingSource : IDtekSource
    {
        public int Asked { get; private set; }
        public Task<DtekDatasetResult> DatasetAsync(string? etag, CancellationToken ct) { Asked++; throw new InvalidDataException("the archive entry was compressed using an unsupported method"); }
        public Task<DtekFetch> SiteAsync(bool withHouse, CancellationToken ct) { Asked++; throw new InvalidOperationException("cannot read a lone surrogate"); }
        public void Dispose() { }
    }

    // design.md §11 DTEK module, rule 19
    [Fact]
    public async Task A_source_that_throws_records_the_attempt_is_failed_and_obeys_the_cadence()
    {
        using var source = new ThrowingSource();
        using var w = Watcher(Config(queues: House), () => source);
        Assert.True(await TickAt(w, "2026-10-07 12:00"));
        Assert.Equal(2, source.Asked);
        for (int minute = 1; minute < 30; minute++) Assert.False(await TickAt(w, $"2026-10-07 12:{minute:00}"));
        Assert.Equal(2, source.Asked);
        var v = w.View(false, null);
        Assert.Equal(("failed", "failed"), (v.DatasetState, v.SiteState));
        Assert.Contains("dataset read failed (failed): InvalidDataException", v.Note, StringComparison.Ordinal);
        Assert.True(await TickAt(w, "2026-10-07 12:30"));
        Assert.Equal(3, source.Asked);   // the dataset again; the site waits for its hour
        Assert.Equal(["dataset: failed: InvalidDataException: the archive entry was compressed using an unsupported method", "site: failed: InvalidOperationException: cannot read a lone surrogate"],
            Events("dtek-watcher-failed").Select(e => e.Detail).Order(StringComparer.Ordinal));
    }

    // design.md §11 DTEK module, rule 19: a source that cannot even be made
    [Fact]
    public async Task A_source_that_cannot_be_made_is_a_failed_read_on_the_cadence()
    {
        int made = 0;
        using var w = Watcher(null, () => { made++; throw new ArgumentException("bad address"); });
        Assert.True(await TickAt(w, "2026-10-07 12:00"));
        Assert.False(await TickAt(w, "2026-10-07 12:10"));
        Assert.Equal((1, "failed"), (made, w.View(false, null).DatasetState));
    }

    private static string Dataset(string dayKey = "1791320400", string update = "07.10.2026 11:43", string collected = "2026-10-07T08:45:00Z", string queue = "GPV1.1", string cell = "\"no\"", string hour = "18") =>
        """{"lastUpdated":"COLLECTED","fact":{"data":{"DAY":{"QUEUE":{"HOUR":CELL}}},"update":"UPDATE"}}"""
            .Replace("COLLECTED", collected, StringComparison.Ordinal).Replace("DAY", dayKey, StringComparison.Ordinal).Replace("QUEUE", queue, StringComparison.Ordinal)
            .Replace("HOUR", hour, StringComparison.Ordinal).Replace("CELL", cell, StringComparison.Ordinal).Replace("UPDATE", update, StringComparison.Ordinal);


    private async Task<DtekView> FeedTo(DtekWatcherService w, string body, string etag = "\"new\"")
    {
        _source.Datasets.Enqueue(DtekParser.ParseDataset(body, etag));
        await TickAt(w, "2026-10-07 12:30");
        return w.View(false, null);
    }

    /// <summary>The module with one queue and a held schedule (update 11:43, GPV1.1 off 17:00-21:00 today), dataset only.</summary>
    private async Task<DtekWatcherService> Holding()
    {
        var c = Config(queues: ["GPV1.1"]);
        c.Site = false;
        var w = Watcher(c);
        _source.Datasets.Enqueue(FakeSource.Data(Updated("2026-10-07 11:43", "2026-10-07 11:45", "dataset", Day("2026-10-07", ("GPV1.1", "18-21:0"))), "\"held\""));
        await TickAt(w, "2026-10-07 12:00");
        return w;
    }

    // design.md §11 DTEK module, rule 19: the whole answer is refused as format, the held schedule and its validator stay
    [Theory]
    [InlineData("253402214400", "07.10.2026 12:10", "a day key")]          // the year 9999
    [InlineData("1791320400123", "07.10.2026 12:10", "a day key")]
    [InlineData("1791338400", "07.10.2026 12:10", "a day key that is no Kyiv day start")]   // 05:00 Kyiv
    [InlineData("1790974800", "07.10.2026 12:10", "outside the days around today")]         // 03.10: over 2 days back
    [InlineData("1792098000", "07.10.2026 12:10", "outside the days around today")]         // 16.10: over 7 days ahead
    [InlineData("1791320400", "09.10.2026 12:01", "an update time in the future")]          // over 24 h ahead
    public async Task An_answer_with_a_day_or_an_update_time_out_of_bounds_is_refused_whole(string dayKey, string update, string why)
    {
        using var w = await Holding();
        var v = await FeedTo(w, Dataset(dayKey, update));
        Assert.Equal(("ok", "failed", "07.10.2026 11:43"), (v.State, v.DatasetState, v.Update));
        Assert.Contains("dataset read failed (format)", v.Note, StringComparison.Ordinal);
        Assert.Contains(why, v.Note, StringComparison.Ordinal);
        Assert.Equal("17:00-21:00", v.Windows[0].Today);
        Assert.Equal("\"held\"", w.DatasetETag);   // not the rejected body's: it is read again in full
        await TickAt(w, "2026-10-07 13:00");
        Assert.Equal("dataset \"held\"", _source.Asked[^1]);
    }

    // design.md §11 DTEK module, rule 19: within bounds
    [Theory]
    [InlineData("1791320400", "08.10.2026 12:20")]   // an update under 24 h ahead
    [InlineData("1791324000", "07.10.2026 12:10")]   // a day start an hour off (a clock change)
    [InlineData("1791925200", "07.10.2026 12:10")]   // 14.10: 7 days ahead
    public async Task An_answer_inside_the_bounds_is_taken(string dayKey, string update)
    {
        using var w = await Holding();
        var v = await FeedTo(w, Dataset(dayKey, update));
        Assert.Equal(("ok", update), (v.DatasetState, v.Update));
        Assert.Equal("\"new\"", w.DatasetETag);
    }

    // design.md §11 DTEK module, rule 19: parser bounds
    [Theory]
    [InlineData("""{"lastUpdated":"2026-10-07T08:45:00Z","fact":{"data":{"1791320400":{"<img src=x>":{"1":"no"}}},"update":"u"}}""")]   // a queue name of no known form
    [InlineData("""{"lastUpdated":"2026-10-07T08:45:00Z","fact":{"data":{"1791320400":{"GPV1.123":{"1":"no"}}},"update":"u"}}""")]
    [InlineData("""{"lastUpdated":"2026-10-07T08:45:00Z","fact":{"data":{"1791320400":{"GPV1.1":{"25":"no"}}},"update":"u"}}""")]        // an hour outside 1-24
    [InlineData("""{"lastUpdated":"2026-10-07T08:45:00Z","fact":{"data":{"1791320400":{"GPV1.1":{"0":"no"}}},"update":"u"}}""")]
    [InlineData("""{"lastUpdated":"2026-10-07T08:45:00Z","fact":{"data":{"1791320400":{"GPV1.1":{"x":"no"}}},"update":"u"}}""")]
    [InlineData("""{"lastUpdated":"2026-10-07T08:45:00Z","fact":{"data":{"1791320400":{"GPV1.1":{"1":"no"}},"1791406800":{"GPV1.1":{"1":"no"}},"1791493200":{"GPV1.1":{"1":"no"}},"1791579600":{"GPV1.1":{"1":"no"}},"1791666000":{"GPV1.1":{"1":"no"}}},"update":"u"}}""")]   // five days
    [InlineData("""{"lastUpdated":"2026-10-07T08:45:00Z","fact":{"data":{"1791320400":{"GPV\ud800.1":{"1":"no"}}},"update":"u"}}""")]    // a lone surrogate in a name
    [InlineData("""{"lastUpdated":"2026-10-07T08:45:00Z","fact":{"data":{"1791320400":{"GPV1.1":{"1":"no"}}},"update":"\ud800"}}""")]    // and in the update text
    public void A_dataset_outside_the_structural_bounds_is_a_format_change(string body)
    {
        var r = DtekParser.ParseDataset(body, null);
        Assert.Equal(DtekStatus.Format, r.Status);
        Assert.Null(r.Schedule);
    }

    // design.md §11 DTEK module, rule 19: a cell that is no valid text refuses the whole answer as format, and nothing throws
    [Fact]
    public async Task A_lone_surrogate_cell_is_a_format_change_and_nothing_throws()
    {
        var r = DtekParser.ParseDataset(Dataset(cell: "\"\\ud800\""), null);
        Assert.Equal(DtekStatus.Format, r.Status);
        Assert.Null(r.Schedule);
        using var page = new StringReader("<meta name=\"csrf-token\" content=\"t\">\nDisconSchedule.fact = " + """{"data":{"1791320400":{"GPV1.1":{"18":"\ud800"}}},"update":"u"}""");
        Assert.Equal(DtekStatus.Format, (await DtekParser.ParsePageAsync(page, CancellationToken.None)).Status);
        // and through the module: the held schedule stays
        using var w = await Holding();
        var v = await FeedTo(w, Dataset(cell: "\"\\ud800\""));
        Assert.Equal(("failed", "07.10.2026 11:43"), (v.DatasetState, v.Update));
    }

    // design.md §11 DTEK module, rule 19
    [Fact]
    public void More_than_24_queues_a_day_is_a_format_change_and_24_is_taken()
    {
        string Queues(int n) => string.Join(",", Enumerable.Range(1, n).Select(k => $"\"GPV{k}.1\":{{\"1\":\"no\"}}"));
        string Body(int n) => """{"lastUpdated":"2026-10-07T08:45:00Z","fact":{"data":{"1791320400":{QUEUES}},"update":"u"}}""".Replace("QUEUES", Queues(n), StringComparison.Ordinal);
        Assert.Equal(24, DtekParser.ParseDataset(Body(24), null).Schedule!.Days[0].Queues.Count);
        Assert.Equal(DtekStatus.Format, DtekParser.ParseDataset(Body(25), null).Status);
    }

    // design.md §11 DTEK module, rule 19
    [Fact]
    public void Over_long_texts_of_a_source_are_cut_and_control_characters_removed()
    {
        string longText = new string('k', 500), body = """
            {"result":true,"data":{"1":{"sub_type":"Emergency\u0007\n repair LONG","start_date":"LONG","end_date":"15:00\u0000 07.10.2026","type":"t",
            "sub_type_reason":["GPV4.1","<script>","GPV4.1","LONG","GPV12.10"]}}}
            """.Replace("LONG", longText, StringComparison.Ordinal);
        var h = DtekParser.ParseHouse(body, "1").House!;
        Assert.Equal(120, h.Kind.Length);
        Assert.StartsWith("Emergency repair kkk", h.Kind, StringComparison.Ordinal);
        Assert.Equal(120, h.StartText.Length);
        Assert.Equal("15:00 07.10.2026", h.EndText);
        Assert.Equal(["GPV4.1", "GPV12.10"], h.Queues);
        var s = DtekParser.ParseDataset(Dataset(update: longText), null).Schedule!;
        Assert.Equal(40, s.Update.Length);
        Assert.Equal(DtekStatus.Format, DtekParser.ParseHouse("""{"result":true,"data":{"1":{"sub_type":"\ud800"}}}""", "1").Status);
        foreach (string name in new[] { "GPV1.1", "GPV12.10", "GPV6.2" }) Assert.True(DtekParser.IsQueue(name), name);
        foreach (string name in new[] { "", "GPV", "GPV1", "GPV.1", "GPV1.", "GPV123.1", "GPV1.123", "gpv1.1", "GPV1,1", "GPV1.1 ", "XGPV1.1", "GPV१.1" }) Assert.False(DtekParser.IsQueue(name), name);
    }

    // design.md §11 DTEK module, rule 19
    [Fact]
    public async Task A_collection_time_ahead_of_the_clock_is_taken_as_now_so_the_schedule_still_goes_stale()
    {
        using var w = await Holding();
        var v = await FeedTo(w, Dataset(update: "07.10.2026 12:10", collected: "2027-01-01T00:00:00Z"));
        Assert.Equal(("ok", At("2026-10-07 12:30")), (v.State, v.CollectedAt!.Value));
        _source.Dataset = () => FakeSource.NotModified("\"new\"");
        await TickAt(w, "2026-10-07 14:01");
        Assert.Equal("stale", w.View(false, null).State);
        // a few minutes ahead is a clock difference, kept; the age then counts as 0
        using var w2 = Watcher(Config(queues: ["GPV1.1"]));
        _clock.Now = At("2026-10-07 12:30");
        _source.Datasets.Enqueue(DtekParser.ParseDataset(Dataset(update: "07.10.2026 12:20", collected: "2026-10-07T09:35:00Z"), "\"x\""));
        await w2.TickAsync(CancellationToken.None);
        Assert.Equal(("ok", At("2026-10-07 12:35")), (w2.View(false, null).State, w2.View(false, null).CollectedAt!.Value));
    }

    // design.md §11 DTEK module, rule 19
    [Fact]
    public async Task An_unreadable_update_time_is_taken_only_with_new_content_and_a_newer_collection_and_the_note_says_so()
    {
        using var w = await Holding();
        static DtekDatasetResult Unread(string collected, string spec, string etag) => FakeSource.Data(
            new DtekSchedule { Update = "this morning", CollectedAt = At($"2026-10-07 {collected}"), Source = "dataset", Days = [Day("2026-10-07", ("GPV1.1", spec))] }, etag);
        // the same content: not taken
        _source.Datasets.Enqueue(Unread("12:20", "18-21:0", "\"a\""));
        await TickAt(w, "2026-10-07 12:30");
        Assert.Equal("07.10.2026 11:43", w.View(false, null).Update);
        // new content but collected earlier than the held one: not taken
        _source.Datasets.Enqueue(Unread("11:00", "18:0", "\"b\""));
        await TickAt(w, "2026-10-07 13:00");
        Assert.Equal("07.10.2026 11:43", w.View(false, null).Update);
        // new content, collected later: taken, and said
        _source.Datasets.Enqueue(Unread("13:20", "18:0", "\"c\""));
        await TickAt(w, "2026-10-07 13:30");
        var v = w.View(false, null);
        Assert.Equal(("this morning", "17:00-18:00"), (v.Update, v.Windows[0].Today));
        Assert.Contains("update time cannot be read", v.Note, StringComparison.Ordinal);
        // a readable update afterwards takes over again
        _source.Datasets.Enqueue(DtekParser.ParseDataset(Dataset(update: "07.10.2026 13:40", collected: "2026-10-07T10:50:00Z"), "\"d\""));
        await TickAt(w, "2026-10-07 14:00");
        Assert.Equal("07.10.2026 13:40", w.View(false, null).Update);
    }

    // design.md §11 DTEK module, rule 19
    [Fact]
    public async Task Changed_is_raised_for_moved_windows_only_and_at_most_once_per_queue_and_day_in_10_minutes()
    {
        var c = Config(queues: ["A"]);   // the site is on: two sources can answer in one look
        using var w = Watcher(c);
        DtekDatasetResult Plan(string update, string spec) => FakeSource.Data(Updated($"2026-10-07 {update}", $"2026-10-07 {update}", "dataset", Day("2026-10-07", ("A", spec))), $"\"{update}\"");
        _source.Datasets.Enqueue(Plan("11:43", "18-21:0"));
        await TickAt(w, "2026-10-07 12:00");
        // a "possible" cell appears: the planned windows are the same, no event
        _source.Datasets.Enqueue(Plan("12:10", "18-21:0 22:m"));
        await TickAt(w, "2026-10-07 12:30");
        Assert.Empty(Events("dtek-watcher-changed"));
        _source.Datasets.Enqueue(Plan("12:40", "17-21:0 22:m"));
        _source.Sites.Enqueue(FakeSource.Answer(Updated("2026-10-07 12:50", "2026-10-07 12:55", "site", Day("2026-10-07", ("A", "16-21:0")))));
        await TickAt(w, "2026-10-07 13:00");
        Assert.Equal("A 07.10.2026: 17:00-21:00, possible 21:00-22:00 -> 16:00-21:00, possible 21:00-22:00", Assert.Single(Events("dtek-watcher-changed")).Detail);
        Assert.Equal("15:00-21:00", w.View(false, null).Windows[0].Today);   // the site moved it again in the same look: the schedule is the newest, the event is not repeated
        _source.Datasets.Enqueue(Plan("13:20", "18-21:0"));
        await TickAt(w, "2026-10-07 13:30");
        Assert.Equal(2, Events("dtek-watcher-changed").Count);
    }

    // design.md §11 DTEK module, rule 19
    [Fact]
    public async Task Failed_and_ok_of_one_source_are_at_most_one_pair_an_hour()
    {
        using var w = Watcher(Config(queues: House));   // the site, in a cut: asked every 15 minutes
        var t = At("2026-10-07 12:00");
        w.AfterPoll(new StationState { Cut = true, OffSince = t, LastAt = t });
        for (int k = 0; k < 4; k++)
        {
            _clock.Now = t.AddMinutes(15 * k);
            _source.Sites.Enqueue(k % 2 == 0 ? FakeSource.Down() : FakeSource.Answer());   // fails, recovers, fails, recovers: inside one hour
            await w.TickAsync(CancellationToken.None);
        }
        Assert.Equal(["dtek-watcher-failed", "dtek-watcher-ok"], Events().Select(e => e.Kind));
        _clock.Now = t.AddMinutes(60);
        _source.Sites.Enqueue(FakeSource.Down());
        await w.TickAsync(CancellationToken.None);
        Assert.Equal(3, Events().Count);
    }

    private static StationState On(string at) => new() { LastAt = At(at), OnSince = At(at) };
    private static StationState OffAt(string since, string at) => new() { LastAt = At(at), OffSince = At(since), Cut = true, CutSince = At(since).AddMinutes(5) };
    private static StationState Back(string at) => new() { LastAt = At(at), OnSince = At(at), MainsBackAt = At(at) };

    // design.md §11 DTEK module, rule 19
    [Fact]
    public async Task A_finished_cut_is_compared_with_the_schedule_held_when_it_began()
    {
        var c = Config(queues: House);
        c.Site = false;
        using var w = Watcher(c);
        await TickAt(w, "2026-10-07 12:00");   // A 17:00-21:00, B 22:00-24:00
        w.AfterPoll(On("2026-10-07 17:01"));
        w.AfterPoll(OffAt("2026-10-07 17:02", "2026-10-07 17:08"));
        // during the cut the plan is rewritten so that the cut would fit B and no longer A
        _source.Datasets.Enqueue(FakeSource.Data(Updated("2026-10-07 18:00", "2026-10-07 18:05", "dataset",
            Day("2026-10-07", ("A", "23-24:0"), ("B", "18-21:0"), ("C", "10:f")), TwoDayCells()[1]), "\"v2\""));
        await TickAt(w, "2026-10-07 18:10");
        w.AfterPoll(OffAt("2026-10-07 17:02", "2026-10-07 20:57"));
        w.AfterPoll(Back("2026-10-07 20:58"));
        Assert.Equal((null, 0), (w.Match.Candidate, w.Match.Count));   // neither a match for B nor one for A
    }

    // design.md §11 DTEK module, rule 19
    [Fact]
    public async Task A_cut_under_an_unchanged_plan_still_counts_and_a_fixed_queue_is_not_contradicted_by_a_rewritten_plan()
    {
        var c = Config(queues: House);
        c.Site = false;
        using var w = Watcher(c);
        await TickAt(w, "2026-10-07 12:00");
        w.AfterPoll(On("2026-10-07 17:01"));
        w.AfterPoll(OffAt("2026-10-07 17:02", "2026-10-07 17:08"));
        // the same plan under a newer update time: still the plan the cut began under
        _source.Datasets.Enqueue(FakeSource.Data(Updated("2026-10-07 18:00", "2026-10-07 18:05", "dataset", TwoDayCells()), "\"v2\""));
        await TickAt(w, "2026-10-07 18:10");
        w.AfterPoll(OffAt("2026-10-07 17:02", "2026-10-07 20:57"));
        w.AfterPoll(Back("2026-10-07 20:58"));
        Assert.Equal(("A", 1), (w.Match.Candidate, w.Match.Count));
        // the held plan is saved with the open cut, so a restart inside a cut keeps it
        w.AfterPoll(On("2026-10-08 08:04"));
        w.AfterPoll(OffAt("2026-10-08 08:05", "2026-10-08 08:11"));
        using var again = Watcher(c);
        again.AfterPoll(OffAt("2026-10-08 08:05", "2026-10-08 11:54"));
        again.AfterPoll(Back("2026-10-08 11:55"));
        Assert.Equal("A", again.Match.Fixed);
    }
}
