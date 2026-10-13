using System.Text.Json;
using BluettiWatcher.Decision;
using BluettiWatcher.Io;

namespace BluettiWatcher.Tests;

/// <summary>
/// The adapter against the real power-mode.ps1, opt-in: set BLUETTI_PM_SCRIPT to a power-mode.ps1 (the tools worktree copy).
/// The flag is a power-mode-test-*.json under the temp directory and powercfg / schtasks are stand-ins, so neither the
/// live flag nor the power plan nor the Task Scheduler is touched. Without the variable the test returns at once.
/// </summary>
public sealed class PowerModeIntegrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bluetti-watcher-pm-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    // design.md §2 Architecture
    [Fact]
    public async Task Eco_battery_on_and_off_write_the_flag_through_power_mode_ps1()
    {
        string? script = Environment.GetEnvironmentVariable("BLUETTI_PM_SCRIPT");
        if (string.IsNullOrEmpty(script)) return;   // opt-in
        Directory.CreateDirectory(_dir);
        string stub = Path.Combine(_dir, "stand-in.ps1");
        await File.WriteAllTextAsync(stub, "exit 0\n");
        Environment.SetEnvironmentVariable("POWER_MODE_POWERCFG", stub);
        Environment.SetEnvironmentVariable("POWER_MODE_SCHTASKS", stub);
        string flag = Path.Combine(Path.GetTempPath(), $"power-mode-test-{Guid.NewGuid():N}.json");
        try
        {
            var log = new List<string>();
            var a = new PowerModeAdapter(new StationConfig { PowerMode = new() { Script = script, FlagPath = flag }, PowerModeTimeoutSec = 60 }, new ProcessRunner(), log.Add);
            string Mode()
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(flag));
                return doc.RootElement.GetProperty("mode").GetString() + "/" + (doc.RootElement.TryGetProperty("source", out var s) ? s.GetString() : "");
            }

            Assert.True((await a.StationEcoAsync(CancellationToken.None)).Ok, string.Join("\n", log));
            Assert.Equal("eco/station", Mode());
            Assert.True((await a.BatteryAsync(new BatteryReport(19.5, Level.Low, false), 5, CancellationToken.None)).Ok, string.Join("\n", log));
            using (var doc = JsonDocument.Parse(await File.ReadAllTextAsync(flag)))
                Assert.Equal("low", doc.RootElement.GetProperty("battery").GetProperty("level").GetString());
            Assert.True((await a.StationEmergencyAsync(CancellationToken.None)).Ok, string.Join("\n", log));
            Assert.Equal("emergency/station", Mode());
            Assert.True((await a.LiftAsync(CancellationToken.None)).Ok, string.Join("\n", log));
            Assert.StartsWith("normal", Mode(), StringComparison.Ordinal);
            var bad = await a.BatteryAsync(new BatteryReport(150, Level.Ok, true), 5, CancellationToken.None);
            Assert.False(bad.Ok);
            Assert.Equal(2, bad.ExitCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("POWER_MODE_POWERCFG", null);
            Environment.SetEnvironmentVariable("POWER_MODE_SCHTASKS", null);
            if (File.Exists(flag)) File.Delete(flag);
        }
    }
}
