using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using BluettiWatcher.Decision;
using BluettiWatcher.DtekWatcher;
using BluettiWatcher.Http;
using BluettiWatcher.Storage;
using static BluettiWatcher.Tests.DtekWatcher.Dx;

namespace BluettiWatcher.Tests.DtekWatcher;

public sealed class DtekWatcherServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bluetti-watcher-test-" + Guid.NewGuid().ToString("N"));
    private readonly StationStore _store;
    private readonly ManualClock _clock = new(At("2026-10-07 12:00"));
    private readonly FakeSource _source = new();
    private int _made;

    public DtekWatcherServiceTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new StationStore(Path.Combine(_dir, "station.db"));
    }

    public void Dispose()
    {
        _source.Dispose();
        _store.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    /// <summary>The module with the house's queues in config (A, B, C) unless a config is given.</summary>
    private DtekWatcherService Watcher(DtekConfig? config = null) =>
        new(new StationConfig { DataDir = _dir, DtekWatcher = config ?? Config(queues: House) }, () => { _made++; return _source; }, new DtekStore(_store), _clock, _ => { });

    private async Task<bool> TickAt(DtekWatcherService w, string wall)
    {
        _clock.Now = At(wall);
        return await w.TickAsync(CancellationToken.None);
    }

    private List<EventRow> Events(string kind = "dtek-watcher") => _store.Events(0, long.MaxValue).Where(e => e.Kind.StartsWith(kind, StringComparison.Ordinal)).ToList();

    private static DtekConfig NoSite(string[]? queues = null)
    {
        var c = Config(queues: queues ?? House);
        c.Site = false;
        return c;
    }

    // design.md §11 DTEK module, rule 8
    [Fact]
    public async Task Disabled_makes_no_source_and_no_request()
    {
        using var w = Watcher(new DtekConfig { Queues = House });
        Assert.False(await w.TickAsync(CancellationToken.None));
        await w.RunAsync(CancellationToken.None);   // returns at once
        w.AfterPoll(new StationState { Cut = true, OffSince = _clock.Now, LastAt = _clock.Now });
        Assert.Equal(0, _made);
        Assert.Empty(_source.Asked);
        Assert.Equal("off", w.View(false, null).State);
        Assert.Null(_store.GetMeta("dtekWatcher"));
    }

    // design.md §11 DTEK module, rule 8
    [Fact]
    public async Task Enabled_with_a_dataset_address_that_cannot_be_used_makes_no_request()
    {
        using var w = Watcher(new DtekConfig { Enabled = true, DatasetUrl = "http://plain.example/x.json" });
        Assert.False(await w.TickAsync(CancellationToken.None));
        await w.RunAsync(CancellationToken.None);
        Assert.Equal(0, _made);
    }

    // design.md §11 DTEK module, rules 15, 17
    [Fact]
    public async Task The_dataset_is_read_every_30_minutes_and_the_site_once_an_hour_on_mains()
    {
        using var w = Watcher();
        Assert.True(await TickAt(w, "2026-10-07 12:00"));
        Assert.False(await TickAt(w, "2026-10-07 12:29"));
        Assert.True(await TickAt(w, "2026-10-07 12:30"));
        Assert.False(await TickAt(w, "2026-10-07 12:59"));
        Assert.True(await TickAt(w, "2026-10-07 13:00"));
        Assert.Equal(["dataset", "site+house", "dataset \"v1\"", "dataset \"v1\"", "site+house"], _source.Asked);
        Assert.Equal(1, _made);
    }

    // design.md §11 DTEK module, rule 17
    [Fact]
    public async Task During_a_cut_the_site_is_asked_every_15_minutes()
    {
        using var w = Watcher();
        await TickAt(w, "2026-10-07 12:00");
        w.AfterPoll(new StationState { Cut = true, OffSince = At("2026-10-07 12:01"), LastAt = At("2026-10-07 12:06") });
        Assert.False(await TickAt(w, "2026-10-07 12:14"));
        Assert.True(await TickAt(w, "2026-10-07 12:15"));
        Assert.True(await TickAt(w, "2026-10-07 12:30"));
        Assert.Equal(["dataset", "site+house", "site+house", "dataset \"v1\"", "site+house"], _source.Asked);
    }

    // design.md §11 DTEK module, rule 23
    [Fact]
    public async Task On_mains_the_sites_hour_is_its_own_and_a_challenge_or_a_failure_waits_for_the_next_one()
    {
        using (var w = Watcher(NoSite())) await TickAt(w, "2026-10-07 12:00");
        using var site = Watcher();
        _source.Sites.Enqueue(FakeSource.Blocked());
        _source.Sites.Enqueue(FakeSource.Down());
        Assert.True(await TickAt(site, "2026-10-07 12:10"));    // the site's first attempt, between two dataset reads: a challenge
        Assert.True(await TickAt(site, "2026-10-07 12:30"));
        Assert.True(await TickAt(site, "2026-10-07 13:00"));
        Assert.False(await TickAt(site, "2026-10-07 13:09"));
        Assert.True(await TickAt(site, "2026-10-07 13:10"));    // a failure
        Assert.True(await TickAt(site, "2026-10-07 13:30"));
        Assert.True(await TickAt(site, "2026-10-07 14:00"));
        Assert.False(await TickAt(site, "2026-10-07 14:09"));
        Assert.Equal(["dataset", "site+house", "dataset \"v1\"", "dataset \"v1\"", "site+house", "dataset \"v1\"", "dataset \"v1\""], _source.Asked);
    }

    // design.md §11 DTEK module, rules 17, 18
    [Fact]
    public async Task The_site_is_never_asked_when_it_is_turned_off_and_without_an_address_only_its_page_is()
    {
        using (var w = Watcher(NoSite()))
        {
            await TickAt(w, "2026-10-07 12:00");
            await TickAt(w, "2026-10-07 13:00");
            Assert.DoesNotContain(_source.Asked, a => a.StartsWith("site", StringComparison.Ordinal));
            Assert.Equal("off", w.View(false, null).SiteState);
        }
        _source.Asked.Clear();
        using var noAddress = Watcher(new DtekConfig { Enabled = true, Site = true, Queues = House });
        await TickAt(noAddress, "2026-10-07 14:00");
        Assert.Equal(["dataset \"v1\"", "site"], _source.Asked);
    }

    // The site is opt-in: with the module on and an address set, but no "site": true, only the dataset is read.
    [Fact]
    public async Task The_site_is_not_asked_unless_it_is_opted_in()
    {
        var c = new DtekConfig { Enabled = true, City = "Test City", Street = "Test Street", House = "1", Queues = House };
        Assert.False(c.Site);
        using var w = Watcher(c);
        await TickAt(w, "2026-10-07 12:00");
        await TickAt(w, "2026-10-07 13:00");
        Assert.NotEmpty(_source.Asked);
        Assert.DoesNotContain(_source.Asked, a => a.StartsWith("site", StringComparison.Ordinal));
        Assert.Equal("off", w.View(false, null).SiteState);
    }

    // design.md §11 DTEK module, rule 17
    [Fact]
    public async Task The_site_blocked_while_the_dataset_works_keeps_the_module_ok_and_raises_one_event_per_streak()
    {
        using var w = Watcher();
        await TickAt(w, "2026-10-07 11:50");
        await TickAt(w, "2026-10-07 12:50");
        await TickAt(w, "2026-10-07 13:15");   // only a look: nothing is due
        var v = w.View(false, null);
        Assert.Equal(("ok", "ok", "blocked", "dataset"), (v.State, v.DatasetState, v.SiteState, v.Source));
        var failed = Assert.Single(Events());
        Assert.Equal(("dtek-watcher-failed", "site: blocked: a challenge page instead of the schedule"), (failed.Kind, failed.Detail));
        // the site answers: one recovery event; a later streak gets its own
        _source.Sites.Enqueue(FakeSource.Answer());
        await TickAt(w, "2026-10-07 13:50");
        Assert.Equal("ok", w.View(false, null).SiteState);
        Assert.Equal(["dtek-watcher-failed", "dtek-watcher-ok"], Events().Select(e => e.Kind));
        await TickAt(w, "2026-10-07 14:50");
        Assert.Equal(3, Events().Count);
    }

    // design.md §11 DTEK module, rule 17
    [Fact]
    public async Task After_24_blocked_attempts_in_a_row_the_site_is_tried_every_6_hours_until_it_answers()
    {
        using var w = Watcher();
        var t = At("2026-10-07 12:00");
        for (int k = 0; k < 24; k++) { _clock.Now = t.AddHours(k); await w.TickAsync(CancellationToken.None); }
        Assert.Equal(24, _source.Asked.Count(a => a == "site+house"));
        Assert.Equal(24, w.SiteBlocked);
        var last = t.AddHours(23);
        for (int h = 1; h < 6; h++) { _clock.Now = last.AddHours(h); await w.TickAsync(CancellationToken.None); }
        Assert.Equal(24, _source.Asked.Count(a => a == "site+house"));
        // a cut does not bring it sooner either
        w.AfterPoll(new StationState { Cut = true, OffSince = _clock.Now, LastAt = _clock.Now });
        _clock.Now = last.AddHours(5.5);
        await w.TickAsync(CancellationToken.None);
        Assert.Equal(24, _source.Asked.Count(a => a == "site+house"));
        w.AfterPoll(new StationState { LastAt = _clock.Now, OnSince = _clock.Now });
        _source.Sites.Enqueue(FakeSource.Answer());
        _clock.Now = last.AddHours(6);
        await w.TickAsync(CancellationToken.None);
        Assert.Equal(25, _source.Asked.Count(a => a == "site+house"));
        Assert.Equal(0, w.SiteBlocked);
        // it answered: back to the hour
        _clock.Now = last.AddHours(7);
        await w.TickAsync(CancellationToken.None);
        Assert.Equal(26, _source.Asked.Count(a => a == "site+house"));
        Assert.Single(Events("dtek-watcher-failed"), e => e.Detail.StartsWith("site:", StringComparison.Ordinal) && e.At < last.AddHours(6).ToUnixTimeSeconds());
    }

    // design.md §11 DTEK module, rule 16
    [Fact]
    public async Task The_schedule_with_the_newest_update_time_of_the_distributor_is_the_one_in_use()
    {
        using var w = Watcher();
        var newer = Updated("2026-10-07 12:20", "2026-10-07 12:25", "site", Day("2026-10-07", ("A", "17-21:0")), Day("2026-10-08", ("A", "9-12:0")));
        _source.Sites.Enqueue(FakeSource.Answer(newer));
        await TickAt(w, "2026-10-07 12:30");   // the dataset (update 11:43), then the site (update 12:20)
        var v = w.View(false, null);
        Assert.Equal(("site", "07.10.2026 12:20", At("2026-10-07 12:30")), (v.Source, v.Update, v.CollectedAt!.Value));
        Assert.Equal("16:00-21:00", v.Windows.Single(q => q.Queue == "A").Today);
        // the dataset still carries the older update: it does not take the schedule back
        _source.Datasets.Enqueue(FakeSource.Data(Updated("2026-10-07 11:43", "2026-10-07 12:55", "dataset", TwoDayCells()), "\"v2\""));
        await TickAt(w, "2026-10-07 13:00");
        Assert.Equal(("site", "07.10.2026 12:20"), (w.View(false, null).Source, w.View(false, null).Update));
        // the dataset catches up with a newer update: it is in use again
        _source.Datasets.Enqueue(FakeSource.Data(Updated("2026-10-07 13:10", "2026-10-07 13:25", "dataset", TwoDayCells()), "\"v3\""));
        await TickAt(w, "2026-10-07 13:30");
        v = w.View(false, null);
        Assert.Equal(("dataset", "07.10.2026 13:10", At("2026-10-07 13:25")), (v.Source, v.Update, v.CollectedAt!.Value));
    }

    // design.md §11 DTEK module, rule 16
    [Fact]
    public async Task The_same_update_collected_again_only_moves_the_collection_time()
    {
        using var w = Watcher(NoSite());
        await TickAt(w, "2026-10-07 12:00");
        _source.Datasets.Enqueue(FakeSource.Data(Updated("2026-10-07 11:43", "2026-10-07 12:25", "dataset", TwoDayCells()), "\"v2\""));
        await TickAt(w, "2026-10-07 12:30");
        var v = w.View(false, null);
        Assert.Equal(("07.10.2026 11:43", At("2026-10-07 12:25")), (v.Update, v.CollectedAt!.Value));
        Assert.Empty(Events());
    }

    // design.md §11 DTEK module, rule 15: not modified = the dataset was not collected anew, so the age shown stays the dataset's own
    [Fact]
    public async Task Not_modified_keeps_the_schedule_and_its_collection_time_and_the_module_goes_stale_with_the_dataset()
    {
        using var w = Watcher(NoSite());
        await TickAt(w, "2026-10-07 12:00");   // collected by the dataset at 11:45
        _source.Dataset = () => FakeSource.NotModified();
        await TickAt(w, "2026-10-07 12:30");
        await TickAt(w, "2026-10-07 13:00");
        var v = w.View(false, null);
        Assert.Equal(("ok", "ok", At("2026-10-07 11:45")), (v.State, v.DatasetState, v.CollectedAt!.Value));
        Assert.Equal(At("2026-10-07 17:00"), v.NextStart);
        await TickAt(w, "2026-10-07 13:30");
        v = w.View(false, null);
        Assert.Equal(("stale", "ok", At("2026-10-07 11:45")), (v.State, v.DatasetState, v.CollectedAt!.Value));
        Assert.Equal(["dataset", "dataset \"v1\"", "dataset \"v1\"", "dataset \"v1\""], _source.Asked);
        Assert.Empty(Events());
    }

    // design.md §11 DTEK module, rules 16, 10
    [Fact]
    public async Task With_the_collection_failing_the_schedule_is_stale_after_90_minutes_then_none_after_its_days()
    {
        using var w = Watcher(NoSite());
        _source.Datasets.Enqueue(FakeSource.Data(Updated("2026-10-07 11:43", "2026-10-07 11:58", "dataset", TwoDayCells())));
        await TickAt(w, "2026-10-07 12:00");
        _source.Dataset = () => FakeSource.NoData(DtekStatus.Network, "no route");
        await TickAt(w, "2026-10-07 12:30");
        await TickAt(w, "2026-10-07 13:00");
        var v = w.View(false, null);
        Assert.Equal(("ok", "failed"), (v.State, v.DatasetState));   // 62 minutes old
        Assert.Contains("dataset read failed (network): no route", v.Note, StringComparison.Ordinal);
        await TickAt(w, "2026-10-07 13:30");
        v = w.View(false, null);
        Assert.Equal("stale", v.State);
        Assert.Contains("over 90 min ago", v.Note, StringComparison.Ordinal);
        Assert.Equal(At("2026-10-07 17:00"), v.NextStart);   // the saved schedule is still shown
        await TickAt(w, "2026-10-09 00:30");
        Assert.Equal("none", w.View(false, null).State);
        var failed = Assert.Single(Events());
        Assert.Equal(("dtek-watcher-failed", "dataset: network: no route"), (failed.Kind, failed.Detail));
        // recovery: one event
        _source.Dataset = () => FakeSource.Data(Updated("2026-10-09 00:40", "2026-10-09 00:50", "dataset", Day("2026-10-09", ("A", "9:0"))), "\"v9\"");
        await TickAt(w, "2026-10-09 01:00");
        Assert.Equal(["dtek-watcher-failed", "dtek-watcher-ok"], Events().Select(e => e.Kind));
        Assert.Equal("ok", w.View(false, null).State);
    }

    // design.md §11 DTEK module, rule 10
    [Fact]
    public async Task A_failed_read_is_tried_again_on_the_normal_cadence_never_sooner()
    {
        using var w = Watcher(NoSite());
        _source.Dataset = () => FakeSource.NoData();
        Assert.True(await TickAt(w, "2026-10-07 12:00"));
        for (int minute = 1; minute < 30; minute++) Assert.False(await TickAt(w, $"2026-10-07 12:{minute:00}"));
        Assert.True(await TickAt(w, "2026-10-07 12:30"));
        Assert.Equal(["dataset", "dataset"], _source.Asked);   // no validator without a schedule
        Assert.Equal("none", w.View(false, null).State);
    }

    // design.md §11 DTEK module, rule 16
    [Fact]
    public async Task A_change_of_a_shown_queues_windows_is_one_event_with_both_texts()
    {
        using var w = Watcher(NoSite(["A", "B"]));
        await TickAt(w, "2026-10-07 12:00");
        Assert.Empty(Events());   // the first schedule changes nothing
        var moved = Updated("2026-10-07 12:20", "2026-10-07 12:25", "dataset",
            Day("2026-10-07", ("A", "17-21:0"), ("B", "23-24:0"), ("C", "1-5:0")), Day("2026-10-08", ("A", "9-12:0"), ("B", "1-2:0 14-15:0"), ("C", "19-20:0")));
        _source.Datasets.Enqueue(FakeSource.Data(moved, "\"v2\""));
        await TickAt(w, "2026-10-07 12:30");
        var changed = Assert.Single(Events());
        Assert.Equal(("dtek-watcher-changed", "A 07.10.2026: 17:00-21:00 -> 16:00-21:00"), (changed.Kind, changed.Detail));   // C moved too, but C is not shown
        // the same plan read again, and a new update with the same windows: no second event
        _source.Datasets.Enqueue(FakeSource.NotModified("\"v2\""));
        await TickAt(w, "2026-10-07 13:00");
        _source.Datasets.Enqueue(FakeSource.Data(Updated("2026-10-07 13:20", "2026-10-07 13:25", "dataset", [.. moved.Days]), "\"v3\""));
        await TickAt(w, "2026-10-07 13:30");
        Assert.Single(Events());
        Assert.Equal("07.10.2026 13:20", w.View(false, null).Update);
    }

    // design.md §11 DTEK module, rule 18
    [Fact]
    public async Task The_queues_come_from_config_or_from_a_saved_site_answer_or_are_asked_for()
    {
        using (var none = Watcher(Config()))
        {
            await TickAt(none, "2026-10-07 12:00");   // the site is blocked: nothing names the queues
            var v = none.View(false, null);
            Assert.Equal(("none", "dataset", ""), (v.State, v.Source, v.QueuesSource));
            Assert.Contains("set dtekWatcher.queues", v.Note, StringComparison.Ordinal);
            _source.Sites.Enqueue(FakeSource.Answer(null, "B", "C"));
            await TickAt(none, "2026-10-07 13:00");
            v = none.View(false, null);
            Assert.Equal(("ok", "site"), (v.State, v.QueuesSource));
            Assert.Equal(["B", "C"], v.Queues);
        }
        // saved: a restart with the site blocked again still knows them
        using var again = Watcher(Config());
        Assert.Equal(["B", "C"], again.View(false, null).Queues);
        Assert.Equal("site", again.View(false, null).QueuesSource);
        // and the config's list wins over the saved answer
        using var configured = Watcher(Config(queues: ["A"]));
        Assert.Equal(["A"], configured.View(false, null).Queues);
        Assert.Equal(("config", "A"), (configured.View(false, null).QueuesSource, configured.View(false, null).Queue));
    }

    // design.md §11 DTEK module, rule 12
    [Fact]
    public async Task Only_the_schedule_in_use_is_kept_and_it_survives_a_restart()
    {
        using (var w = Watcher(NoSite()))
        {
            await TickAt(w, "2026-10-07 12:00");
            _source.Datasets.Enqueue(FakeSource.Data(Updated("2026-10-07 12:20", "2026-10-07 12:25", "dataset", Day("2026-10-07", ("A", "2:0")), Day("2026-10-08", ("A", "20:0"))), "\"v2\""));
            await TickAt(w, "2026-10-07 12:30");
        }
        using var doc = JsonDocument.Parse(_store.GetMeta("dtekWatcher")!);
        Assert.Equal(2, doc.RootElement.GetProperty("schedule").GetProperty("days").GetArrayLength());
        _source.Dataset = () => FakeSource.NoData();
        using var again = Watcher(NoSite());
        var v = again.View(false, null);
        Assert.Equal(("ok", "07.10.2026 12:20"), (v.State, v.Update));
        Assert.Equal(At("2026-10-08 19:00"), v.NextStart);
        Assert.False(await TickAt(again, "2026-10-07 12:40"));   // a restart does not bring a read early
    }

    private static StationState On(string at) => new() { LastAt = At(at), OnSince = At(at) };
    private static StationState OffAt(string since, string at, bool cut) => new() { LastAt = At(at), OffSince = At(since), Cut = cut, CutSince = cut ? At(since).AddMinutes(5) : null };
    private static StationState Back(string since, string at) => new() { LastAt = At(at), OnSince = At(since), MainsBackAt = At(since) };

    private static void RunCut(DtekWatcherService w, string day, string before, string start, string lastOff, string back)
    {
        w.AfterPoll(On($"{day} {before}"));
        w.AfterPoll(OffAt($"{day} {start}", $"{day} {start}", cut: false));
        w.AfterPoll(OffAt($"{day} {start}", $"{day} {lastOff}", cut: true));
        w.AfterPoll(new StationState { LastAt = At($"{day} {back}"), OnSince = At($"{day} {back}"), Cut = true, OffSince = null });   // back, not confirmed yet
        w.AfterPoll(Back($"{day} {back}", $"{day} {back}"));
    }

    // design.md §11 DTEK module, rule 3
    [Fact]
    public async Task Two_real_cuts_that_follow_one_queue_fix_it_and_it_is_saved()
    {
        using var w = Watcher(NoSite());
        await TickAt(w, "2026-10-07 12:00");
        RunCut(w, "2026-10-07", "17:01", "17:02", "20:57", "20:58");
        Assert.Null(w.Match.Fixed);
        Assert.Equal(["A", "B", "C"], w.View(false, null).Windows.Select(q => q.Queue));
        RunCut(w, "2026-10-08", "08:04", "08:05", "11:54", "11:55");
        Assert.Equal("A", w.Match.Fixed);
        _clock.Now = At("2026-10-08 12:00");
        Assert.Equal(("A", "matched"), (w.View(false, null).Queue, w.View(false, null).QueueSource));
        using var again = Watcher(NoSite());
        Assert.Equal("A", again.Match.Fixed);
    }

    // design.md §11 DTEK module, rule 9
    [Fact]
    public async Task A_cut_whose_start_or_end_fell_in_a_gap_of_readings_does_not_count()
    {
        using var w = Watcher(NoSite());
        await TickAt(w, "2026-10-07 12:00");
        // the PC slept through the return: the last reading off mains is an hour before mains is seen back
        RunCut(w, "2026-10-07", "17:01", "17:02", "19:50", "20:58");
        Assert.Equal(0, w.Match.Count);
        // the service started inside a cut: nothing on mains was seen before it
        using var late = Watcher(NoSite());
        late.AfterPoll(OffAt("2026-10-08 08:05", "2026-10-08 08:10", cut: true));
        late.AfterPoll(OffAt("2026-10-08 08:05", "2026-10-08 11:54", cut: true));
        late.AfterPoll(Back("2026-10-08 11:55", "2026-10-08 11:55"));
        Assert.Equal(0, late.Match.Count);
    }

    // design.md §11 DTEK module, rule 9
    [Fact]
    public async Task A_gap_reset_inside_a_cut_does_not_end_it()
    {
        using var w = Watcher(NoSite());
        await TickAt(w, "2026-10-07 12:00");
        w.AfterPoll(On("2026-10-07 17:01"));
        w.AfterPoll(OffAt("2026-10-07 17:02", "2026-10-07 17:10", cut: true));
        w.AfterPoll(new StationState { LastAt = At("2026-10-07 19:00"), OffSince = At("2026-10-07 19:00"), CutBeforeGap = true });   // after a gap
        w.AfterPoll(OffAt("2026-10-07 19:00", "2026-10-07 20:57", cut: true));
        w.AfterPoll(Back("2026-10-07 20:58", "2026-10-07 20:58"));
        Assert.Equal(("A", 1), (w.Match.Candidate, w.Match.Count));
    }

    // design.md §11 DTEK module, rule 18
    [Fact]
    public async Task The_sites_outage_line_shows_once_the_site_answered_inside_the_cut()
    {
        using var w = Watcher();
        await TickAt(w, "2026-10-07 12:00");
        w.AfterPoll(On("2026-10-07 12:20"));
        w.AfterPoll(OffAt("2026-10-07 12:21", "2026-10-07 12:27", cut: true));
        var answer = FakeSource.Answer();
        answer.House!.House!.Kind = "Emergency repair";
        answer.House.House.EndText = "15:00 07.10.2026";
        _source.Sites.Enqueue(answer);
        await TickAt(w, "2026-10-07 12:30");
        var v = w.View(true, 1);
        Assert.Equal(("Emergency repair", "07.10.2026 15:00", "no"), (v.OutageKind, v.OutageEnd, v.BatteryLasts));
    }
}

