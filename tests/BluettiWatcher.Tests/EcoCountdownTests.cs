using System.Net;
using System.Text;
using System.Text.Json;
using BluettiWatcher.Decision;
using BluettiWatcher.Http;
using BluettiWatcher.Storage;
using static BluettiWatcher.Tests.Fx;

namespace BluettiWatcher.Tests;

/// <summary>StationDecision.EcoAt: when eco comes on if mains stays off, from the state the cut itself reads (service defaults).</summary>
public class EcoAtTests
{
    private static S M(Reading? r, int dt = 60) => new(r, Dt: dt);

    private static DateTimeOffset? EcoAt(StationDecisionResult d, DateTimeOffset now, FlagView? flag = null, StationConfig? cfg = null) =>
        StationDecision.EcoAt(d.State, flag ?? Flag(), now, cfg ?? Defaults());

    [Fact]
    public void Mains_off_120s_eco_at_first_off_plus_mains_lost_sec()
    {
        var d = Step([M(Bat(80)), M(Bat(80), 120)], cfg: Defaults());
        Assert.Equal(T0.AddSeconds(180), EcoAt(d[1], T0.AddSeconds(120)));
    }

    [Fact]
    public void The_threshold_is_the_configured_mains_lost_sec()
    {
        var cfg = Defaults(); cfg.MainsLostSec = 420;
        var d = Step([M(Bat(80)), M(Bat(80), 120)], cfg: cfg);
        Assert.Equal(T0.AddSeconds(420), EcoAt(d[1], T0.AddSeconds(120), cfg: cfg));
    }

    [Fact]
    public void The_cut_comes_at_the_first_reading_at_eco_at_with_eco_on()
    {
        var d = Step([M(Bat(80)), M(Bat(80)), M(Bat(80)), M(Bat(80))], cfg: Defaults());
        Assert.Equal(T0.AddSeconds(180), EcoAt(d[2], T0.AddSeconds(120)));
        Assert.DoesNotContain(StationAction.EcoOn, d[2].Actions);
        Assert.Equal(T0.AddSeconds(180), d[3].State.CutSince);
        Assert.Contains(StationAction.EcoOn, d[3].Actions);
    }

    [Fact]
    public void Null_once_the_cut_has_begun()
    {
        var d = Step([M(Bat(80)), M(Bat(80)), M(Bat(80)), M(Bat(80))], cfg: Defaults());
        Assert.True(d[3].State.Cut);
        Assert.Null(EcoAt(d[3], T0.AddSeconds(180)));
    }

    [Fact]
    public void Null_on_mains() => Assert.Null(EcoAt(Step([M(Bat(80)), M(Grid(80))], cfg: Defaults())[1], T0.AddSeconds(60)));

    [Fact]
    public void Null_with_mains_unknown() =>
        Assert.Null(EcoAt(Step([M(Bat(80)), M(new Reading(80, 0, 220, "AC_OUTPUT"))], cfg: Defaults())[1], T0.AddSeconds(60)));

    [Theory]
    [InlineData("eco", "station")]
    [InlineData("eco", "manual")]
    [InlineData("emergency", "station")]
    [InlineData("unreadable", "")]
    public void Null_when_the_flag_is_not_normal(string mode, string source) =>
        Assert.Null(EcoAt(Step([M(Bat(80)), M(Bat(80))], cfg: Defaults())[1], T0.AddSeconds(60), Flag(mode, source)));

    [Fact]
    public void Null_while_the_station_is_silent()
    {
        var d = Step([M(Bat(80)), M(null, 20), M(null, 20), M(null, 20)], cfg: Defaults());
        Assert.True(d[3].State.Silent);
        Assert.Null(EcoAt(d[3], T0.AddSeconds(60)));
    }

