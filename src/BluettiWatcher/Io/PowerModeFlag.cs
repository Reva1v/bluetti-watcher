using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BluettiWatcher.Decision;

namespace BluettiWatcher.Io;

/// <summary>The power-mode flag as power-mode-flag.ps1's Read-PowerModeFlag sees it (the fields the service uses).</summary>
internal sealed record PowerModeFlag(string Mode, string Source, string By, DateTimeOffset? At, DateTimeOffset? UntilAt,
    bool Expired, DateTimeOffset? ReplacedUntilAt)
{
    public static PowerModeFlag Normal { get; } = new("normal", "manual", "", null, null, false, null);
    public FlagView View => new(Mode, Source, By, At);
}

internal interface IFlagReader
{
    /// <summary>null = the flag cannot be trusted (unreadable).</summary>
    PowerModeFlag? Read(DateTimeOffset now);
}

/// <summary>
/// Reads the power-mode flag file under the writers' named mutex, never writing it. The mutex is the one
/// power-mode-flag.ps1 takes (POWER_MODE_MUTEX, config <c>powerMode.mutex</c>); a test passes its own name and a short wait.
/// </summary>
internal sealed partial class FlagFileReader(string path, Action<string> log, string mutexName, TimeSpan? lockWait = null) : IFlagReader
{
    /// <summary>The mutex this reader takes.</summary>
    public string MutexName => mutexName;

    private readonly TimeSpan _wait = lockWait ?? TimeSpan.FromSeconds(5);

    public PowerModeFlag? Read(DateTimeOffset now)
    {
        using var m = new Mutex(false, mutexName);
        bool owned;
        try { owned = m.WaitOne(_wait); }
        catch (AbandonedMutexException) { owned = true; }   // a writer that died: ours now
        if (!owned)
        {
            log($"flag: a writer holds the lock for {_wait.TotalSeconds.ToString("0.##", CultureInfo.InvariantCulture)} s - unreadable this poll");
            return null;
        }
        try
        {
            if (!File.Exists(path)) return PowerModeFlag.Normal;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
            return Parse(reader.ReadToEnd(), now);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log($"flag: {e.Message}");
            return null;
        }
        finally { m.ReleaseMutex(); }
    }

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2}(\.\d+)?)?(Z|[+-]\d{2}:\d{2})$")]
    private static partial Regex SincePattern();

    private static DateTimeOffset? Time(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { } t && SincePattern().IsMatch(t)
        && DateTimeOffset.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) ? at : null;

    private static string? Text(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>The flag text as Read-PowerModeFlag reads it; null = not a valid flag. An eco past its <c>until</c> reads normal.</summary>
    public static PowerModeFlag? Parse(string text, DateTimeOffset now)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            var j = doc.RootElement;
            if (j.ValueKind != JsonValueKind.Object) return null;
            string? mode = Text(j, "mode");
            if (mode is not ("emergency" or "normal" or "eco")) return null;
            if (Time(j, "since") is not { } at) return null;
            string source = Text(j, "source") == "station" ? "station" : "manual";
            string by = Text(j, "by") is "manual" or "station" ? Text(j, "by")! : "";
            DateTimeOffset? replaced = j.TryGetProperty("replaced", out var r) && r.ValueKind == JsonValueKind.Object && Time(r, "since") is not null
                ? Time(r, "until") : null;
            var flag = new PowerModeFlag(mode, source, by, at, null, false, replaced);
            if (mode != "eco") return flag;
            bool hasUntil = j.TryGetProperty("until", out var u) && u.ValueKind != JsonValueKind.Null;
            if (!hasUntil && source == "station") return flag;   // the station's eco lasts while mains is off
            if (Time(j, "until") is not { } untilAt) return null;
            flag = flag with { UntilAt = untilAt };
            return now >= untilAt ? flag with { Mode = "normal", At = untilAt, Expired = true } : flag;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>A time people read: HH:mm in <paramref name="zone"/>, the PC's local time zone when not given.</summary>
    public static string Clock(DateTimeOffset at, TimeZoneInfo? zone = null) =>
        TimeZoneInfo.ConvertTime(at, zone ?? TimeZoneInfo.Local).ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>One line for the page's eco chip, "" when the mode is normal; times in <paramref name="zone"/> (local when not given).</summary>
    public static string EcoText(PowerModeFlag? flag, DateTimeOffset now, TimeZoneInfo? zone = null)
    {
        if (flag is null) return "";
        if (flag.Mode == "emergency" && flag.ReplacedUntilAt is { } ru && ru > now) return $"Emergency mode (over a manual eco until {Clock(ru, zone)})";
        if (flag.Mode == "emergency") return "Emergency mode";
        if (flag.Mode == "eco" && flag.UntilAt is null) return "Eco mode while mains is off";
        if (flag.Mode == "eco") return $"Eco mode until {Clock(flag.UntilAt!.Value, zone)}";
        return "";
    }

    /// <summary><see cref="EcoText"/> with the code the page translates it by; null when the mode is normal.</summary>
    public static PageNote? EcoNote(PowerModeFlag? flag, DateTimeOffset now, TimeZoneInfo? zone = null)
    {
        string text = EcoText(flag, now, zone);
        if (text.Length == 0 || flag is null) return null;
        if (flag.Mode == "emergency" && flag.ReplacedUntilAt is { } ru && ru > now) return new(PageCodes.EcoEmergencyOver, text, Clock(ru, zone));
        if (flag.Mode == "emergency") return new(PageCodes.EcoEmergency, text);
        if (flag.UntilAt is { } until) return new(PageCodes.EcoUntil, text, Clock(until, zone));
        return new(PageCodes.EcoMains, text);
    }
}
