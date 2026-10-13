using System.Text;
using System.Text.RegularExpressions;

namespace BluettiWatcher.Io;

/// <summary>
/// The library's writer never stays on disk: its entry points (Scripts\bluetti-write*), its script and writer modules with
/// their compiled copies, and the two import lines that would load the module (the reader runs without them). The service
/// refuses to start while any of them is there and cannot be removed. Named here only to be deleted.
/// </summary>
internal static partial class WriterGuard
{
    private static readonly string[] Files = [Path.Combine("scripts", "bluetti_write.py"), Path.Combine("bluetooth", "device_writer.py")];

    [GeneratedRegex(@"^(bluetti_write|device_writer)\.")]
    private static partial Regex CompiledCopy();

    [GeneratedRegex(@"^from \.device_writer import \*\s*$")]
    private static partial Regex BluetoothImport();

    [GeneratedRegex(@"^\s*DeviceWriter,\s*$")]
    private static partial Regex PackageImport();

    private static (string File, Regex Line)[] Imports() =>
        [(Path.Combine("bluetooth", "__init__.py"), BluetoothImport()), ("__init__.py", PackageImport())];

    /// <summary>What of the writer is on disk under the venv; an import line is "&lt;file&gt;: import".</summary>
    public static List<string> Left(string venvDir)
    {
        var left = new List<string>();
        string scripts = Path.Combine(venvDir, "Scripts");
        if (Directory.Exists(scripts)) left.AddRange(Directory.GetFiles(scripts, "bluetti-write*"));
        string lib = Path.Combine(venvDir, "Lib", "site-packages", "bluetti_bt_lib");
        if (!Directory.Exists(lib)) return left;
        foreach (var rel in Files) { var p = Path.Combine(lib, rel); if (File.Exists(p)) left.Add(p); }
        foreach (var dir in Directory.GetDirectories(lib, "__pycache__", SearchOption.AllDirectories))
            left.AddRange(Directory.GetFiles(dir).Where(f => CompiledCopy().IsMatch(Path.GetFileName(f))));
        foreach (var (file, line) in Imports())
        {
            var p = Path.Combine(lib, file);
            if (File.Exists(p) && File.ReadAllLines(p).Any(line.IsMatch)) left.Add(p + ": import");
        }
        return left;
    }

    /// <summary>Removes every writer piece; returns what is still there (empty = read only).</summary>
    public static List<string> Remove(string venvDir, Action<string> log)
    {
        string lib = Path.Combine(venvDir, "Lib", "site-packages", "bluetti_bt_lib");
        foreach (var item in Left(venvDir))
        {
            try
            {
                if (item.EndsWith(": import", StringComparison.Ordinal))
                {
                    string p = item[..^8];
                    var line = Imports().First(i => string.Equals(Path.Combine(lib, i.File), p, StringComparison.OrdinalIgnoreCase)).Line;
                    var keep = File.ReadAllLines(p).Where(l => !line.IsMatch(l));
                    File.WriteAllText(p, string.Join('\n', keep) + "\n", new UTF8Encoding(false));
                }
                else File.Delete(item);
                log($"writer guard: removed {item}");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                log($"writer guard: could not remove {item}: {e.Message}");
            }
        }
        return Left(venvDir);
    }
}
