using System.Globalization;
using System.Text.RegularExpressions;
using BluettiWatcher.Decision;

namespace BluettiWatcher.Io;

internal interface IStationReader
{
    /// <summary>One reading; null = the station did not answer.</summary>
    Task<Reading?> ReadAsync(CancellationToken ct);
}

/// <summary>
/// READ ONLY, FOREVER: the only program started against the station is the library's <c>bluetti-read</c>
/// (connect, read, disconnect). Its <c>FieldName.X: value</c> lines are parsed.
/// </summary>
internal sealed partial class BluettiReader(StationConfig config, IProcessRunner runner, Action<string> log) : IStationReader
{
    /// <summary>The writer guard runs before every read: a writer piece that reappeared is removed, or the read is refused (a miss).</summary>
    public async Task<Reading?> ReadAsync(CancellationToken ct)
    {
        var left = WriterGuard.Remove(config.VenvDir, log);
        if (left.Count > 0)
        {
            log($"read refused: the library's writer is on disk and could not be removed: {string.Join("; ", left)}");
            return null;
        }
        var r = await runner.RunAsync(config.ReaderPath, ["-m", config.Mac ?? "", "-t", config.Type], TimeSpan.FromSeconds(config.ReaderTimeoutSec), ct);
        return r.TimedOut ? null : Parse(r.Lines);
    }

    [GeneratedRegex(@"^FieldName\.([A-Z_]+):\s*(.+?)\s*$")]
    private static partial Regex FieldLine();

    [GeneratedRegex(@"^\s*(-?\d+(\.\d+)?)\s*[A-Za-z%]*\s*$")]
    private static partial Regex Number();

    /// <summary>No field at all = no answer. A field that is missing or not a number stays null (the decision calls such a reading incomplete).</summary>
    public static Reading? Parse(IEnumerable<string> lines)
    {
        var f = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            var m = FieldLine().Match(line);
            if (m.Success) f[m.Groups[1].Value] = m.Groups[2].Value;
        }
        if (f.Count == 0) return null;
        double? Num(string k) => f.TryGetValue(k, out var v) && Number().Match(v) is { Success: true } m
            ? double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;
        string? mode = f.TryGetValue("AC_OUTPUT_MODE", out var mv) ? mv.Replace("OutputMode.", "", StringComparison.Ordinal) : null;
        return new Reading(Num("BATTERY_SOC"), Num("AC_INPUT_POWER"), Num("AC_OUTPUT_POWER"), mode,
            Num("DC_INPUT_POWER"), Num("DC_OUTPUT_POWER"), Num("INTERNAL_AC_VOLTAGE"), Num("INTERNAL_AC_FREQUENCY"));
    }
}
