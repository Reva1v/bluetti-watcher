using System.Reflection;
using System.Text.Json;
using BluettiWatcher.Decision;
using BluettiWatcher.Http;
using BluettiWatcher.Runtime;
using BluettiWatcher.Storage;
using static BluettiWatcher.Tests.Fx;

namespace BluettiWatcher.Tests;

/// <summary>design.md §7 Battery time: the measured discharge rate, the efficiency formula only while data is thin.</summary>
public sealed class RuntimeEstimatorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bluetti-watcher-test-" + Guid.NewGuid().ToString("N"));
    private static readonly long Now = T0.ToUnixTimeSeconds();

    public RuntimeEstimatorTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private static HistoryRow Row(int minute, double? soc, bool mains = false, double load = 200) =>
        new(Now + minute * 60L, soc, mains ? 350 : 0, 0, load, 0, 230, 50, mains ? 1 : 0, null);

    /// <summary>One reading a minute over [from, to] minutes; <paramref name="soc"/> of the minute.</summary>
    private static List<HistoryRow> Rows(int from, int to, Func<int, double?> soc, bool mains = false, double load = 200) =>
        Enumerable.Range(from, to - from + 1).Select(m => Row(m, soc(m), mains, load)).ToList();

    /// <summary>10 % per hour: one point every 6 minutes, 80 % an hour ago, 70 % now.</summary>
    private static List<HistoryRow> TenPerHour() => Rows(-60, 0, m => 80 - (m + 60) / 6);

    private static RuntimeEstimate Est(List<HistoryRow> rows, double soc = 70, StationConfig? c = null, double? learned = null) =>
        RuntimeEstimator.Estimate(Bat(soc), rows, Now, c ?? new StationConfig(), learned);

    [Fact]
    public void A_steady_drop_gives_the_measured_rate_to_empty_and_to_the_hibernate_level()
    {
        // 10 %/h at 200 W over the window; the current reading draws 220 W: 70 / 10 x 200 / 220 and 65 / 10 x 200 / 220
        var e = Est(TenPerHour());
        Assert.Equal(("rate", 10.0, 6.4, 5.9), (e.Source, e.RatePerHour, e.HoursLeft, e.HoursToHibernate));
        // 200 W out of 10 % of 2304 Wh an hour = 0.868
        Assert.Equal(0.868, e.MeasuredEfficiency);
    }

    [Fact]
    public void A_cut_just_started_falls_back_to_the_configured_efficiency()
    {
        var e = Est(Rows(-5, 0, _ => 100), soc: 100);
        Assert.Equal(("config", (double?)null, 8.9), (e.Source, e.RatePerHour, e.HoursLeft));   // 2304 Wh x 0.85 / 220 W
    }

    [Fact]
    public void Coarse_steps_at_the_top_fall_back_until_the_drop_is_enough()
    {
        // 100 for 15 minutes, then 99: one step, no rate
        Assert.Equal("config", Est(Rows(-15, 0, m => m < 0 ? 100 : 99), soc: 99).Source);
        // a long 100 plateau does not dilute the rate: it counts from the first step
        var rows = Rows(-60, 0, m => m < -40 ? 100 : 99 - (m + 40) / 5);   // 100 till -40, then 1 point per 5 min
        var e = Est(rows, soc: 91);
        Assert.Equal(("rate", 12.0), (e.Source, e.RatePerHour));
    }

    [Theory]
    [InlineData(2, 60)]    // steps at -30 and 0: a 1-point drop, under 3
    [InlineData(4, 18)]    // steps from -13 (79) to 0 (76): 3 points over 13 minutes, under 20
    public void Thin_data_falls_back(int points, int minutes)
    {
        var rows = Rows(-minutes, 0, m => 80 - Math.Min(points, (m + minutes) * points / minutes));
        Assert.Equal("config", Est(rows).Source);
    }

    [Fact]
    public void Exactly_the_minimum_drop_and_span_measure()
    {
        // steps at -20 (79), -10 (78), 0 (77): 2 points over exactly 20 minutes, the drop lowered to 2
        var rows = Rows(-30, 0, m => 80 - (m + 30) / 10);
        var c = new StationConfig { RateMinDrop = 2 };
        var e = RuntimeEstimator.Estimate(Bat(77), rows, Now, c, null);
        Assert.Equal(("rate", 6.0), (e.Source, e.RatePerHour));   // 2 points over 20 min
    }

    [Fact]
    public void A_load_raised_now_shortens_the_measured_time_at_once()
    {
        // 10 %/h at 200 W over the window; Max raises the load to 400 W now: half the time, not the window's 7 h
        var c = new StationConfig();
        var at400 = RuntimeEstimator.Estimate(new Reading(70, 0, 400, "INVERTER_OUTPUT"), TenPerHour(), Now, c, null);
        Assert.Equal(("rate", 10.0, 3.5), (at400.Source, at400.RatePerHour, at400.HoursLeft));
        var at100 = RuntimeEstimator.Estimate(new Reading(70, 0, 100, "INVERTER_OUTPUT"), TenPerHour(), Now, c, null);
        Assert.Equal(14.0, at100.HoursLeft);
        // a load change inside the window: the rate is the window's average (14 points in 57 min) at the window's mean load
        var rows = Rows(-60, 0, m => m <= -30 ? 90 - (m + 60) / 3 : 80 - (m + 30) / 6);
        Assert.Equal(14.7, Est(rows, soc: 75).RatePerHour);
    }

    [Fact]
    public void A_dirty_window_measures_but_teaches_nothing()
    {
        var solar = TenPerHour().Select(r => r with { InDc = 40 }).ToList();
        var e = Est(solar);
        Assert.Equal(("rate", (double?)null), (e.Source, e.MeasuredEfficiency));
        var jump = TenPerHour();
        for (int k = 31; k < jump.Count; k++) jump[k] = jump[k] with { Soc = jump[k].Soc - 4 };   // one 4-point step at -30
        e = Est(jump, soc: 66);
        Assert.Equal(("rate", (double?)null), (e.Source, e.MeasuredEfficiency));
        Assert.Equal(0.868, Est(TenPerHour()).MeasuredEfficiency);   // the clean one teaches
    }

    [Fact]
    public void Readings_on_mains_are_excluded()
    {
        // charging 90 -> 95 on mains, then off mains at 10 %/h: the rise on mains is not a jump, the mains rows do not count
        var rows = Rows(-60, -31, m => 90 + (m + 60) / 6, mains: true);
        rows.AddRange(Rows(-30, 0, m => 95 - (m + 30) / 6));
        var e = Est(rows, soc: 90);
        Assert.Equal(("rate", 10.0), (e.Source, e.RatePerHour));
    }

    [Fact]
    public void On_mains_now_or_with_no_load_there_is_no_estimate()
    {
        Assert.Equal(RuntimeEstimate.None, RuntimeEstimator.Estimate(Grid(70), TenPerHour(), Now, new StationConfig(), null));
        Assert.Equal(RuntimeEstimate.None, RuntimeEstimator.Estimate(new Reading(70, 0, 0, "INVERTER_OUTPUT"), TenPerHour(), Now, new StationConfig(), null));
        Assert.Equal(RuntimeEstimate.None, RuntimeEstimator.Estimate(null, TenPerHour(), Now, new StationConfig(), null));
    }

    [Fact]
    public void A_soc_rise_during_the_cut_falls_back()
    {
        var rows = TenPerHour();
        rows[40] = rows[40] with { Soc = rows[40].Soc + 2 };
        Assert.Equal("config", Est(rows).Source);
    }

    [Fact]
    public void Empty_or_damaged_history_falls_back()
    {
        Assert.Equal("config", Est([]).Source);
        var rows = TenPerHour();
        rows[52] = rows[52] with { Soc = null };   // a row without SOC ends the run: 8 minutes left
        Assert.Equal("config", Est(rows).Source);
        rows = TenPerHour();
        rows[52] = rows[52] with { Soc = double.NaN };
        Assert.Equal("config", Est(rows).Source);
    }

    [Fact]
    public void A_gap_longer_than_maxGapSec_ends_the_run_and_old_readings_do_not_count()
    {
        var rows = TenPerHour();
        rows.RemoveAll(r => r.At > Now - 50 * 60 && r.At < Now - 5 * 60);   // 45 minutes without readings
        Assert.Equal("config", Est(rows).Source);
        // readings all older than the window: nothing to measure
        Assert.Equal("config", Est(Rows(-180, -61, m => 100 - (m + 180) / 6)).Source);
        // a row ahead of the clock is skipped, not used
        var ahead = TenPerHour();
        ahead.Add(Row(5, 10));
        Assert.Equal(("rate", 10.0), (Est(ahead).Source, Est(ahead).RatePerHour));
    }

    [Fact]
    public void A_rate_implying_more_than_the_cap_is_capped()
    {
        var c = new StationConfig { RateWindowMin = 60 * 24 * 60, MaxGapSec = int.MaxValue };
        var rows = new List<HistoryRow> { Row(-59 * 24 * 60, 100), Row(-59 * 24 * 60 + 1, 99), Row(-1, 97), Row(0, 96) };
        var e = RuntimeEstimator.Estimate(Bat(96), rows, Now, c, null);
        Assert.Equal(("rate", 720.0), (e.Source, e.HoursLeft));
        // the formula is capped too
        Assert.Equal(720, RuntimeEstimator.Estimate(new Reading(100, 0, 0.001, "INVERTER_OUTPUT"), [], Now, new StationConfig(), null).HoursLeft);
    }

    [Theory]
    [InlineData(0.65, "learned")]
    [InlineData(0.3, "config")]       // out of bounds: the configured efficiency
    [InlineData(0.96, "config")]
    [InlineData(double.NaN, "config")]
    public void A_learned_efficiency_replaces_the_configured_one_in_the_fallback(double learned, string source)
    {
        var e = Est([], learned: learned);
        Assert.Equal(source, e.Source);
        double eff = source == "learned" ? learned : 0.85;
        Assert.Equal(Math.Round(2304 * 0.70 * eff / 220, 1), e.HoursLeft);
    }

    [Fact]
    public void The_measured_efficiency_is_bounded()
    {
        // 1 W out of 10 %/h of 2304 Wh: far under 0.4
        var rows = Rows(-60, 0, m => 80 - (m + 60) / 6, load: 1);
        Assert.Equal(RuntimeEstimator.LearnedMin, Est(rows).MeasuredEfficiency);
        rows = Rows(-60, 0, m => 80 - (m + 60) / 6, load: 5000);
        Assert.Equal(0.9, Est(rows).MeasuredEfficiency);   // never over the configured 0.85 + 0.05
        Assert.Equal(0.7, RuntimeEstimator.Estimate(Bat(70), rows, Now, new StationConfig { Efficiency = 0.65 }, null).MeasuredEfficiency);
        Assert.Null(RuntimeEstimator.Sound(0.96, new StationConfig()));
        Assert.Equal(0.9, RuntimeEstimator.Sound(0.95, new StationConfig()));
        Assert.Null(RuntimeEstimator.Estimate(Bat(70), TenPerHour(), Now, new StationConfig { CapacityWh = 0 }, null).MeasuredEfficiency);
    }

    // ---- through the service and /api/now

    private sealed record Rig(StationStore Store, StationService Service, StationApi Api, QueueReader Reader, ManualClock Clock, FakeActions Actions) : IDisposable
    {
        public void Dispose() { Service.Dispose(); Store.Dispose(); }
    }

