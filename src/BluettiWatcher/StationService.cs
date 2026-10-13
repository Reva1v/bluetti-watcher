using System.Globalization;
using BluettiWatcher.Decision;
using BluettiWatcher.Io;
using BluettiWatcher.Storage;

namespace BluettiWatcher;

internal sealed record ModeChange(bool Ok, int Status, string Message, ManualMode Manual);

/// <summary>
/// One poll: a reading, a stored row, the decision, its actions. The state is saved before anything is switched; an
/// action's success is added to it afterwards. Every failing command backs off (<see cref="Backoff"/>) and each failure is
/// an event the page shows. The config's <c>actions</c> run on entering a state (<see cref="StateActions"/>), never awaited.
/// </summary>
internal sealed class StationService(StationConfig config, IStationReader reader, IPowerModeActions actions, IHibernator hibernator,
    IFlagReader flagReader, StationStore store, TimeProvider time, Action<string> log, WakeKeeper? wake = null, StateActions? onState = null) : IDisposable
{
    /// <summary>A time people read, in the PC's local zone (the time provider's).</summary>
    private string Clock(DateTimeOffset at) => FlagFileReader.Clock(at, time.LocalTimeZone);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Backoff _backoff = new();
    private readonly SemaphoreSlim _wake = new(0, int.MaxValue);
    private StationState _state = store.LoadState();
    private BatteryWritten? _battery;
    private Reading? _last;
    private DateTimeOffset? _lastAt;
    private DateTimeOffset? _lastPollAt;
    private PowerModeFlag? _flag;

    public void Dispose()
    {
        _gate.Dispose();
        _wake.Dispose();
    }

    public StationState State => _state.Clone();
    public Reading? LastAccepted => _last;
    public DateTimeOffset? LastAcceptedAt => _lastAt;
    public DateTimeOffset? LastPollAt => _lastPollAt;
    public PowerModeFlag? LastFlag => _flag;
    /// <summary>The wake of a hibernated PC (spec 4.1); null when the service runs without one.</summary>
    public WakeStatus? Wake => wake?.Status;

    private string KillSwitch => Path.Combine(config.DataDir, "no-hibernate");

    private static string Fmt(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Minutes(int sec) => $"{Fmt(sec / 60.0)} minutes";

    private static string From(Reading? r) => r?.Source switch { null => "", "cloud" => " from the cloud", var s => $" from {s}" };

    /// <summary>A rebase is remembered in this cut and its confirmation is not over: hibernate waits for it.</summary>
    private bool RebaseMayDelay(StationState s) =>
        s.RebasedAt is not null && s.RebaseConfirmedSec < config.RebaseHibernateConfirmSec;

    /// <summary>Runs <paramref name="run"/> unless its backoff is closed; null = not run (backing off).</summary>
    private async Task<ActionResult?> Gated(string key, DateTimeOffset now, Func<Task<ActionResult>> run)
    {
        if (!_backoff.Open(key, now)) return null;
        var r = await run();
        if (r.Ok) _backoff.Succeeded(key);
        else if (!r.DryRun) Failed(key, now, $"{r.CommandLine} -> exit {r.ExitCode}: {r.LastLine}");
        return r;
    }

    private void Failed(string key, DateTimeOffset now, string detail)
    {
        var next = _backoff.Failed(key, now);
        string text = $"{key} failed: {detail} - next try {Clock(next)}";
        store.AddEvent(now, "action-failed", text);
        log(text);
    }

    private void Complete(StationAction a, DateTimeOffset now)
    {
        _state = StationDecision.Complete(_state, a, now);
        store.SaveState(_state);
    }

    /// <summary>One poll. Returns the delay in seconds before the next one.</summary>
    public async Task<int> PollAsync(CancellationToken ct)
    {
        var reading = await reader.ReadAsync(ct);
        var now = time.GetLocalNow();   // the reading's time: when it arrived, before any lock
        await _gate.WaitAsync(ct);
        try
        {
            _lastPollAt = now;
            _flag = flagReader.Read(now);
            var d = StationDecision.Decide(reading, _state, _flag?.View ?? FlagView.Unreadable, now, config);
            var status = reading is null ? ReadingStatus.Missed : d.Rejected.Length > 0 ? ReadingStatus.Rejected : ReadingStatus.Accepted;
            store.AddReading(now, reading, status, d.Rejected);
            string? sourceBefore = _last?.Source;
            if (status == ReadingStatus.Accepted) { _last = reading; _lastAt = now; }
            bool cutBefore = _state.Cut || _state.CutBeforeGap;
            _state = d.State;
            store.SaveState(_state);
            // Mains back confirmed: the cut is over, so the next cut's entries start their commands again.
            if (cutBefore && !_state.Cut && !_state.CutBeforeGap) onState?.CutEnded();

            // Spec 3.1 AC6: every cloud reading is logged with its source, and the first Bluetooth one after it.
            if (status == ReadingStatus.Rejected) log($"reading rejected{From(reading)}: {d.Rejected} (miss {_state.Misses})");
            else if (status == ReadingStatus.Accepted && (reading!.Source == "cloud" || sourceBefore == "cloud"))
                log($"reading accepted{From(reading)}: battery {Fmt(reading.Soc!.Value)} %");
            else if (status == ReadingStatus.Missed) log($"no answer from the station (miss {_state.Misses}){(_state.Silent ? " - station silent" : "")}");

            foreach (var a in d.Actions)
            {
                switch (a)
                {
                    case StationAction.EcoOn:
                        var eco = await Gated("eco-on", now, () => actions.StationEcoAsync(ct));
                        if (eco is { Ok: true })
                        {
                            store.AddEvent(now, "eco-on", eco.CommandLine);
                            onState?.Enter(StateActions.Eco);
                        }
                        break;
                    case StationAction.EmergencyOn:
                        var em = await Gated("emergency-on", now, () => actions.StationEmergencyAsync(ct));
                        if (em is { Ok: true }) { store.AddEvent(now, "emergency-on", em.CommandLine); Complete(a, now); onState?.Enter(StateActions.Emergency); }
                        break;
                    case StationAction.MainsBack:
                        var off = await Gated("mains-back", now, () => actions.LiftAsync(ct));
                        if (off is { Ok: true }) { store.AddEvent(now, "mains-back", off.CommandLine); Complete(a, now); onState?.Enter(StateActions.Normal); }
                        break;
                    case StationAction.Stop:
                        store.AddEvent(now, "stop", $"battery {Fmt(reading!.Soc!.Value)} % - the flag says critical");
                        onState?.Enter(StateActions.Stop);
                        break;
                    case StationAction.HibernateWarning:
                        onState?.Enter(StateActions.HibernateWarn);
                        store.AddEvent(now, "hibernate-warning", $"battery {Fmt(reading!.Soc!.Value)} % - " + (RebaseMayDelay(_state)
                            ? $"the baseline moved at {Clock(_state.RebasedAt!.Value)}: the PC hibernates only after {Minutes(config.RebaseHibernateConfirmSec)} at or under {Fmt(config.HibernateAt)} %"
                            : $"the PC hibernates at {Fmt(config.HibernateAt)} %"));
                        break;
                    case StationAction.HibernateDelayed:
                        string text = $"battery {Fmt(reading!.Soc!.Value)} % - hibernate held back: the baseline moved at {Clock(_state.RebasedAt!.Value)}; "
                            + $"allowed from {Clock(_state.HibernateDelayedUntil!.Value)} if the battery stays at or under {Fmt(config.HibernateAt)} %";
                        store.AddEvent(now, "hibernate-delayed", text);
                        log(text);
                        break;
                    case StationAction.ModeDropped:
                        // The decision dropped the page choice and left the reason in the state, saved above.
                        store.AddEvent(now, "mode-dropped", _state.ModeError);
                        log($"mode: {_state.ModeError}");
                        break;
                    default:
                        break;   // Hibernate: below, after the battery write, behind its guard
                }
            }

            // The reading goes into the flag after the mode and before a hibernation: the level is what sessions hear.
            // Best effort: a failure is an event and backs off; no level waits for it.
            if (d.Battery is { } b && BatteryWritePolicy.ShouldWrite(_battery, b, now, config))
            {
                var r = await Gated("battery", now, () => actions.BatteryAsync(b, config.HibernateAt, ct));
                if (r is { Ok: true }) _battery = new BatteryWritten(b.Soc, b.Level, b.Mains, now);
            }

            (Reading Reading, bool Accepted)? fresh = null;
            if (d.Actions.Contains(StationAction.Hibernate) && _backoff.Open("hibernate", now)) fresh = await HibernateAsync(d, ct);
            // The wake: off on mains; after the timer woke the PC, the hibernation again on battery at or under the level or
            // with no reading (spec 4.1e). A fresh reading the hibernation above took supersedes the poll's: one on mains
            // that refused it is never followed by a hibernation on the older reading.
            // A fresh reading that was rejected says nothing: the poll's own (accepted) reading stands.
            var (heard, accepted) = fresh is { Accepted: true } f ? (f.Reading, true) : (reading, status == ReadingStatus.Accepted);
            if (wake is not null && await wake.AfterPollAsync(now, heard, accepted, ct) is var step and not WakeStep.None
                && _backoff.Open("hibernate", now)) await RehibernateAsync(step, ct);
            return d.DelaySec;
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// The hibernation counts as done (saved) right before shutdown runs, and goes back if it fails. Returns the fresh
    /// reading it took (and whether it was accepted), null when it took none or none answered.
    /// </summary>
    private async Task<(Reading Reading, bool Accepted)?> HibernateAsync(StationDecisionResult d, CancellationToken ct)
    {
        var now = time.GetLocalNow();
        bool killSwitch = File.Exists(KillSwitch);
        string? why = HibernateGuard.BeforeRead(d, killSwitch);
        if (KillSwitchHolds(killSwitch, why ?? "", now)) return null;
        Reading? fresh = null;
        if (why is null)
        {
            fresh = await reader.ReadAsync(ct);
            now = time.GetLocalNow();
            why = HibernateGuard.AfterRead(d, fresh, now, config);
            string rejected = fresh is null ? "" : StationDecision.Check(fresh, d.State, now, config);
            store.AddReading(now, fresh, fresh is null ? ReadingStatus.Missed : rejected.Length > 0 ? ReadingStatus.Rejected : ReadingStatus.Accepted, rejected);
        }
        (Reading, bool)? taken = fresh is null ? null : (fresh, StationDecision.Check(fresh, d.State, now, config).Length == 0);
        if (why is not null) { store.AddEvent(now, "hibernate-refused", why); log($"hibernate: refused - {why}"); return taken; }
        // The wake first: once shutdown runs nothing after it may. A wake that cannot be armed never holds this back.
        if (wake is not null) await wake.BeforeHibernateAsync(now, fresh!.Soc, ct);
        store.AddEvent(now, "hibernate", $"battery {Fmt(fresh!.Soc!.Value)} %, on battery");
        await RunHibernateAsync(now, ct);
        return taken;
    }

    /// <summary>The kill switch is checked on every poll, so removing the file is heard at once; only its event backs off. true = held.</summary>
    private bool KillSwitchHolds(bool killSwitch, string why, DateTimeOffset now)
    {
        if (!killSwitch)
        {
            _backoff.Succeeded(RefusedKey);
            return false;
        }
        if (!_backoff.Open(RefusedKey, now)) return true;
        var next = _backoff.Failed(RefusedKey, now);
        store.AddEvent(now, "hibernate-refused", $"{why} - said again {Clock(next)}");
        log($"hibernate: refused - {why}");
        return true;
    }

    /// <summary>
    /// The timer woke the PC during a cut (spec 4.1d, e): this poll's accepted reading is on battery at or under
    /// hibernateAt, or no reading was accepted for <c>wakeReadSec</c>. The first takes the fresh reading right before
    /// acting as every hibernation (the resume's agreement and warning delays are for a wake by hand); one on mains
    /// refuses it and ends what the wake owed. It hibernates again, the next wake armed. The kill switch and a dry run hold
    /// it as any hibernation. A wake by hand never comes here.
    /// </summary>
    private async Task RehibernateAsync(WakeStep step, CancellationToken ct)
    {
        var now = time.GetLocalNow();
        if (KillSwitchHolds(File.Exists(KillSwitch), "disabled by the kill switch", now)) return;
        double? soc = _state.LastSoc;
        if (step == WakeStep.LowOnBattery)
        {
            var fresh = await reader.ReadAsync(ct);
            now = time.GetLocalNow();
            string rejected = fresh is null ? "" : StationDecision.Check(fresh, _state, now, config);
            store.AddReading(now, fresh, fresh is null ? ReadingStatus.Missed : rejected.Length > 0 ? ReadingStatus.Rejected : ReadingStatus.Accepted, rejected);
            if (HibernateGuard.Fresh(fresh, _state, now, config) is { } refused)
            {
                store.AddEvent(now, "hibernate-refused", $"{refused} (after the wake timer)");
                log($"hibernate: refused after the wake timer - {refused}");
                if (fresh is not null && rejected.Length == 0) await wake!.AfterPollAsync(now, fresh, true, ct);   // mains on ends what the wake owed
                return;
            }
            soc = fresh!.Soc;
        }
        await wake!.BeforeHibernateAsync(now, soc, ct);
        string why = step == WakeStep.LowOnBattery
            ? $"battery {Fmt(soc!.Value)} %, on battery, after the wake timer woke the PC"
            : $"no reading for {Minutes((int)wake.ReadWithin.TotalSeconds)} after the wake timer woke the PC";
        store.AddEvent(now, "hibernate", why);
        log($"hibernate: {why}");
        await RunHibernateAsync(now, ct);
    }

    private async Task RunHibernateAsync(DateTimeOffset now, CancellationToken ct)
    {
        var before = _state;
        Complete(StationAction.Hibernate, now);   // saved before the call: the PC may be gone before anything after it runs
        if (await hibernator.HibernateAsync(ct))
        {
            _backoff.Succeeded("hibernate");
            wake?.Hibernated(now);
            return;
        }
        _state = before;
        store.SaveState(_state);
        if (config.DryRun)
        {
            // Not a failure, but backed off like one: no fresh reading and no event at the poll rate.
            var next = _backoff.Failed("hibernate", now);
            store.AddEvent(now, "hibernate-skipped", $"dry run - next try {Clock(next)}");
        }
        else Failed("hibernate", now, "shutdown /h");
    }

    private const string RefusedKey = "hibernate-refused";

    /// <summary>
    /// The page's Auto / Eco / Max: the choice is saved at once and its command left to the service loop
    /// (<see cref="ApplyPendingAsync"/>), never to the request. Eco = the station's eco (and the <c>eco</c> action);
    /// Max = every mode lifted; Auto = the decision takes over, and an Eco of the page is lifted while there is no cut.
    /// The choice holds until the next observed mains change or Auto. Max during a station emergency is refused; a
    /// command still waiting is dropped by an emergency hold (Eco included) and by an observed mains change.
    /// </summary>
    public async Task<ModeChange> SetModeAsync(ManualMode mode, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var now = time.GetLocalNow();
            // The battery levels apply always: Max overrides eco only, never a station emergency.
            if (mode == ManualMode.Max && EmergencyHolds(flagReader.Read(now)))
            {
                store.AddEvent(now, "mode-refused", MaxHeldBack);
                return new ModeChange(false, 409, MaxHeldBack, _state.Manual);
            }
            bool liftEco = mode == ManualMode.Auto && _state.Manual == ManualMode.Eco && !_state.Cut;
            _state = StationDecision.SetManual(_state, mode, now);
            _state.ModePending = mode != ManualMode.Auto || liftEco ? mode : null;
            _state.ModeError = "";
            store.SaveState(_state);
            store.AddEvent(now, "mode", mode.ToString().ToLowerInvariant());
            _wake.Release();
            return new ModeChange(true, 202, "accepted", _state.Manual);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Runs the command of a saved page choice, with the power-mode timeout and the normal backoff.</summary>
    public async Task ApplyPendingAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_state.ModePending is not { } mode) return;
            var now = time.GetLocalNow();
            // Every attempt, retries included, re-checks the hold: no page choice ever lifts a station emergency, and the
            // page's Eco, which power-mode.ps1 refuses under an emergency, is dropped the same way instead of retried.
            var flag = flagReader.Read(now);
            if (EmergencyHolds(flag))
            {
                if (_state.Manual == mode && mode != ManualMode.Auto) _state = StationDecision.SetManual(_state, ManualMode.Auto, now);
                _state.ModePending = null;
                _state.ModeError = mode switch { ManualMode.Max => MaxHeldBack, ManualMode.Eco => EcoHeldBack, _ => AutoLiftHeldBack };
                store.SaveState(_state);
                store.AddEvent(now, "mode-refused", _state.ModeError);
                return;
            }
            // Unknown refuses: with the flag unreadable nobody knows whether an emergency is on, so nothing is lifted. The
            // lift stays pending and is tried again with the normal backoff.
            if (mode != ManualMode.Eco && flag is null)
            {
                if (!_backoff.Open("mode-lift", now)) return;
                _state.ModeError = $"{mode}: {FlagUnreadable}";
                store.SaveState(_state);
                Failed("mode-lift", now, FlagUnreadable);
                return;
            }
            ActionResult? r = mode == ManualMode.Eco
                ? await Gated("mode-eco", now, () => actions.StationEcoAsync(ct))
                : await Gated("mode-lift", now, () => actions.LiftAsync(ct));
            if (r is null) return;   // backing off
            if (!r.Ok && !r.DryRun) { _state.ModeError = $"{mode}: {r.LastLine}"; store.SaveState(_state); return; }
            if (r.Ok) onState?.Enter(mode == ManualMode.Eco ? StateActions.Eco : StateActions.Normal);
            _state.ModePending = null;
            _state.ModeError = "";
            store.SaveState(_state);
            store.AddEvent(now, "mode-applied", mode.ToString().ToLowerInvariant() + (r.DryRun ? " (dry run)" : ""));
        }
        finally { _gate.Release(); }
    }

    /// <summary>Waits until <paramref name="wait"/> passes or a page choice arrives.</summary>
    public async Task WaitForWorkAsync(TimeSpan wait, CancellationToken ct)
    {
        if (wait > TimeSpan.Zero) await _wake.WaitAsync(wait, ct);
    }

    /// <summary>The last failure of a page choice's command, "" when none.</summary>
    public string ModeError => _state.ModeError;
    public const string MaxHeldBack = "Max held back: battery emergency";
    public const string AutoLiftHeldBack = "Auto: eco not lifted: battery emergency";
    public const string EcoHeldBack = "Eco held back: battery emergency";
    public const string FlagUnreadable = "the power-mode flag is unreadable - nothing lifted";

    /// <summary>An emergency is on (the flag says so) or the station's emergency level is reached in this cut.</summary>
    public bool EmergencyHolds(PowerModeFlag? flag) =>
        flag?.Mode == "emergency" || (_state.Cut && _state.Reached[(int)LevelStep.Emergency]);

    public void Compact()
    {
        int folded = store.Compact(time.GetLocalNow(), config.RawRetentionDays);
        if (folded > 0) log($"storage: {folded} raw rows folded into 5-minute aggregates");
    }
}
