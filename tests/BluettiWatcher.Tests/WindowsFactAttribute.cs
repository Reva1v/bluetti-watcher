namespace BluettiWatcher.Tests;

/// <summary>A fact that depends on Windows file semantics (a FileShare.None handle blocks a delete); skipped on other systems.</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Windows only: an open FileShare.None handle blocks a delete only on Windows";
    }
}
