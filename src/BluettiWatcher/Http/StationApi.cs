using System.Globalization;
using System.Reflection;
using System.Text.Json;
using BluettiWatcher.Decision;
using BluettiWatcher.DtekWatcher;
using BluettiWatcher.Io;
using BluettiWatcher.Runtime;
using BluettiWatcher.Storage;

namespace BluettiWatcher.Http;

/// <summary>
/// The routes. GET /, /api/now, /api/history?from&amp;to&amp;step, /api/events?from&amp;to, /api/health;
/// POST /api/mode { "mode": "auto" | "eco" | "max" } behind <see cref="RequestGuard"/>.
/// from / to: ISO 8601 or Unix milliseconds; step: seconds (default 60).
/// </summary>
internal sealed class StationApi(StationConfig config, StationService service, StationStore store, TimeProvider time, DateTimeOffset startedAt, IDtekWatcher? dtek = null, Action<string>? log = null)
{
    private static readonly Lazy<byte[]> Page = new(() =>
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("station.html")
            ?? throw new InvalidOperationException("station.html is not embedded");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    });

    /// <summary>A time people read, in the PC's local zone (the time provider's).</summary>
    private string Clock(DateTimeOffset at) => FlagFileReader.Clock(at, time.LocalTimeZone);

    public async Task<HttpResponse> HandleAsync(HttpRequest r, CancellationToken ct)
    {
        if (!RequestGuard.HostOk(r, config.Port)) return HttpResponse.Text(403, "forbidden: foreign Host");
        if (r.Path == "/api/mode")
        {
            if (r.Method != "POST") return HttpResponse.Text(405, "POST only");
            if (RequestGuard.WriteRefusal(r, config.Port) is { } why) return HttpResponse.Text(403, "forbidden: " + why);
            return await ModeAsync(r, ct);
        }
        if (r.Method != "GET") return HttpResponse.Text(405, "GET only");
        var now = time.GetLocalNow();
        return r.Path switch
        {
            "/" or "/index.html" => new HttpResponse(200, "text/html; charset=utf-8", Page.Value),
            "/api/now" => HttpResponse.Json(200, Now(now)),
            "/api/health" => HttpResponse.Json(200, Health(now)),
            "/api/history" => History(r, now),
            "/api/events" => Events(r, now),
            _ => HttpResponse.Text(404, "not found"),
        };
    }

    /// <summary>ISO 8601 or Unix milliseconds; null when absent; throws FormatException when unreadable.</summary>
    internal static DateTimeOffset? ParseTime(IReadOnlyDictionary<string, string> q, string key)
    {
        if (!q.TryGetValue(key, out var v) || v.Length == 0) return null;
        if (long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out long ms)) return DateTimeOffset.FromUnixTimeMilliseconds(ms);
        if (DateTimeOffset.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)) return at;
        throw new FormatException($"{key} is neither ISO 8601 nor Unix milliseconds");
    }

    private HttpResponse History(HttpRequest r, DateTimeOffset now)
    {
        DateTimeOffset from, to; long step;
        try
        {
            to = ParseTime(r.Query, "to") ?? now;
            from = ParseTime(r.Query, "from") ?? to.AddHours(-26);
            step = r.Query.TryGetValue("step", out var s) ? long.Parse(s, CultureInfo.InvariantCulture) : 60;
        }
        catch (Exception e) when (e is FormatException or OverflowException or ArgumentOutOfRangeException) { return HttpResponse.Text(400, e.Message); }
        if (step < 1 || from >= to) return HttpResponse.Text(400, "need from < to and step >= 1");
        // Step 60 or less: each stored row (raw minutes and older 5-minute aggregates) as it is.
        var rows = store.History(from.ToUnixTimeSeconds(), to.ToUnixTimeSeconds() + 1, step <= 60 ? 1 : step);
        return HttpResponse.Json(200, Write(w =>
        {
            w.WriteStartObject();
            w.WriteNumber("step", step);
            w.WriteStartArray("rows");
            foreach (var row in rows) WriteRow(w, row);
            w.WriteEndArray();
            w.WriteEndObject();
        }));
    }

    private HttpResponse Events(HttpRequest r, DateTimeOffset now)
    {
        DateTimeOffset from, to;
        try
        {
            to = ParseTime(r.Query, "to") ?? now;
            from = ParseTime(r.Query, "from") ?? to.AddHours(-26);
        }
        catch (Exception e) when (e is FormatException or ArgumentOutOfRangeException) { return HttpResponse.Text(400, e.Message); }
        var rows = store.Events(from.ToUnixTimeSeconds(), to.ToUnixTimeSeconds() + 1);
        return HttpResponse.Json(200, Write(w =>
        {
            w.WriteStartObject();
            w.WriteStartArray("events");
            foreach (var e in rows)
            {
                w.WriteStartObject();
                w.WriteString("at", Iso(DateTimeOffset.FromUnixTimeSeconds(e.At)));
                w.WriteString("kind", e.Kind);
                w.WriteString("detail", e.Detail);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }));
    }

    private async Task<HttpResponse> ModeAsync(HttpRequest r, CancellationToken ct)
    {
        ManualMode mode;
        try
        {
            using var doc = JsonDocument.Parse(r.Body);
            string? m = doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("mode", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            switch (m)
            {
                case "auto": mode = ManualMode.Auto; break;
                case "eco": mode = ManualMode.Eco; break;
                case "max": mode = ManualMode.Max; break;
                default: return HttpResponse.Text(400, "mode must be auto, eco or max");
            }
        }
        catch (JsonException) { return HttpResponse.Text(400, "body must be JSON { \"mode\": \"auto\" | \"eco\" | \"max\" }"); }
        var res = await service.SetModeAsync(mode, ct);
        return HttpResponse.Json(res.Status, Write(w =>
        {
            w.WriteStartObject();
            w.WriteBoolean("ok", res.Ok);
            w.WriteBoolean("accepted", res.Status == 202);
            w.WriteString("manual", res.Manual.ToString().ToLowerInvariant());
            w.WriteString("message", res.Message);
            w.WriteString("code", res.Message == StationService.MaxHeldBack ? PageCodes.MaxHeld : "");
            w.WriteEndObject();
        }));
    }

    private static string Iso(DateTimeOffset at) => at.ToLocalTime().ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

    private static byte[] Write(Action<Utf8JsonWriter> body)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms)) body(w);
        return ms.ToArray();
    }

    private static void Num(Utf8JsonWriter w, string name, double? v, int digits = 1)
    {
        if (v is { } x) w.WriteNumber(name, Math.Round(x, digits));
        else w.WriteNull(name);
    }

    private static void WriteRow(Utf8JsonWriter w, HistoryRow row)
    {
        w.WriteStartObject();
        w.WriteString("at", Iso(DateTimeOffset.FromUnixTimeSeconds(row.At)));
        Num(w, "soc", row.Soc);
        Num(w, "inAc", row.InAc);
        Num(w, "inDc", row.InDc);
        Num(w, "outAc", row.OutAc);
        Num(w, "outDc", row.OutDc);
        Num(w, "volt", row.Volt);
        Num(w, "hz", row.Hz, 2);
        w.WriteBoolean("mains", row.Mains >= 0.5);
        w.WriteEndObject();
    }

    /// <summary>Hours of battery at the current load by the efficiency formula alone (capacity x efficiency); null on mains or with no load.</summary>
    internal static double? HoursLeft(Reading? r, StationConfig c) => RuntimeEstimator.Estimate(r, [], 0, c, null).HoursLeft;

    /// <summary>Hours down to <c>hibernateAt</c>, when the PC hibernates, by the efficiency formula alone; null on mains or with no load.</summary>
    internal static double? HoursToHibernate(Reading? r, StationConfig c) => RuntimeEstimator.Estimate(r, [], 0, c, null).HoursToHibernate;

    /// <summary>The meta key of the learned efficiency, "value;unix seconds of the last update" (design.md §7 Battery time).</summary>
    internal const string LearnedKey = "runtimeEfficiency";
    /// <summary>The weight a new clean window gets against the learned value; at most one update per rate window.</summary>
    internal const double LearnWeight = 0.25;
    private readonly Lock _learnGate = new();
    private double? _learned;
    private long _learnedAt;
    private bool _learnedRead;
    private readonly HashSet<string> _logged = [];

    /// <summary>A failure on the store while answering a GET: logged once per kind, the answer goes on without it.</summary>
    private void Once(string kind, Exception e)
    {
        lock (_logged) if (!_logged.Add(kind)) return;
        log?.Invoke($"battery time: {kind} failed - {e.GetType().Name}: {e.Message}");
    }

    /// <summary>The learned efficiency as stored; a missing, unreadable or out-of-bounds value is none. A failed read is tried again next time.</summary>
    private double? Learned()
    {
        lock (_learnGate)
        {
            if (_learnedRead) return _learned;
            string? raw;
            try { raw = store.GetMeta(LearnedKey); }
            catch (Exception e) when (e is System.Data.Common.DbException or InvalidOperationException) { Once("meta read", e); return null; }
            _learnedRead = true;
            var parts = (raw ?? "").Split(';');
            _learned = double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? RuntimeEstimator.Sound(v, config) : null;
            _learnedAt = _learned is not null && parts.Length > 1 && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long at) ? at : 0;
            return _learned;
        }
    }

    /// <summary>
    /// The battery time for the current reading: the measured rate over the stored readings, else the formula. A clean
    /// window moves the learned efficiency by <see cref="LearnWeight"/>, at most once per rate window.
    /// </summary>
    internal RuntimeEstimate Runtime(Reading? current, DateTimeOffset now)
    {
        if (current is null || current.InAc > 0) return RuntimeEstimate.None;
        long nowSec = now.ToUnixTimeSeconds();
        IReadOnlyList<HistoryRow> rows = [];
        try { if (config.RateWindowMin > 0) rows = store.History(nowSec - config.RateWindowMin * 60L, nowSec + 1, 1); }
        catch (Exception e) when (e is System.Data.Common.DbException or InvalidOperationException) { Once("history read", e); }
        double? learned = Learned();
        var est = RuntimeEstimator.Estimate(current, rows, nowSec, config, learned);
        if (est.MeasuredEfficiency is { } m)
        {
            lock (_learnGate)
            {
                if (_learned is null || nowSec - _learnedAt >= config.RateWindowMin * 60L || nowSec < _learnedAt)
                {
                    double next = Math.Round(RuntimeEstimator.Bound(RuntimeEstimator.Smooth(_learned, m, LearnWeight), config), 3);
                    try
                    {
                        store.SetMeta(LearnedKey, next.ToString("0.###", CultureInfo.InvariantCulture) + ";" + nowSec.ToString(CultureInfo.InvariantCulture));
                        (_learned, _learnedAt, _learnedRead) = (next, nowSec, true);
                    }
                    catch (Exception e) when (e is System.Data.Common.DbException or InvalidOperationException) { Once("meta write", e); }
                }
            }
        }
        return est;
    }

    /// <summary>
    /// The library's output mode for the page: BYPASS_OUTPUT_D -> "bypass output (D)". The C / D suffix is the library's
    /// own enum name (bluetti_bt_lib enums/output_mode.py), a variant letter, not a cut word.
    /// </summary>
    internal static string ModeLabel(string? mode)
    {
        if (string.IsNullOrEmpty(mode)) return "";
        var words = mode.Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 1 && words[^1].Length == 1) return string.Join(' ', words[..^1]).ToLowerInvariant() + $" ({words[^1]})";
        return string.Join(' ', words).ToLowerInvariant();
    }

    private byte[] Now(DateTimeOffset now)
    {
        var s = service.State;
        var last = service.LastAccepted;
        var lastAt = service.LastAcceptedAt;
        var flag = service.LastFlag;
        var notes = new List<PageNote>();
        if (s.Silent && s.SilentSince is { } ss) notes.Add(new(PageCodes.StationSilent, $"station silent since {Clock(ss)}", Clock(ss)));
        if (s.HibernateDelayedUntil is { } hd && s.Cut)
        {
            string until = Clock(hd), moved = s.RebasedAt is { } rb ? Clock(rb) : "?", level = config.HibernateAt.ToString("0.##", CultureInfo.InvariantCulture);
            notes.Insert(0, new(PageCodes.HibernateHeld, $"hibernate held back until {until}: the baseline moved at {moved}, the battery must stay at or under {level} %", until, moved, level));
        }
        if (s.Manual == ManualMode.Max && service.EmergencyHolds(flag)) notes.Insert(0, new(PageCodes.MaxHeld, StationService.MaxHeldBack));
        if (s.Rejected.Length > 0 && s.RejectedAt is { } ra && (now - ra).TotalHours < 6)
            notes.Add(new(PageCodes.ReadingRejected, $"reading rejected at {Clock(ra)}: {s.Rejected}", Clock(ra), s.Rejected));
        var wake = service.Wake;
        if (wake is { Error.Length: > 0 }) notes.Add(new(PageCodes.WakeDisarmFailed, $"the wake timer could not be turned off ({wake.Error}): retrying", wake.Error));
        var runtime = Runtime(last, now);
        var schedule = dtek?.View(s.Cut, runtime.HoursLeft, runtime.HoursToHibernate) ?? DtekView.Off();
        return Write(w =>
        {
            w.WriteStartObject();
            w.WriteString("type", config.Type);
            w.WriteNumber("capacityWh", config.CapacityWh);
            w.WriteNumber("efficiency", config.Efficiency);
            w.WriteString("mode", ModeLabel(last?.Mode));
            w.WriteString("eco", FlagFileReader.EcoText(flag, now, time.LocalTimeZone));
            Note(w, "ecoNote", FlagFileReader.EcoNote(flag, now, time.LocalTimeZone));
            w.WriteString("modeCode", last?.Mode ?? "");
            w.WriteString("manual", s.Manual.ToString().ToLowerInvariant());
            if (s.ModePending is { } pending) w.WriteString("modePending", pending.ToString().ToLowerInvariant()); else w.WriteNull("modePending");
            w.WriteString("modeError", service.ModeError);
            Note(w, "modeErrorNote", ModeErrorNote(service.ModeError));
            w.WriteString("level", s.Level.ToString().ToLowerInvariant());
            w.WriteBoolean("cut", s.Cut);
            if (s.HibernateDelayedUntil is { } until && s.Cut) w.WriteString("hibernateDelayedUntil", Iso(until)); else w.WriteNull("hibernateDelayedUntil");
            if (StationDecision.EcoAt(s, flag?.View ?? FlagView.Unreadable, now, config) is { } ecoAt) w.WriteString("ecoAt", Iso(ecoAt)); else w.WriteNull("ecoAt");
            w.WriteString("grid", s.Grid.ToString().ToLowerInvariant());
            w.WriteBoolean("silent", s.Silent);
            w.WriteString("note", string.Join(" · ", notes.Select(n => n.Text)));
            Notes(w, "notes", notes);
            w.WriteBoolean("dryRun", config.DryRun);
            Num(w, "hoursLeft", runtime.HoursLeft);
            if (runtime.Source is { } src) w.WriteString("runtimeSource", src); else w.WriteNull("runtimeSource");
            Num(w, "ratePerHour", runtime.RatePerHour);
            Num(w, "learnedEfficiency", Learned(), 3);
            w.WriteStartObject("thresholds");
            w.WriteNumber("emergencyAt", config.EmergencyAt);
            w.WriteNumber("stopAt", config.StopAt);
            w.WriteNumber("warnAt", config.WarnAt);
            w.WriteNumber("hibernateAt", config.HibernateAt);
            w.WriteEndObject();
            if (last is not null && lastAt is { } at)
            {
                w.WriteStartObject("last");
                w.WriteString("at", Iso(at));
                Num(w, "soc", last.Soc);
                Num(w, "inAc", last.InAc);
                Num(w, "inDc", last.InDc);
                Num(w, "outAc", last.OutAc);
                Num(w, "outDc", last.OutDc);
                Num(w, "volt", last.Volt);
                Num(w, "hz", last.Hz, 2);
                w.WriteBoolean("mains", last.InAc > 0);
                w.WriteEndObject();
            }
            else w.WriteNull("last");
            if (wake is not null)
            {
                w.WriteStartObject("wake");
                w.WriteNumber("everyMinutes", wake.EveryMinutes);
                w.WriteNumber("floorPercent", wake.FloorPercent);
                w.WriteBoolean("armed", wake.Armed);
                Time(w, "wakeAt", wake.WakeAt);
                w.WriteBoolean("disarmOwed", wake.Owed);
                w.WriteString("error", wake.Error);
                w.WriteEndObject();
            }
            else w.WriteNull("wake");
            w.WriteStartObject("dtekWatcher");
            WriteDtek(w, schedule);
            w.WriteEndObject();
            w.WriteEndObject();
        });
    }

    private static void Note(Utf8JsonWriter w, string name, PageNote? n)
    {
        if (n is null) { w.WriteNull(name); return; }
        w.WriteStartObject(name);
        NoteBody(w, n);
        w.WriteEndObject();
    }

    private static void NoteBody(Utf8JsonWriter w, PageNote n)
    {
        w.WriteString("code", n.Code);
        w.WriteString("text", n.Text);
        w.WriteStartArray("args");
        foreach (var a in n.Args) w.WriteStringValue(a);
        w.WriteEndArray();
    }

    private static void Notes(Utf8JsonWriter w, string name, IEnumerable<PageNote> notes)
    {
        w.WriteStartArray(name);
        foreach (var n in notes) { w.WriteStartObject(); NoteBody(w, n); w.WriteEndObject(); }
        w.WriteEndArray();
    }

    /// <summary>The saved mode refusal or failure as a coded note; "" = none. The text is the service's; the code is read from its known shapes.</summary>
    internal static PageNote? ModeErrorNote(string error)
    {
        if (error.Length == 0) return null;
        if (error == StationService.MaxHeldBack) return new(PageCodes.MaxHeld, error);
        if (error == StationService.EcoHeldBack) return new(PageCodes.EcoHeld, error);
        if (error == StationService.AutoLiftHeldBack) return new(PageCodes.AutoHeld, error);
        int colon = error.IndexOf(": ", StringComparison.Ordinal);
        string head = colon > 0 ? error[..colon] : "";
        string rest = colon > 0 ? error[(colon + 2)..] : "";
        if (head.EndsWith(" dropped", StringComparison.Ordinal) && Enum.TryParse<ManualMode>(head[..^" dropped".Length], out var dropped))
        {
            string mode = dropped.ToString();
            return rest switch
            {
                "mains came back before it ran" => new(PageCodes.ModeDroppedBack, error, mode),
                "mains was lost before it ran" => new(PageCodes.ModeDroppedLost, error, mode),
                _ => new(PageCodes.ModeDropped, error, mode, rest),
            };
        }
        if (Enum.TryParse<ManualMode>(head, out var failed) && head == failed.ToString())
            return rest == StationService.FlagUnreadable ? new(PageCodes.FlagUnreadable, error, head) : new(PageCodes.ModeFailed, error, head, rest);
        return new(PageCodes.ModeOther, error, error);
    }

    private static void Time(Utf8JsonWriter w, string name, DateTimeOffset? at)
    {
        if (at is { } t) w.WriteString(name, Iso(t)); else w.WriteNull(name);
    }

    /// <summary>The DTEK Watcher module's object in /api/now (design.md §11 rule 14).</summary>
    private static void WriteDtek(Utf8JsonWriter w, DtekView v)
    {
        w.WriteString("state", v.State);
        w.WriteString("note", v.Note);
        Notes(w, "notes", v.Notes);
        w.WriteString("source", v.Source);
        w.WriteString("update", v.Update);
        Time(w, "collectedAt", v.CollectedAt);
        w.WriteString("datasetState", v.DatasetState);
        w.WriteString("siteState", v.SiteState);
        w.WriteStartArray("queues");
        foreach (var q in v.Queues) w.WriteStringValue(q);
        w.WriteEndArray();
        w.WriteString("queuesSource", v.QueuesSource);
        w.WriteString("queue", v.Queue);
        w.WriteString("queueSource", v.QueueSource);
        w.WriteString("queueOrigin", v.QueueOrigin);
        w.WriteNumber("queueContradictions", v.QueueContradictions);
        Strings(w, "queueSet", v.QueueSet);
        Time(w, "nextStart", v.NextStart);
        Time(w, "nextEnd", v.NextEnd);
        w.WriteString("nextQueue", v.NextQueue);
        Strings(w, "nextQueues", v.NextQueues);
        Time(w, "return", v.Return);
        w.WriteString("outageKind", v.OutageKind);
        w.WriteString("outageStart", v.OutageStart);
        w.WriteString("outageEnd", v.OutageEnd);
        Time(w, "outageEndAt", v.OutageEndAt);
        w.WriteString("batteryLasts", v.BatteryLasts);
        w.WriteBoolean("overdue", v.Overdue);
        if (v.OverdueMin is { } late) w.WriteNumber("overdueMin", late); else w.WriteNull("overdueMin");
        Time(w, "plannedReturn", v.PlannedReturn);
        Time(w, "batteryUntil", v.BatteryUntil);
        w.WriteNumber("planChanges", v.PlanChanges);
        if (v.LateBackMin is { } usual) w.WriteNumber("lateBackMin", usual); else w.WriteNull("lateBackMin");
        w.WriteNumber("lateCuts", v.LateCuts);
        w.WriteString("today", v.Today);
        w.WriteString("tomorrow", v.Tomorrow);
        w.WriteStartArray("windows");
        foreach (var d in v.Windows)
        {
            w.WriteStartObject();
            w.WriteString("queue", d.Queue);
            w.WriteString("today", d.Today);
            w.WriteString("tomorrow", d.Tomorrow);
            w.WriteStartArray("intervals");
            foreach (var i in d.Intervals)
            {
                w.WriteStartObject();
                Time(w, "start", i.Start);
                Time(w, "end", i.End);
                Strings(w, "queues", i.Queues);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    private static void Strings(Utf8JsonWriter w, string name, IEnumerable<string> values)
    {
        w.WriteStartArray(name);
        foreach (var s in values) w.WriteStringValue(s);
        w.WriteEndArray();
    }
    private byte[] Health(DateTimeOffset now)
    {
        var s = service.State;
        var (raw, agg) = store.Counts();
        return Write(w =>
        {
            w.WriteStartObject();
            string? problem = config.ConfigProblem;
            w.WriteBoolean("ok", problem is null);
            if (problem is not null) w.WriteString("problem", problem); else w.WriteNull("problem");
            w.WriteString("version", typeof(StationApi).Assembly.GetName().Version?.ToString() ?? "");
            w.WriteNumber("uptimeSec", (long)(now - startedAt).TotalSeconds);
            w.WriteBoolean("dryRun", config.DryRun);
            if (service.LastPollAt is { } p) w.WriteString("lastPollAt", Iso(p)); else w.WriteNull("lastPollAt");
            if (service.LastAcceptedAt is { } a) w.WriteString("lastAcceptedAt", Iso(a)); else w.WriteNull("lastAcceptedAt");
            if (service.LastAccepted?.Source is { } src) w.WriteString("lastSource", src); else w.WriteNull("lastSource");
            w.WriteNumber("misses", s.Misses);
            w.WriteBoolean("silent", s.Silent);
            w.WriteNumber("rawRows", raw);
            w.WriteNumber("aggregateRows", agg);
            w.WriteEndObject();
        });
    }
}
