using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BluettiWatcher.Io;

/// <summary>The exe has no console of its own (WinExe): a command run from a terminal prints into the terminal it came from.</summary>
internal static partial class ConsoleOut
{
    private const int AttachParentProcess = -1;

    [LibraryImport("kernel32.dll", EntryPoint = "AttachConsole")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int AttachConsole(int processId);

    [LibraryImport("kernel32.dll", EntryPoint = "GetStdHandle")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint GetStdHandle(int which);

    private const int StdOutput = -11, StdError = -12;

    /// <summary>A WinExe started from a terminal has no standard handle unless the caller redirected it; a redirected one is the caller's.</summary>
    private static bool HasNoHandle(int which) => GetStdHandle(which) is 0 or -1;

    /// <summary>
    /// Attaches to the parent's console and points stdout/stderr at it, for each stream the caller did not redirect
    /// (a WinExe starts with no standard handles, so attaching alone prints nothing). No parent console (the logon task,
    /// a service) = nothing happens and nothing is printed. Keep the result until the process ends.
    /// </summary>
    public static Attached Attach()
    {
        bool outFree = HasNoHandle(StdOutput), errFree = HasNoHandle(StdError);
        if (!outFree && !errFree) return new Attached(false, false);
        if (AttachConsole(AttachParentProcess) == 0) return new Attached(false, false);
        return new Attached(outFree, errFree);
    }

    /// <summary>Owns the console writer of <see cref="Attach"/>; binds stdout and/or stderr to the attached console.</summary>
    internal sealed class Attached : IDisposable
    {
        private readonly StreamWriter? _writer;

        public Attached(bool bindOut, bool bindErr)
        {
            if (!bindOut && !bindErr) return;
            _writer = new StreamWriter(
                new FileStream("CONOUT$", FileMode.Open, FileAccess.Write, FileShare.ReadWrite),
                new System.Text.UTF8Encoding(false)) { AutoFlush = true };
            var shared = TextWriter.Synchronized(_writer);
            if (bindOut) Console.SetOut(shared);
            if (bindErr) Console.SetError(shared);
        }

        public void Dispose() => _writer?.Dispose();
    }

    /// <summary>Opens <paramref name="url"/> in the default browser; a failure only says so (the address is printed anyway).</summary>
    public static void OpenBrowser(Uri url)
    {
        try { using var _ = Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Console.WriteLine($"the browser did not open ({e.GetType().Name}): open the address above by hand");
        }
    }
}
