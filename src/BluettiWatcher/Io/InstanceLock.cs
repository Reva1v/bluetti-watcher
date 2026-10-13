namespace BluettiWatcher.Io;

/// <summary>
/// One instance per port. A restart of the logon task (Stop-ScheduledTask, then Start-ScheduledTask) can start the new
/// instance while the old one is still exiting: the new one waits for the old one to let go instead of giving up at once.
/// </summary>
internal static class InstanceLock
{
    public static string Name(int port) => $"Local\\BluettiWatcher-{port}";

    /// <summary>Takes the port's lock, waiting up to <paramref name="wait"/> for the holder to let go; null = still held.</summary>
    public static Mutex? Take(int port, TimeSpan wait, Action<string> log)
    {
        var mutex = new Mutex(false, Name(port), out _);
        if (TryWait(mutex, TimeSpan.Zero)) return mutex;
        log($"another instance holds this port's lock - waiting up to {wait.TotalSeconds:0.#} s for it to exit");
        if (TryWait(mutex, wait)) { log("the port's lock was released - starting"); return mutex; }
        mutex.Dispose();
        return null;
    }

    private static bool TryWait(Mutex mutex, TimeSpan wait)
    {
        // A holder that was killed (Stop-ScheduledTask) abandons the lock: it is ours then.
        try { return mutex.WaitOne(wait); }
        catch (AbandonedMutexException) { return true; }
    }
}