public sealed class DtekWatcherHttpTests
{
    private const string Dataset = "https://raw.githubusercontent.com/Baskerville42/outage-data-ua/main/data/kyiv-region.json";

    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<(string Method, Uri Uri, string Cookie, string Csrf, string Body, string IfNoneMatch)> Seen { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            string Header(string name) => string.Join("", request.Headers.TryGetValues(name, out var v) ? v : []);
            Seen.Add((request.Method.Method, request.RequestUri!, Header("Cookie"), Header("X-CSRF-Token"), body, Header("If-None-Match")));
            return answer(request);
        }
    }

    /// <summary>A body that declares no length, as a chunked answer does.</summary>
    private sealed class Unsized(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes, 0, bytes.Length);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private static HttpResponseMessage Text(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(body, Encoding.UTF8, "text/html") };

    private static HttpResponseMessage Redirect(string to)
    {
        var r = new HttpResponseMessage(HttpStatusCode.Found) { Content = new StringContent("") };
        r.Headers.Location = new Uri(to, UriKind.RelativeOrAbsolute);
        return r;
    }

    private static readonly string Big = "<meta name=\"csrf-token\" content=\"t\">\n" + string.Join('\n', Enumerable.Repeat(new string('x', 1023), 4100));

    private static async Task<(DtekFetch Fetch, Stub Stub)> Site(Func<HttpRequestMessage, HttpResponseMessage> answer, bool withHouse = true)
    {
        var stub = new Stub(answer);
        using var source = new DtekHttpSource(Config(), stub);
        return (await source.SiteAsync(withHouse, CancellationToken.None), stub);
    }

    private static async Task<(DtekDatasetResult Result, Stub Stub)> Data(Func<HttpRequestMessage, HttpResponseMessage> answer, string? etag = null)
    {
        var stub = new Stub(answer);
        using var source = new DtekHttpSource(Config(), stub);
        return (await source.DatasetAsync(etag, CancellationToken.None), stub);
    }

    // design.md §11 DTEK module, rule 15
    [Fact]
    public async Task The_dataset_is_one_get_and_its_validator_comes_back_for_the_next_read()
    {
        var (r, stub) = await Data(_ =>
        {
            var res = Text(Fixture("dataset.json"));
            res.Headers.ETag = new EntityTagHeaderValue("\"abc\"", isWeak: true);
            return res;
        });
        Assert.Equal((DtekStatus.Ok, false, "W/\"abc\""), (r.Status, r.NotModified, r.ETag));
        Assert.Equal("07.10.2026 11:43", r.Schedule!.Update);
        var only = Assert.Single(stub.Seen);
        Assert.Equal(("GET", Dataset, ""), (only.Method, only.Uri.ToString(), only.IfNoneMatch));
    }

    // design.md §11 DTEK module, rule 15
    [Fact]
    public async Task The_validator_is_sent_and_not_modified_is_an_answer_without_a_schedule()
    {
        var (r, stub) = await Data(_ => new HttpResponseMessage(HttpStatusCode.NotModified), "W/\"abc\"");
        Assert.Equal((DtekStatus.Ok, true, "W/\"abc\""), (r.Status, r.NotModified, r.ETag));
        Assert.Null(r.Schedule);
        Assert.Equal("W/\"abc\"", Assert.Single(stub.Seen).IfNoneMatch);
    }

    // design.md §11 DTEK module, rule 15
    [Fact]
    public async Task A_dataset_answer_that_does_not_fit_or_does_not_come_is_a_value()
    {
        Assert.Equal(DtekStatus.Format, (await Data(_ => Text("<html>not found</html>", HttpStatusCode.NotFound))).Result.Status);
        Assert.Equal(DtekStatus.Network, (await Data(_ => Text("down", HttpStatusCode.ServiceUnavailable))).Result.Status);
        Assert.Equal(DtekStatus.Format, (await Data(_ => Text("{\"fact\":{}}"))).Result.Status);
        var (r, _) = await Data(_ => throw new HttpRequestException("no such host"));
        Assert.Equal((DtekStatus.Network, "no such host"), (r.Status, r.Detail));
    }

    // design.md §11 DTEK module, rules 4, 15
    [Theory]
    [InlineData("https://evil.example/data.json")]
    [InlineData("https://www.dtek-krem.com.ua/ua/shutdowns")]   // the other allowed host is not the dataset's
    [InlineData("http://raw.githubusercontent.com/x.json")]
    public async Task A_dataset_redirect_to_another_host_is_refused_with_its_own_reason(string to)
    {
        var (r, stub) = await Data(_ => Redirect(to));
        Assert.Equal(DtekStatus.Refused, r.Status);
        Assert.Contains("a redirect to", r.Detail, StringComparison.Ordinal);
        Assert.Contains("refused", r.Detail, StringComparison.Ordinal);
        Assert.Single(stub.Seen);
    }

    // design.md §11 DTEK module, rule 4
    [Fact]
    public async Task A_dataset_over_4_mb_is_refused_with_its_own_reason()
    {
        var (r, _) = await Data(_ => Text(Big));
        Assert.Equal((DtekStatus.Refused, "the dataset is over 4 MB"), (r.Status, r.Detail));
        (r, _) = await Data(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new Unsized(Encoding.UTF8.GetBytes(Big)) });
        Assert.Equal((DtekStatus.Refused, "the dataset is over 4 MB"), (r.Status, r.Detail));
    }

    // design.md §11 DTEK module, rule 4
    [Fact]
    public async Task One_site_read_is_the_page_and_then_the_address_with_the_pages_token_and_cookies()
    {
        var (f, stub) = await Site(r =>
        {
            if (r.Method == HttpMethod.Post) return Text(Fixture("house.json"));
            var page = Text(Fixture("schedule-page.html"));
            page.Headers.TryAddWithoutValidation("Set-Cookie", "session=abc; path=/; HttpOnly");
            page.Headers.TryAddWithoutValidation("Set-Cookie", "_csrf-x=def; path=/");
            return page;
        });
        Assert.Equal((DtekStatus.Ok, "site"), (f.Page.Status, f.Page.Schedule!.Source));
        Assert.Equal(["GPV4.1", "GPV6.2", "GPV2.2"], f.House!.House!.Queues);
        Assert.Equal(["GET https://www.dtek-krem.com.ua/ua/shutdowns", "POST https://www.dtek-krem.com.ua/ua/ajax"], stub.Seen.Select(s => $"{s.Method} {s.Uri}"));
        var post = stub.Seen[1];
        Assert.Equal("session=abc; _csrf-x=def", post.Cookie);
        Assert.StartsWith("fixtureCsrfToken", post.Csrf, StringComparison.Ordinal);
        var form = post.Body.Split('&').Select(Uri.UnescapeDataString).Select(p => p.Replace('+', ' ')).ToList();
        Assert.Equal(["method=getHomeNum", "data[0][name]=city", "data[0][value]=Test City", "data[1][name]=street", "data[1][value]=Test Street",
            "data[2][name]=updateFact", "data[2][value]=07.10.2026 11:43"], form);
    }

    // design.md §11 DTEK module, rules 4, 18
    [Fact]
    public async Task Without_the_address_only_the_page_is_asked()
    {
        var (f, stub) = await Site(_ => Text(Fixture("schedule-page.html")), withHouse: false);
        Assert.Equal(DtekStatus.Ok, f.Page.Status);
        Assert.Null(f.House);
        Assert.Single(stub.Seen);
        // asked for the house, but the config has no address
        using var noAddress = new Stub(_ => Text(Fixture("schedule-page.html")));
        using var source = new DtekHttpSource(new DtekConfig { Enabled = true, Site = true }, noAddress);
        Assert.Null((await source.SiteAsync(true, CancellationToken.None)).House);
        Assert.Single(noAddress.Seen);
    }

    // design.md §11 DTEK module, rule 4
    [Theory]
    [InlineData("https://evil.example/ua/shutdowns")]
    [InlineData("http://www.dtek-krem.com.ua/ua/shutdowns")]       // not TLS
    [InlineData("https://www.dtek-krem.com.ua:8443/ua/shutdowns")]  // another port
    [InlineData("https://www.dtek-krem.com.ua.evil.example/")]
    [InlineData("https://raw.githubusercontent.com/x")]            // the other allowed host is not the site's
    [InlineData("//evil.example/x")]
    public async Task A_site_redirect_to_another_host_is_refused_and_no_request_goes_there(string to)
    {
        var (f, stub) = await Site(_ => Redirect(to));
        Assert.Equal(DtekStatus.Refused, f.Page.Status);
        Assert.Contains("a redirect to", f.Page.Detail, StringComparison.Ordinal);
        Assert.Null(f.House);
        var only = Assert.Single(stub.Seen);
        Assert.Equal("www.dtek-krem.com.ua", only.Uri.Host);
    }

    // design.md §11 DTEK module, rule 4
    [Fact]
    public async Task A_redirect_inside_the_host_is_followed_and_a_loop_of_them_is_refused()
    {
        var (f, stub) = await Site(r => r.RequestUri!.AbsolutePath == "/ua/shutdowns" ? Redirect("/ua/shutdowns/") : Text(Fixture("schedule-page.html")), withHouse: false);
        Assert.Equal(DtekStatus.Ok, f.Page.Status);
        Assert.Equal(2, stub.Seen.Count);
        var (loop, loopStub) = await Site(_ => Redirect("/ua/shutdowns"));
        Assert.Equal((DtekStatus.Refused, "too many redirects"), (loop.Page.Status, loop.Page.Detail));
        Assert.Equal(4, loopStub.Seen.Count);
    }

    // design.md §11 DTEK module, rule 4
    [Fact]
    public async Task A_site_body_over_4_mb_is_refused_with_its_own_reason()
    {
        var (f, _) = await Site(_ => Text(Big));
        Assert.Equal((DtekStatus.Refused, "the page is over 4 MB"), (f.Page.Status, f.Page.Detail));
        // without a declared length too
        var (g, _) = await Site(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new Unsized(Encoding.UTF8.GetBytes(Big)) });
        Assert.Equal((DtekStatus.Refused, "the page is over 4 MB"), (g.Page.Status, g.Page.Detail));
        // the address answer has the same cap
        var (h, _) = await Site(r => r.Method == HttpMethod.Post ? Text(Big) : Text(Fixture("schedule-page.html")));
        Assert.Equal(DtekStatus.Ok, h.Page.Status);
        Assert.Equal((DtekStatus.Refused, "the address answer is over 4 MB"), (h.House!.Status, h.House.Detail));
    }

    // design.md §11 DTEK module, rule 6
    [Fact]
    public async Task The_challenge_page_is_blocked_whatever_its_status_and_the_address_is_not_asked()
    {
        var (f, stub) = await Site(_ => Text(Fixture("blocked.html")));
        Assert.Equal(DtekStatus.Blocked, f.Page.Status);
        Assert.Single(stub.Seen);
        Assert.Equal(DtekStatus.Blocked, (await Site(_ => Text("denied", HttpStatusCode.Forbidden))).Fetch.Page.Status);
        Assert.Equal(DtekStatus.Network, (await Site(_ => Text("down", HttpStatusCode.BadGateway))).Fetch.Page.Status);
        Assert.Equal(DtekStatus.Format, (await Site(_ => Text("gone", HttpStatusCode.NotFound))).Fetch.Page.Status);
    }

    // design.md §11 DTEK module, rule 6
    [Fact]
    public async Task A_network_failure_is_a_value_and_the_address_failing_keeps_the_page()
    {
        var (f, _) = await Site(_ => throw new HttpRequestException("no such host"));
        Assert.Equal((DtekStatus.Network, "no such host"), (f.Page.Status, f.Page.Detail));
        var (g, _) = await Site(r => r.Method == HttpMethod.Post ? throw new HttpRequestException("reset") : Text(Fixture("schedule-page.html")));
        Assert.Equal(DtekStatus.Ok, g.Page.Status);
        Assert.Equal(DtekStatus.Network, g.House!.Status);
        var (h, _) = await Site(r => r.Method == HttpMethod.Post ? Text(Fixture("blocked.html")) : Text(Fixture("schedule-page.html")));
        Assert.Equal(DtekStatus.Blocked, h.House!.Status);
    }

    // design.md §11 DTEK module, rule 15: exactly the dataset's host and the site's host, nothing else
    [Fact]
    public async Task Only_the_two_configured_hosts_are_ever_requested()
    {
        using var stub = new Stub(r => r.RequestUri!.Host == "data.example" ? Text(Fixture("dataset.json"))
            : r.Method == HttpMethod.Post ? Text(Fixture("house.json")) : Text(Fixture("schedule-page.html")));
        var config = Config();
        config.Host = "schedule.example";
        config.DatasetUrl = "https://data.example/sets/region.json";
        using var source = new DtekHttpSource(config, stub);
        Assert.Equal(DtekStatus.Ok, (await source.DatasetAsync(null, CancellationToken.None)).Status);
        Assert.Equal(DtekStatus.Ok, (await source.SiteAsync(true, CancellationToken.None)).House!.Status);
        Assert.Equal(["https://data.example/sets/region.json", "https://schedule.example/ua/shutdowns", "https://schedule.example/ua/ajax"], stub.Seen.Select(s => s.Uri.ToString()));
        Assert.All(stub.Seen, s => Assert.True(s.Uri.Host is "data.example" or "schedule.example", s.Uri.Host));
        Assert.All(stub.Seen, s => Assert.Equal("https", s.Uri.Scheme));
    }
}

