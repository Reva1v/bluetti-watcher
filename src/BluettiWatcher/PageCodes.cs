namespace BluettiWatcher;

/// <summary>A text the page shows, with the stable code the page translates it by; <see cref="Args"/> fill the code's {0}, {1}, ...</summary>
internal sealed record PageNote(string Code, string Text, params string[] Args);

/// <summary>
/// Every code the service sends the page in place of prose. The page's dictionary holds, in each language, <c>n_</c> + a note
/// code, <c>v_</c> + a value code and <c>om_</c> + an output mode, hyphens as underscores (a test checks).
/// </summary>
internal static class PageCodes
{
    // /api/now notes
    public const string StationSilent = "station-silent";
    public const string HibernateHeld = "hibernate-held";
    public const string MaxHeld = "max-held";
    public const string ReadingRejected = "reading-rejected";
    public const string WakeDisarmFailed = "wake-disarm-failed";
    // modeError and the POST's answer
    public const string EcoHeld = "eco-held";
    public const string AutoHeld = "auto-held";
    public const string FlagUnreadable = "flag-unreadable";
    public const string ModeDroppedBack = "mode-dropped-back";
    public const string ModeDroppedLost = "mode-dropped-lost";
    public const string ModeDropped = "mode-dropped";
    public const string ModeFailed = "mode-failed";
    public const string ModeOther = "mode-other";
    // the eco chip
    public const string EcoEmergency = "eco-emergency";
    public const string EcoEmergencyOver = "eco-emergency-over";
    public const string EcoMains = "eco-mains";
    public const string EcoUntil = "eco-until";
    // DTEK Watcher notes
    public const string DtekQueuesUnknown = "dtek-queues-unknown";
    public const string DtekNoSchedule = "dtek-no-schedule";
    public const string DtekStale = "dtek-stale";
    public const string DtekQueueNotFixed = "dtek-queue-not-fixed";
    public const string DtekGroupRefuted = "dtek-group-refuted";
    public const string DtekGroupNotHouse = "dtek-group-not-house";
    public const string DtekUpdateUnreadable = "dtek-update-unreadable";
    public const string DtekDatasetFailed = "dtek-dataset-failed";
    public const string DtekSiteBlocked = "dtek-site-blocked";
    public const string DtekSiteFailed = "dtek-site-failed";
    public const string DtekConfig = "dtek-config";
    public const string DtekNotStarted = "dtek-not-started";
    public const string DtekModuleError = "dtek-module-error";

    /// <summary>Every note code above (no reflection: the service is NativeAOT; a test compares it with the constants).</summary>
    public static IReadOnlyList<string> Notes { get; } =
    [
        StationSilent, HibernateHeld, MaxHeld, ReadingRejected, WakeDisarmFailed, EcoHeld, AutoHeld, FlagUnreadable, ModeDroppedBack, ModeDroppedLost, ModeDropped,
        ModeFailed, ModeOther, EcoEmergency, EcoEmergencyOver, EcoMains, EcoUntil, DtekQueuesUnknown, DtekNoSchedule, DtekStale, DtekQueueNotFixed,
        DtekGroupRefuted, DtekGroupNotHouse, DtekUpdateUnreadable, DtekDatasetFailed, DtekSiteBlocked, DtekSiteFailed, DtekConfig, DtekNotStarted,
        DtekModuleError,
    ];

    /// <summary>Codes sent as values: DTEK states and sources, source states, read error classes, the page's modes, "battery lasts".</summary>
    public static IReadOnlyList<string> Values { get; } =
        ["off", "ok", "stale", "none", "blocked", "failed", "network", "format", "dataset", "site", "auto", "eco", "max", "yes", "no", "unknown"];

    /// <summary>The station's output modes (bluetti_bt_lib enums/output_mode.py), and CLOUD_MAINS: a cloud reading with AC input (design.md §4 AC2a).</summary>
    public static IReadOnlyList<string> OutputModes { get; } = ["STOP", "INVERTER_OUTPUT", "BYPASS_OUTPUT_C", "BYPASS_OUTPUT_D", "LOAD_MATCHING", "CLOUD_MAINS"];

    /// <summary>The page's dictionary keys for every code.</summary>
    public static IEnumerable<string> Keys() =>
        Notes.Select(c => "n_" + c).Concat(Values.Select(c => "v_" + c)).Concat(OutputModes.Select(c => "om_" + c)).Select(k => k.Replace('-', '_'));
}
