using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using BluettiWatcher.Decision;
using BluettiWatcher.DtekWatcher;

namespace BluettiWatcher;

/// <summary>Thresholds, times and paths. Defaults, then <c>config.json</c> in the data directory, then the command line.</summary>
internal sealed class StationConfig
{
    private static readonly string LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// <summary>The station's Bluetooth address; required: without it nothing is polled and /api/health names the problem.</summary>
    public string? Mac { get; set; }
    public string Type { get; set; } = "AC200PL";
    public int CapacityWh { get; set; } = 2304;
    public double Efficiency { get; set; } = 0.85;
    /// <summary>The battery time comes from the SOC drop over the readings off mains within this many minutes.</summary>
    public int RateWindowMin { get; set; } = 60;
    /// <summary>The least SOC drop, points, the measured rate needs; with less the efficiency formula stands.</summary>
    public double RateMinDrop { get; set; } = 3;
    /// <summary>The least span, minutes, the measured rate needs; with less the efficiency formula stands.</summary>
    public int RateMinSpanMin { get; set; } = 20;

    public int PollSec { get; set; } = 60;
    public int FastPollSec { get; set; } = 60;
    public double FastBelow { get; set; } = 25;
    public double EmergencyAt { get; set; } = 20;
    public double StopAt { get; set; } = 10;
    public double WarnAt { get; set; } = 7;
    public double HibernateAt { get; set; } = 5;
    public int AgreeSec { get; set; } = 90;
    public int HibernateAfterWarnSec { get; set; } = 180;
    /// <summary>After a rebase in the cut, hibernate needs this long at or under hibernateAt, counted from the latest rebase.</summary>
    public int RebaseHibernateConfirmSec { get; set; } = 600;
    /// <summary>
    /// A reading accepted after a silence whose SOC change is over the rate applied to at most this many seconds (10 points
    /// by default) counts as a rebase for hibernate.
    /// </summary>
    public int JumpWindowSec { get; set; } = 120;
    public int MainsLostSec { get; set; } = 180;
    public int MainsBackSec { get; set; } = 180;
    public int MaxGapSec { get; set; } = 600;
    public double SocRatePerMin { get; set; } = 5;
    public double SocJumpFloor { get; set; } = 3;
    public int SilentAfter { get; set; } = 3;
    /// <summary>The battery reading goes into the flag at least this often while on battery (the hook hears it for 15 minutes).</summary>
    public int BatteryRefreshSec { get; set; } = 600;

    public int Port { get; set; } = 18940;
    /// <summary>How long a start waits for the instance before it (a task restart) to let go of the port's lock.</summary>
    public int LockWaitSec { get; set; } = 30;
    public string DataDir { get; set; } = Path.Combine(LocalAppData, "bluetti-watcher");
    public string ReaderPath { get; set; } = Path.Combine(LocalAppData, "bluetti-watcher", ".venv", "Scripts", "bluetti-read.exe");
    public int ReaderTimeoutSec { get; set; } = 30;
    /// <summary>The power-mode switch: its script and the live names it shares with the watcher.</summary>
    public PowerModeConfig PowerMode { get; set => field = value ?? new PowerModeConfig(); } = new();
    /// <summary>Alias of <c>powerMode.flagPath</c>; <c>powerMode.flagPath</c> wins when both are set.</summary>
    public string? FlagPath { get; set; }
    public string PwshPath { get; set; } = "pwsh.exe";
    /// <summary>The timeout of a power-mode switch or wake-task command, seconds; kept within 1 to 600.</summary>
    public int PowerModeTimeoutSec { get; set => field = Math.Clamp(value, MinTimeoutSec, MaxTimeoutSec); } = 60;
    /// <summary>The timeout of an <see cref="Actions"/> entry, seconds; kept within 1 to 600.</summary>
    public int ActionTimeoutSec { get; set => field = Math.Clamp(value, MinTimeoutSec, MaxTimeoutSec); } = 30;
    public const int MinTimeoutSec = 1;
    public const int MaxTimeoutSec = 600;
    /// <summary>
    /// Optional commands run when the watcher enters a state: keys <c>eco</c>, <c>emergency</c>, <c>stop</c>,
    /// <c>hibernate-warn</c>, <c>normal</c>; each value is an executable and its arguments, run without a shell.
    /// </summary>
    public Dictionary<string, string>? Actions { get; set; }
    public int RawRetentionDays { get; set; } = 30;
    /// <summary>Logs every power-mode command and hibernation instead of running it.</summary>
    public bool DryRun { get; set; }