    [Fact]
    public void Null_once_the_last_reading_is_older_than_max_gap_sec_the_restart_time_is_not_known()
    {
        var cfg = Defaults();
        var d = Step([M(Bat(80))], cfg: cfg);
        Assert.Equal(T0.AddSeconds(180), EcoAt(d[0], T0.AddSeconds(cfg.MaxGapSec)));
        Assert.Null(EcoAt(d[0], T0.AddSeconds(cfg.MaxGapSec + 1)));
        // The decision agrees: a reading past the gap starts the mains-lost timer over at itself.
        var after = StationDecision.Decide(Bat(80), d[0].State, Flag(), T0.AddSeconds(cfg.MaxGapSec + 1), cfg);
        Assert.Equal(T0.AddSeconds(cfg.MaxGapSec + 1), after.State.OffSince);
    }

    [Fact]
    public void Null_with_the_clock_behind_the_last_reading()
    {
        var d = Step([M(Bat(80)), M(Bat(80))], cfg: Defaults());
        Assert.Null(EcoAt(d[1], T0.AddSeconds(30)));
    }

    [Fact]
    public void Max_chosen_during_the_outage_outlives_the_cut_no_eco()
    {
        var d = Step([M(Bat(80)), M(Bat(80)), M(Bat(80)), M(Bat(80))], cfg: Defaults(),
            before: (i, now, s) => i == 1 ? StationDecision.SetManual(s, ManualMode.Max, now) : s);
        Assert.Null(EcoAt(d[2], T0.AddSeconds(120)));
        Assert.DoesNotContain(StationAction.EcoOn, d[3].Actions);
    }

    [Fact]
    public void Max_chosen_before_the_outage_ends_at_the_cut_eco_comes()
    {
        var start = StationDecision.SetManual(new StationState(), ManualMode.Max, T0.AddMinutes(-10));
        var d = Step([M(Bat(80)), M(Bat(80)), M(Bat(80)), M(Bat(80))], state: start, cfg: Defaults());
        Assert.Equal(T0.AddSeconds(180), EcoAt(d[2], T0.AddSeconds(120)));
        Assert.Contains(StationAction.EcoOn, d[3].Actions);
    }

    [Fact]
    public void A_20_percent_streak_not_yet_agreed_at_eco_at_still_gives_null_the_reading_after_it_brings_the_emergency()
    {
        // The streak begins at +100 s; at eco-at (+180 s) it is 80 s old, under agreeSec (90): the next reading decides.
        var d = Step([M(Bat(21)), M(Bat(20), 100), M(Bat(20), 100)], cfg: Defaults());
        Assert.Null(EcoAt(d[1], T0.AddSeconds(100)));
        Assert.Contains(StationAction.EmergencyOn, d[2].Actions);
        Assert.DoesNotContain(StationAction.EcoOn, d[2].Actions);
    }