public sealed class DtekWatcherHostTests : IDisposable
{
    private const int Port = 18940;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bluetti-watcher-test-" + Guid.NewGuid().ToString("N"));
    private readonly StationStore _store;
    private readonly ManualClock _clock = new(At("2026-10-07 12:00"));

    public DtekWatcherHostTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new StationStore(Path.Combine(_dir, "station.db"));
    }

    public void Dispose()
    {
        _store.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private async Task<JsonElement> Now(StationConfig cfg, IDtekWatcher? watcher, QueueReader? reader = null)
    {
        using var service = new StationService(cfg, reader ?? new QueueReader(), new FakeActions(), new FakeHibernator(true), new FixedFlag(Fx.Station), _store, _clock, _ => { });
        if (reader is not null) await service.PollAsync(CancellationToken.None);
        var api = watcher is null ? new StationApi(cfg, service, _store, _clock, _clock.Now) : new StationApi(cfg, service, _store, _clock, _clock.Now, watcher);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Host"] = $"127.0.0.1:{Port}" };
        var r = await api.HandleAsync(new HttpRequest("GET", "/api/now", new Dictionary<string, string>(), headers, []), CancellationToken.None);
        using var doc = JsonDocument.Parse(r.Body);
        return doc.RootElement.Clone();
    }

    // design.md §11 DTEK module, rule 14
    [Fact]
    public async Task Api_now_carries_the_module_as_one_object_off_by_default()
    {
        var now = await Now(new StationConfig { DataDir = _dir, Port = Port }, null);
        var w = now.GetProperty("dtekWatcher");
        Assert.Equal(("off", "off", "off"), (w.GetProperty("state").GetString(), w.GetProperty("datasetState").GetString(), w.GetProperty("siteState").GetString()));
        Assert.Equal(0, w.GetProperty("queues").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, w.GetProperty("batteryLasts").ValueKind);
        Assert.DoesNotContain(now.EnumerateObject(), p => p.Name.StartsWith("dtek", StringComparison.Ordinal) && p.Name != "dtekWatcher");
    }

    // design.md §11 DTEK module, rules 14, 16
    [Fact]
    public async Task Api_now_shows_both_times_the_source_the_queues_and_the_next_outage()
    {
        var cfg = new StationConfig { DataDir = _dir, Port = Port, DtekWatcher = Config(group: "A", queues: House) };
        using var source = new FakeSource();
        using var watcher = new DtekWatcherService(cfg, () => source, new DtekStore(_store), _clock, _ => { });
        await watcher.TickAsync(CancellationToken.None);
        var w = (await Now(cfg, watcher, new QueueReader { Default = Fx.Grid(80) })).GetProperty("dtekWatcher");
        Assert.Equal(("ok", "dataset", "ok", "blocked"),
            (w.GetProperty("state").GetString(), w.GetProperty("source").GetString(), w.GetProperty("datasetState").GetString(), w.GetProperty("siteState").GetString()));
        Assert.Equal("07.10.2026 11:43", w.GetProperty("update").GetString());
        Assert.Equal(At("2026-10-07 11:45"), w.GetProperty("collectedAt").GetDateTimeOffset());
        Assert.Equal(["A", "B", "C"], w.GetProperty("queues").EnumerateArray().Select(q => q.GetString()));
        Assert.Equal(("config", "A", "config"), (w.GetProperty("queuesSource").GetString(), w.GetProperty("queue").GetString(), w.GetProperty("queueSource").GetString()));
        Assert.Equal(At("2026-10-07 17:00"), w.GetProperty("nextStart").GetDateTimeOffset());
        Assert.Equal(At("2026-10-07 21:00"), w.GetProperty("nextEnd").GetDateTimeOffset());
        Assert.Equal(JsonValueKind.Null, w.GetProperty("return").ValueKind);
        Assert.Equal("17:00-21:00", w.GetProperty("windows")[0].GetProperty("today").GetString());
        Assert.Equal("08:00-12:00", w.GetProperty("windows")[0].GetProperty("tomorrow").GetString());
        foreach (var name in new[] { "note", "nextQueue", "outageKind", "outageStart", "outageEnd", "outageEndAt", "batteryLasts", "today", "tomorrow" })
            Assert.True(w.TryGetProperty(name, out _), name);
        Assert.False(w.TryGetProperty("error", out _));
        Assert.Equal((false, JsonValueKind.Null, JsonValueKind.Null, JsonValueKind.Null, 0, JsonValueKind.Null, 0),
            (w.GetProperty("overdue").GetBoolean(), w.GetProperty("overdueMin").ValueKind, w.GetProperty("plannedReturn").ValueKind, w.GetProperty("batteryUntil").ValueKind,
             w.GetProperty("planChanges").GetInt32(), w.GetProperty("lateBackMin").ValueKind, w.GetProperty("lateCuts").GetInt32()));
    }

    private sealed class Shown(DtekView view) : IDtekWatcher
    {
        public Task RunAsync(CancellationToken ct) => Task.CompletedTask;
        public void AfterPoll(StationState state) { }
        public DtekView View(bool cut, double? hoursLeft, double? hoursToHibernate = null) => view;
        public void Dispose() { }
    }

    // design.md §11 DTEK module, rule 27
    [Fact]
    public async Task Api_now_carries_overdue_the_battery_time_plan_changes_and_the_usual_lateness()
    {
        var cfg = new StationConfig { DataDir = _dir, Port = Port };
        var view = DtekView.Broken(new PageNote(PageCodes.DtekModuleError, "")) with
        {
            State = "ok", BatteryLasts = "unknown", Overdue = true, OverdueMin = 60, PlannedReturn = At("2026-10-07 21:00"), BatteryUntil = At("2026-10-08 00:00"),
            PlanChanges = 2, LateBackMin = -7, LateCuts = 5,
        };
        using var shown = new Shown(view);
        var w = (await Now(cfg, shown)).GetProperty("dtekWatcher");
        Assert.Equal((true, 60, At("2026-10-07 21:00"), At("2026-10-08 00:00"), 2, -7, 5),
            (w.GetProperty("overdue").GetBoolean(), w.GetProperty("overdueMin").GetInt32(), w.GetProperty("plannedReturn").GetDateTimeOffset(), w.GetProperty("batteryUntil").GetDateTimeOffset(),
             w.GetProperty("planChanges").GetInt32(), w.GetProperty("lateBackMin").GetInt32(), w.GetProperty("lateCuts").GetInt32()));
    }

    // design.md §11 DTEK module, rule 28 (c)
    [Fact]
    public async Task Api_now_says_which_queues_of_a_set_each_interval_came_from()
    {
        var cfg = new StationConfig { DataDir = _dir, Port = Port };
        var day = new DtekQueueDays("GPV4.1+GPV6.2", "17:00-24:00", "")
        {
            Intervals = [new DtekInterval(At("2026-10-07 17:00"), At("2026-10-08 00:00"), ["GPV4.1", "GPV6.2"])],
        };
        var view = DtekView.Broken(new PageNote(PageCodes.DtekModuleError, "")) with
        {
            State = "ok", Queue = "GPV4.1+GPV6.2", QueueSet = ["GPV4.1", "GPV6.2"], NextQueue = "GPV4.1+GPV6.2", NextQueues = ["GPV4.1", "GPV6.2"], Windows = [day],
        };
        using var shown = new Shown(view);
        var w = (await Now(cfg, shown)).GetProperty("dtekWatcher");
        string[] Names(JsonElement e) => [.. e.EnumerateArray().Select(x => x.GetString()!)];
        Assert.Equal(["GPV4.1", "GPV6.2"], Names(w.GetProperty("queueSet")));
        Assert.Equal(["GPV4.1", "GPV6.2"], Names(w.GetProperty("nextQueues")));
        var interval = w.GetProperty("windows")[0].GetProperty("intervals")[0];
        Assert.Equal((At("2026-10-07 17:00"), At("2026-10-08 00:00")), (interval.GetProperty("start").GetDateTimeOffset(), interval.GetProperty("end").GetDateTimeOffset()));
        Assert.Equal(["GPV4.1", "GPV6.2"], Names(interval.GetProperty("queues")));
    }

    // design.md §11 DTEK module, rule 8
    [Fact]
    public void The_modules_config_comes_from_config_json_and_it_is_off_without_it()
    {
        var d = new StationConfig().DtekWatcher;
        Assert.Equal((false, false, 90, "www.dtek-krem.com.ua", "https://raw.githubusercontent.com/Baskerville42/outage-data-ua/main/data/kyiv-region.json"),
            (d.Enabled, d.Site, d.StaleAfterMin, d.Host, d.DatasetUrl));
        Assert.Null(d.Queues);
        string file = Path.Combine(_dir, "config.json");
        File.WriteAllText(file, """
            { "port": 18999, "dtekWatcher": { "enabled": true, "queues": ["GPV4.1", "GPV6.2", "GPV2.2"], "group": "GPV4.1", "site": false, "staleAfterMin": 45,
              "datasetUrl": "https://data.example/x.json", "city": "Test City", "street": "Test Street", "house": "1" } }
            """);
        var c = StationConfig.Load(["--config", file]).DtekWatcher;
        Assert.Equal((true, false, 45, "GPV4.1", "https://data.example/x.json", "Test City", "Test Street", "1"), (c.Enabled, c.Site, c.StaleAfterMin, c.Group, c.DatasetUrl, c.City, c.Street, c.House));
        Assert.Equal(["GPV4.1", "GPV6.2", "GPV2.2"], c.Queues!);
        Assert.True(c.HasAddress);
        Assert.Null(c.Problem());
    }

    private static string Source([CallerFilePath] string path = "") => path;

    // design.md §11 DTEK module, rule 2: the schedule never influences eco, max or hibernate
    [Fact]
    public void The_decision_layer_has_no_dependency_on_the_module()
    {
        const string module = "BluettiWatcher.DtekWatcher";
        var decision = typeof(StationDecision).Assembly.GetTypes().Where(t => t.Namespace == "BluettiWatcher.Decision").ToList();
        Assert.Contains(typeof(StationDecision), decision);
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        bool Foreign(Type? t) => t is not null && (t.Namespace == module || t.GenericTypeArguments.Any(Foreign) || (t.HasElementType && Foreign(t.GetElementType())));
        foreach (var t in decision)
        {
            Assert.DoesNotContain(t.GetFields(all), f => Foreign(f.FieldType));
            Assert.DoesNotContain(t.GetProperties(all), p => Foreign(p.PropertyType));
            foreach (var m in t.GetMethods(all))
            {
                Assert.False(Foreign(m.ReturnType), $"{t.Name}.{m.Name}");
                Assert.DoesNotContain(m.GetParameters(), p => Foreign(p.ParameterType));
                Assert.DoesNotContain(m.GetMethodBody()?.LocalVariables ?? [], v => Foreign(v.LocalType));
            }
        }
        // and no decision source so much as names it (the config's dtekWatcher section included)
        string dir = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Source())!, "..", "..", "..", "src", "BluettiWatcher", "Decision"));
        var files = Directory.GetFiles(dir, "*.cs");
        Assert.NotEmpty(files);
        Assert.All(files, f => Assert.DoesNotContain("dtek", File.ReadAllText(f), StringComparison.OrdinalIgnoreCase));
    }
}