    /// <summary>
    /// A hibernation during a cut arms the wake task this many minutes ahead (10 to 1440); 0 = off, the default until the
    /// user has seen this PC wake from hibernation by the timer (README, Wake).
    /// </summary>
    public int WakeEveryMinutes { get; set; }
    /// <summary>After the timer woke the PC: with no accepted reading for this many seconds (60 to 3600), it hibernates again.</summary>
    public int WakeReadSec { get; set; } = 300;
    /// <summary>
    /// At or under this battery percent (0..hibernateAt; 0 = no floor) a hibernation arms no wake: the wakes must not drain
    /// the station. Not set: hibernateAt - 5, at least 0.
    /// </summary>
    public double? WakeFloorPercent { get; set; }
    /// <summary>The wake task install.ps1 registers (disabled, WakeToRun, a no-op as the current user).</summary>
    public string WakeTaskName { get; set; } = "BluettiWatcherWake";

    /// <summary>The BLUETTI cloud fallback when Bluetooth misses (design.md §4 Cloud fallback): not set = on when the token file exists at start; false = off.</summary>
    public bool? CloudEnabled { get; set; }
    /// <summary>The cloud token file; not set = cloud-token.json in the data directory.</summary>
    public string? CloudTokenPath { get; set; }
    /// <summary>A cloud reading off mains at or over this AC load, W, whose SOC has not moved for <see cref="StaleAfterMin"/> is stale (design.md §4 AC9).</summary>
    public double StaleLoadW { get; set; } = 100;
    /// <summary>Minutes an unchanged cloud SOC under load off mains takes to count as stale; 0 or less = no stale check.</summary>
    public int StaleAfterMin { get; set; } = 30;

    public const string DefaultMutex = "Local\\BluettiWatcherPowerMode";
    public const string DefaultTaskPrefix = "BluettiWatcherEco";