#pragma warning disable CA2000 // the store and the service belong to the returned rig, which disposes them
    private Rig Open(string name = "a", StationConfig? cfg = null)
    {
        string dir = Path.Combine(_dir, name);
        Directory.CreateDirectory(dir);
        var store = new StationStore(Path.Combine(dir, "station.db"));
        cfg ??= new StationConfig();
        cfg.DataDir = dir;
        var clock = new ManualClock(T0);
        var reader = new QueueReader();
        var actions = new FakeActions();
        var s = new StationService(cfg, reader, actions, new FakeHibernator(true), new FixedFlag(Station), store, clock, _ => { });
        return new Rig(store, s, new StationApi(cfg, s, store, clock, T0), reader, clock, actions);
    }
#pragma warning restore CA2000

    private static async Task<JsonDocument> GetNow(StationApi api) =>
        JsonDocument.Parse((await api.HandleAsync(new HttpRequest("GET", "/api/now", new Dictionary<string, string>(),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Host"] = "127.0.0.1:18940" }, []), CancellationToken.None)).Body);

    private static void Seed(StationStore store, int fromMin, int toMin, Func<int, double> soc)
    {
        for (int m = fromMin; m <= toMin; m++) store.AddReading(T0.AddMinutes(m), new Reading(soc(m), 0, 200, "INVERTER_OUTPUT"), ReadingStatus.Accepted, "");
    }

    [Fact]
    public async Task Now_names_the_source_and_the_rate_and_a_restart_during_a_cut_keeps_measuring()
    {
        using (var rig = Open())
        {
            rig.Reader.Next.Enqueue(Bat(100));
            await rig.Service.PollAsync(CancellationToken.None);
            using var doc = await GetNow(rig.Api);
            var r = doc.RootElement;
            Assert.Equal(("config", JsonValueKind.Null, JsonValueKind.Null, 8.9),
                (r.GetProperty("runtimeSource").GetString(), r.GetProperty("ratePerHour").ValueKind, r.GetProperty("learnedEfficiency").ValueKind, r.GetProperty("hoursLeft").GetDouble()));
        }
        using (var rig = Open("b"))
        {
            Seed(rig.Store, -60, -1, m => 80 - (m + 60) / 6);   // the cut ran an hour before the restart
            rig.Reader.Next.Enqueue(Bat(70));
            await rig.Service.PollAsync(CancellationToken.None);
            using var doc = await GetNow(rig.Api);
            var r = doc.RootElement;
            // 10 %/h at a mean 200.4 W (the seeded rows and the polled one at 220 W), the current 220 W: 70 / 10 x 200.4 / 220
            Assert.Equal(("rate", 10.0, 6.4), (r.GetProperty("runtimeSource").GetString(), r.GetProperty("ratePerHour").GetDouble(), r.GetProperty("hoursLeft").GetDouble()));
            // the first clean window is taken whole: 200.4 W out of 10 % of 2304 Wh an hour
            Assert.Equal(0.87, r.GetProperty("learnedEfficiency").GetDouble());
            Assert.Equal($"0.87;{T0.ToUnixTimeSeconds()}", rig.Store.GetMeta(StationApi.LearnedKey));
        }
        // the next cut starts on the learned efficiency, after a restart too
        using (var rig = Open("b"))
        {
            rig.Clock.Now = T0.AddHours(3);
            rig.Reader.Next.Enqueue(Bat(70));
            await rig.Service.PollAsync(CancellationToken.None);
            using var doc = await GetNow(rig.Api);
            var r = doc.RootElement;
            Assert.Equal(("learned", Math.Round(2304 * 0.70 * 0.87 / 220, 1)), (r.GetProperty("runtimeSource").GetString(), r.GetProperty("hoursLeft").GetDouble()));
        }
    }

    [Fact]
    public async Task A_clean_window_moves_the_learned_value_a_quarter_of_the_way_once_per_window()
    {
        using var rig = Open();
        rig.Store.SetMeta(StationApi.LearnedKey, $"0.71;{T0.AddHours(-2).ToUnixTimeSeconds()}");
        Seed(rig.Store, -60, -1, m => 80 - (m + 60) / 6);
        rig.Reader.Next.Enqueue(Bat(70));
        await rig.Service.PollAsync(CancellationToken.None);
        using (var doc = await GetNow(rig.Api)) Assert.Equal(0.75, doc.RootElement.GetProperty("learnedEfficiency").GetDouble());   // 0.71 + 0.25 x (0.87 - 0.71)
        rig.Clock.Now = T0.AddMinutes(5);
        rig.Reader.Next.Enqueue(Bat(70));
        await rig.Service.PollAsync(CancellationToken.None);
        using (var doc = await GetNow(rig.Api)) Assert.Equal(0.75, doc.RootElement.GetProperty("learnedEfficiency").GetDouble());   // the same window: no second step
        Assert.StartsWith("0.75;", rig.Store.GetMeta(StationApi.LearnedKey), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failing_store_never_breaks_api_now_and_is_logged_once()
    {
        using var rig = Open();
        var logs = new List<string>();
        var api = new StationApi(new StationConfig { DataDir = Path.Combine(_dir, "a") }, rig.Service, rig.Store, rig.Clock, T0, null, logs.Add);
        rig.Reader.Next.Enqueue(Bat(70));
        await rig.Service.PollAsync(CancellationToken.None);
        rig.Store.Dispose();
        for (int i = 0; i < 2; i++)
            using (var doc = await GetNow(api)) Assert.Equal("config", doc.RootElement.GetProperty("runtimeSource").GetString());
        Assert.Equal(2, logs.Count);   // the history read and the meta read, once each
    }

    [Fact]
    public async Task A_damaged_learned_value_is_ignored()
    {
        using var rig = Open();
        rig.Store.SetMeta(StationApi.LearnedKey, "not a number");
        rig.Reader.Next.Enqueue(Bat(100));
        await rig.Service.PollAsync(CancellationToken.None);
        using var doc = await GetNow(rig.Api);
        Assert.Equal("config", doc.RootElement.GetProperty("runtimeSource").GetString());
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("learnedEfficiency").ValueKind);
    }

    [Fact]
    public async Task On_mains_now_has_no_source()
    {
        using var rig = Open();
        rig.Reader.Next.Enqueue(Grid(100));
        await rig.Service.PollAsync(CancellationToken.None);
        using var doc = await GetNow(rig.Api);
        Assert.Equal((JsonValueKind.Null, JsonValueKind.Null), (doc.RootElement.GetProperty("runtimeSource").ValueKind, doc.RootElement.GetProperty("hoursLeft").ValueKind));
    }

    // Levels and hibernate act on SOC thresholds only: a history implying minutes left changes nothing.
    [Fact]
    public async Task The_levels_and_hibernate_ignore_the_estimate()
    {
        async Task<(Level, string, int)> Run(bool fastHistory)
        {
            string sub = Path.Combine(_dir, fastHistory ? "fast" : "none");
            Directory.CreateDirectory(sub);
            var store = new StationStore(Path.Combine(sub, "station.db"));
            var cfg = new StationConfig { DataDir = sub };
            var clock = new ManualClock(T0);
            var reader = new QueueReader();
            var actions = new FakeActions();
            var hibernator = new FakeHibernator(true);
            using var s = new StationService(cfg, reader, actions, hibernator, new FixedFlag(Station), store, clock, _ => { });
            var api = new StationApi(cfg, s, store, clock, T0);
            if (fastHistory) Seed(store, -40, -1, m => 60 - (m + 40) * 1.0);   // 60 % down to 21 % in 40 minutes: minutes left
            // down through 20 %, 10 %, 7 % and 5 %: emergency, stop, warning, hibernate
            foreach (double soc in new[] { 19.0, 19, 19, 15, 12, 9, 9, 9, 6, 6, 6, 4, 4, 4, 4, 4, 4, 4, 4 })
            {
                reader.Next.Clear();
                reader.Next.Enqueue(Bat(soc));
                reader.Next.Enqueue(Bat(soc));   // the fresh reading hibernate takes
                await s.PollAsync(CancellationToken.None);
                (await GetNow(api)).Dispose();
                clock.Now = clock.Now.AddMinutes(2);
            }
            var result = (s.State.Level, string.Join(",", actions.Calls), hibernator.Calls);
            store.Dispose();
            return result;
        }
        var none = await Run(false);
        var fast = await Run(true);
        Assert.Equal(none, fast);
        Assert.Equal(Level.Hibernating, fast.Item1);
        Assert.True(fast.Item3 > 0, "the PC hibernated, in both");
    }

    [Fact]
    public void The_service_and_the_decision_never_refer_to_the_estimator()
    {
        const string ns = "BluettiWatcher.Runtime";
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var types = typeof(StationDecision).Assembly.GetTypes().Where(t => t.Namespace == "BluettiWatcher.Decision" || t == typeof(StationService) || t.DeclaringType == typeof(StationService)).ToList();
        Assert.Contains(typeof(StationService), types);
        bool Foreign(Type? t) => t is not null && (t.Namespace == ns || t.GenericTypeArguments.Any(Foreign) || (t.HasElementType && Foreign(t.GetElementType())));
        foreach (var t in types)
        {
            Assert.DoesNotContain(t.GetFields(all), f => Foreign(f.FieldType));
            foreach (var m in t.GetMethods(all))
            {
                Assert.False(Foreign(m.ReturnType), $"{t.Name}.{m.Name}");
                Assert.DoesNotContain(m.GetParameters(), p => Foreign(p.ParameterType));
                Assert.DoesNotContain(m.GetMethodBody()?.LocalVariables ?? [], v => Foreign(v.LocalType));
            }
        }
    }

    /// <summary>Every method a type's code calls, creates or takes the address of, its nested types (lambdas, async state machines) included.</summary>
    private static List<MethodBase> Callees(Type type)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var found = new List<MethodBase>();
        foreach (var t in new[] { type }.Concat(type.GetNestedTypes(all).SelectMany(n => new[] { n }.Concat(n.GetNestedTypes(all)))))
        {
            foreach (var m in t.GetMethods(all).Cast<MethodBase>().Concat(t.GetConstructors(all)))
            {
                if (m.GetMethodBody()?.GetILAsByteArray() is not { } il) continue;
                for (int i = 0; i + 4 < il.Length; i++)
                {
                    // call, callvirt, newobj; ldftn / ldvirtftn (0xFE 0x06 / 0x07): a method token follows
                    bool op = il[i] is 0x28 or 0x6F or 0x73 || (il[i] == 0xFE && i + 5 < il.Length && il[i + 1] is 0x06 or 0x07);
                    if (!op) continue;
                    int at = il[i] == 0xFE ? i + 2 : i + 1;
                    int token = BitConverter.ToInt32(il, at);
                    if ((token >> 24) is not (0x06 or 0x0A or 0x2B)) continue;
                    try { if (t.Module.ResolveMethod(token, t.IsGenericType ? t.GetGenericArguments() : null, m.IsGenericMethod ? m.GetGenericArguments() : null) is { } callee) found.Add(callee); }
                    catch (Exception e) when (e is ArgumentException or BadImageFormatException) { /* a byte that only looked like a call */ _ = e; }
                }
            }
        }
        return found;
    }

    private static bool IsEstimate(MethodBase m) =>
        m.DeclaringType?.Namespace == "BluettiWatcher.Runtime"
        || (m.DeclaringType == typeof(StationApi) && m.Name is nameof(StationApi.HoursLeft) or nameof(StationApi.HoursToHibernate) or nameof(StationApi.Runtime));

    [Fact]
    public void No_code_of_the_decision_or_the_service_calls_the_estimate()
    {
        // the scan sees calls: the API's /api/now reaches the estimator
        Assert.Contains(Callees(typeof(StationApi)), IsEstimate);
        var types = typeof(StationDecision).Assembly.GetTypes().Where(t => t.Namespace == "BluettiWatcher.Decision" && !t.IsNested).Append(typeof(StationService)).ToList();
        Assert.Contains(typeof(StationDecision), types);
        foreach (var t in types)
            Assert.DoesNotContain(Callees(t), IsEstimate);
    }
}