    [Fact]
    public void Eco_or_the_emergency_comes_at_the_first_counted_reading_at_or_after_eco_at_over_varied_timing()
    {
        // Poll jitter, unanswered polls, mains blips, implausible jumps (rejected, then a rebase), batteries falling through 20 %.
        var cfg = Defaults();
        int[] gaps = [15, 30, 45, 60, 90, 120, 170, 240];
        int promises = 0, kept = 0;
        // CA5394 off here: the random source is seeded on purpose for reproducible timing, nothing secret
#pragma warning disable CA5394
        for (int seed = 1; seed <= 400; seed++)
        {
            var rnd = new Random(seed);
            var steps = new List<object>();
            var times = new List<DateTimeOffset>();
            var t = T0;
            double soc = seed % 3 == 0 ? 24 : 80;
            for (int k = 0; k < 30; k++)
            {
                int dt = k == 0 ? 0 : gaps[rnd.Next(gaps.Length)];
                t = t.AddSeconds(dt);
                double p = rnd.NextDouble();
                Reading? r;
                if (p < 0.10) r = null;
                else if (p < 0.15) r = Grid(soc);
                else
                {
                    soc = p < 0.20 ? Math.Clamp(soc + (rnd.Next(2) == 0 ? -15 : 15), 10, 95) : Math.Max(10, soc - 0.5);
                    r = Bat(soc);
                }
                steps.Add(new S(r, Dt: dt));
                times.Add(t);
            }
#pragma warning restore CA5394
            var d = Step([.. steps], cfg: cfg);
            for (int i = 0; i < d.Count; i++)
            {
                if (StationDecision.EcoAt(d[i].State, Flag(), times[i], cfg) is not { } at) continue;
                promises++;
                for (int j = i + 1; j < d.Count; j++)
                {
                    // Mains seen, or a gap that starts the timer over: the promise was "if mains stays off".
                    if (d[j].State.OffSince != d[i].State.OffSince) break;
                    var acts = d[j].Actions;
                    if (d[j].Battery is null || times[j] < at)
                    {
                        // Unanswered, rejected, a rebase, or before eco-at: no cut, no mode.
                        Assert.False(d[j].Battery is not null && d[j].State.Cut, $"seed {seed}: cut at step {j} before {at:HH:mm:ss}");
                        Assert.DoesNotContain(StationAction.EcoOn, acts);
                        continue;
                    }
                    Assert.True(acts.Contains(StationAction.EcoOn) || acts.Contains(StationAction.EmergencyOn), $"seed {seed}: step {j} at {times[j]:HH:mm:ss}, eco-at {at:HH:mm:ss}: {Acts(d[j])}");
                    Assert.True(d[j].State.Cut);
                    kept++;
                    break;
                }
            }
        }
        Assert.True(promises > 300 && kept > 100, $"promises {promises}, checked to the cut {kept}");
    }
}

/// <summary>/api/now carries ecoAt; the page counts down to it.</summary>
public sealed class EcoAtApiTests : IDisposable
{
    private const int Port = 18940;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bluetti-station-test-" + Guid.NewGuid().ToString("N"));
    private readonly StationStore _store;

    public EcoAtApiTests()
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

    private static HttpRequest Get(string path) =>
        new("GET", path, new Dictionary<string, string>(), new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Host"] = $"127.0.0.1:{Port}" }, []);

    private async Task<JsonElement> NowAfterTwoReadings(FlagView flag)
    {
        var clock = new ManualClock(T0);
        var cfg = new StationConfig { DataDir = _dir, Port = Port };
        using var s = new StationService(cfg, new QueueReader { Default = Bat(80) }, new FakeActions(), new FakeHibernator(true), new FixedFlag(flag), _store, clock, _ => { });
        await s.PollAsync(CancellationToken.None);
        clock.Now = T0.AddSeconds(120);
        await s.PollAsync(CancellationToken.None);
        var r = await new StationApi(cfg, s, _store, clock, T0).HandleAsync(Get("/api/now"), CancellationToken.None);
        using var doc = JsonDocument.Parse(r.Body);
        return doc.RootElement.GetProperty("ecoAt").Clone();
    }

    [Fact]
    public async Task Now_gives_eco_at_while_the_confirmation_runs()
    {
        var at = await NowAfterTwoReadings(Flag());
        Assert.Equal(T0.AddSeconds(180), DateTimeOffset.Parse(at.GetString()!, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Now_gives_null_when_eco_is_already_on() =>
        Assert.Equal(JsonValueKind.Null, (await NowAfterTwoReadings(Station)).ValueKind);

    [Fact]
    public async Task The_page_counts_down_to_eco_at()
    {
        var cfg = new StationConfig { DataDir = _dir, Port = Port };
        using var s = new StationService(cfg, new QueueReader(), new FakeActions(), new FakeHibernator(true), new FixedFlag(Flag()), _store, new ManualClock(T0), _ => { });
        var page = await new StationApi(cfg, s, _store, new ManualClock(T0), T0).HandleAsync(Get("/"), CancellationToken.None);
        string html = Encoding.UTF8.GetString(page.Body);
        Assert.Equal((int)HttpStatusCode.OK, page.Status);
        Assert.Contains("D.ecoAt", html, StringComparison.Ordinal);
        Assert.Contains("t('ecoIn'", html, StringComparison.Ordinal);    }
}