    /// <summary>The flag file: <c>powerMode.flagPath</c>, then the top-level <c>flagPath</c>, then %ProgramData%\BluettiWatcher\power-mode.json.</summary>
    [JsonIgnore]
    public string FlagFile => NonEmpty(PowerMode.FlagPath) ?? NonEmpty(FlagPath)
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "BluettiWatcher", "power-mode.json");

    /// <summary>The switch script; not set = <c>&lt;exe dir&gt;\..\power-mode\power-mode.ps1</c>.</summary>
    [JsonIgnore]
    public string PowerModeScript => NonEmpty(PowerMode.Script) ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "power-mode", "power-mode.ps1"));

    [JsonIgnore]
    public string PowerModeMutex => NonEmpty(PowerMode.Mutex) ?? DefaultMutex;

    /// <summary>
    /// Set when a flag file is configured (<c>powerMode.flagPath</c> or the top-level alias) but the mutex is left at the default:
    /// a power-mode switch installed elsewhere then has to use the same mutex. Null otherwise.
    /// </summary>
    [JsonIgnore]
    public string? MutexWarning => (NonEmpty(PowerMode.FlagPath) ?? NonEmpty(FlagPath)) is not null && NonEmpty(PowerMode.Mutex) is null
        ? "flagPath set but powerMode.mutex is the default; a power-mode switch installed elsewhere must use the same mutex"
        : null;

    [JsonIgnore]
    public string PowerModeTaskPrefix => NonEmpty(PowerMode.TaskPrefix) ?? DefaultTaskPrefix;

    /// <summary>The environment every power-mode child gets: the live names the watcher itself uses.</summary>
    [JsonIgnore]
    public IReadOnlyDictionary<string, string> PowerModeEnvironment => new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["POWER_MODE_FILE"] = FlagFile,
        ["POWER_MODE_MUTEX"] = PowerModeMutex,
        ["POWER_MODE_TASK_PREFIX"] = PowerModeTaskPrefix,
        ["POWER_MODE_EXTRA_READERS"] = string.Join(',', ExtraReaders),
    };

    /// <summary>The accounts <c>powerMode.extraReaders</c> names, in order; empty when none is set or any name is invalid (all or nothing).</summary>
    [JsonIgnore]
    public IReadOnlyList<string> ExtraReaders => ExtraReadersProblem is null ? [.. PowerMode.ExtraReaders] : [];

    /// <summary>The invalid names in <c>powerMode.extraReaders</c>, named; null when all are valid. Allowed: letters, digits, <c>_ . -</c> and space, 1 to 64 characters.</summary>
    [JsonIgnore]
    public string? ExtraReadersProblem
    {
        get
        {
            var bad = PowerMode.ExtraReaders.Where(n => !ValidReaderName(n)).ToList();
            return bad.Count == 0 ? null : $"powerMode.extraReaders: invalid account name {string.Join(", ", bad.Select(n => $"\"{n}\""))} (letters, digits, _ . - and space, 1 to 64 characters); no extra readers are passed";
        }
    }

    private static readonly Regex ReaderName = new(@"\A[A-Za-z0-9_. -]{1,64}\z", RegexOptions.CultureInvariant);

    private static bool ValidReaderName(string? name) => name is not null && ReaderName.IsMatch(name);

    /// <summary>Why the service is not fully configured (shown on /api/health); null when nothing is wrong.</summary>
    [JsonIgnore]
    public string? ConfigProblem => MacProblem is { } m ? (ExtraReadersProblem is { } r ? m + "; " + r : m) : ExtraReadersProblem;

    /// <summary>Why the service cannot poll; null when it can.</summary>
    [JsonIgnore]
    public string? MacProblem => NonEmpty(Mac) is null ? "mac is not set: put the station's Bluetooth address into config.json (\"mac\")" : null;

    private static string? NonEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    [JsonIgnore]
    public string CloudTokenFile => CloudTokenPath ?? Path.Combine(DataDir, "cloud-token.json");

    [JsonIgnore]
    public bool CloudOn => CloudEnabled ?? File.Exists(CloudTokenFile);

    /// <summary>The DTEK Watcher module (design.md §11 DTEK module); off until an address is given.</summary>
    public DtekConfig DtekWatcher { get; set => field = value ?? new DtekConfig(); } = new();

    /// <summary>The most one interval between accepted readings adds to the rebase confirmation: twice the poll interval.</summary>
    [JsonIgnore]
    public int ConfirmStepCapSec => 2 * Math.Max(PollSec, FastPollSec);

    [JsonIgnore]
    public string VenvDir => Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(ReaderPath))) ?? "";

    /// <summary>Defaults, then <c>--config</c> (or <c>config.json</c> in the data directory), then the flags.</summary>
    public static StationConfig Load(string[] args)
    {
        var flags = ParseArgs(args);
        string? dataDir = flags.GetValueOrDefault("data-dir");
        string file = flags.GetValueOrDefault("config") ?? Path.Combine(dataDir ?? new StationConfig().DataDir, "config.json");
        var cfg = File.Exists(file)
            ? JsonSerializer.Deserialize(File.ReadAllText(file), StationJson.Default.StationConfig) ?? new StationConfig()
            : new StationConfig();
        if (dataDir is not null) cfg.DataDir = dataDir;
        if (flags.TryGetValue("port", out var port)) cfg.Port = int.Parse(port, CultureInfo.InvariantCulture);
        if (flags.TryGetValue("reader", out var reader)) cfg.ReaderPath = reader;
        if (flags.TryGetValue("flag", out var flag)) cfg.PowerMode.FlagPath = flag;
        if (flags.TryGetValue("power-mode", out var pm)) cfg.PowerMode.Script = pm;
        if (flags.ContainsKey("dry-run")) cfg.DryRun = true;
        return cfg;
    }

    private static readonly HashSet<string> SwitchFlags = new(["dry-run", "cloud-login", "version", "help"], StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ValueFlags = new(["config", "data-dir", "port", "reader", "flag", "power-mode"], StringComparer.OrdinalIgnoreCase);

    /// <summary>Flags to values; a switch maps to "1". An unknown flag, a stray word or a value that is itself a flag is refused.</summary>
    internal static Dictionary<string, string> ParseArgs(string[] args)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg is "-h" or "/?") { d["help"] = "1"; continue; }
            if (!arg.StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"unknown argument '{arg}'");
            string key = arg[2..];
            if (SwitchFlags.Contains(key)) { d[key] = "1"; continue; }
            if (!ValueFlags.Contains(key)) throw new ArgumentException($"unknown flag '{arg}'");
            if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"--{key} needs a value");
            d[key] = args[++i];
        }
        return d;
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = false, ReadCommentHandling = JsonCommentHandling.Skip, UseStringEnumConverter = true)]
[JsonSerializable(typeof(StationConfig))]
[JsonSerializable(typeof(PowerModeConfig))]
[JsonSerializable(typeof(StationState))]
[JsonSerializable(typeof(DtekSnapshot))]
[JsonSerializable(typeof(List<DtekLate>))]
[JsonSerializable(typeof(WakeState))]
internal sealed partial class StationJson : JsonSerializerContext;

/// <summary>Config section <c>powerMode</c>; every value not set takes its default.</summary>
internal sealed class PowerModeConfig
{
    /// <summary>power-mode.ps1; not set = beside the install, <c>&lt;exe dir&gt;\..\power-mode\power-mode.ps1</c>.</summary>
    public string? Script { get; set; }
    /// <summary>The flag file (POWER_MODE_FILE).</summary>
    public string? FlagPath { get; set; }
    /// <summary>The flag's named mutex (POWER_MODE_MUTEX).</summary>
    public string? Mutex { get; set; }
    /// <summary>The scheduled-task prefix (POWER_MODE_TASK_PREFIX).</summary>
    public string? TaskPrefix { get; set; }
    /// <summary>Accounts that may also read the flag's folder (POWER_MODE_EXTRA_READERS); default none.</summary>
    public List<string> ExtraReaders { get; set => field = value ?? []; } = [];
}
