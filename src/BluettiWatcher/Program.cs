using System.Net;
using System.Net.Sockets;
using BluettiWatcher;
using BluettiWatcher.DtekWatcher;
using BluettiWatcher.Http;
using BluettiWatcher.Io;
using BluettiWatcher.Storage;

// Command line: see Cli.Usage (--help). The service prints nothing (it logs to service.log); every other command prints
// into the terminal it was started from.
// Exit: 0 stopped (or token saved, help, version) · 1 the login failed · 2 could not run (bad arguments, another instance past lockWaitSec, the port taken, the writer on disk, no reader).
using var console = ConsoleOut.Attach();
var outcome = await Cli.ExecuteAsync(args, Console.Out, Console.Error, async cfg =>
{
    using var loginHttp = new SocketsHttpHandler();
    var login = new CloudLogin(cfg, loginHttp, TimeProvider.System, Console.WriteLine, ConsoleOut.OpenBrowser);
    return await login.RunAsync(CancellationToken.None);
});
if (outcome.Exit is { } exitCode) return exitCode;
var config = outcome.Config!;
Directory.CreateDirectory(config.DataDir);
var fileLog = new FileLog(Path.Combine(config.DataDir, "service.log"));
void Log(string text) => fileLog.Write(text);

// A task restart can start this instance while the old one still exits: wait for its lock, bounded (0..600 s).
int lockWait = Math.Clamp(config.LockWaitSec, 0, 600);
using var single = InstanceLock.Take(config.Port, TimeSpan.FromSeconds(lockWait), Log);
if (single is null) { Log($"DID NOT RUN - another instance still holds this port's lock after {lockWait} s"); return 2; }

// The flag's mutex is Local\: the service must share the user's interactive session, never session 0.
if (System.Diagnostics.Process.GetCurrentProcess().SessionId == 0) { Log("DID NOT RUN - session 0: the service runs in the user's interactive session (install.ps1)"); return 2; }

// Read only, forever: the library's writer never stays on disk.
var left = WriterGuard.Remove(config.VenvDir, Log);
if (left.Count > 0) { Log($"DID NOT RUN - the library's writer is on disk and could not be removed: {string.Join("; ", left)}"); return 2; }
// Without a station address nothing is polled; the page and /api/health still answer and name the problem.
if (config.ExtraReadersProblem is { } badReaders) Log($"CONFIG - {badReaders}");
if (config.MacProblem is { } noMac) Log($"NOT POLLING - {noMac}");
else if (!File.Exists(config.ReaderPath)) { Log($"DID NOT RUN - no reader at {config.ReaderPath} (install.ps1 builds the venv)"); return 2; }

using var store = new StationStore(Path.Combine(config.DataDir, "station.db"));
var runner = new ProcessRunner();
var time = TimeProvider.System;
if (config.MutexWarning is { } mutexWarning)
{
    Log($"WARNING - {mutexWarning}");
    store.AddEvent(time.GetLocalNow(), "config-warning", mutexWarning);
}
// The wake of a PC hibernated in a cut: armed only with wakeEveryMinutes > 0; turned off at the first reading on mains whatever it says.
var wake = new WakeKeeper(config, new ScheduledWakeTask(config, runner, Log), store, Log);
// Bluetooth first; when it misses, the BLUETTI cloud (design.md §4 Cloud fallback), on when its token file exists at start unless cloudEnabled is false.
using var cloudHttp = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(10) };
using var cloud = config.CloudOn ? new BluettiCloudReader(config, cloudHttp, time, Log) : null;
using var service = new StationService(config, new FallbackReader(new BluettiReader(config, runner, Log), cloud), new PowerModeAdapter(config, runner, Log),
    config.DryRun ? new DryRunHibernator(Log) : new ShutdownHibernator(runner, Log),
    new FlagFileReader(config.FlagFile, Log, config.PowerModeMutex), store, time, Log, wake, new StateActions(config, runner, store, time, Log));
service.Compact();

var started = time.GetLocalNow();
// The outage schedule: its own task and cadence, never on the poll path; off (no source, no request) unless dtekWatcher.enabled.
// The guard is the module's whole boundary: no call into it ever throws into this loop or into /api/now.
using var dtek = DtekWatcherGuard.Create(config, store, time, Log);
var api = new StationApi(config, service, store, time, started, dtek, Log);
using var http = new MiniHttpServer(IPAddress.Loopback, config.Port, api.HandleAsync, Log);
try { http.Start(); }
catch (SocketException e) { Log($"DID NOT RUN - 127.0.0.1:{config.Port} is taken ({e.SocketErrorCode})"); return 2; }

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => stop.Cancel();
Log($"Bluetti Watcher started: http://127.0.0.1:{config.Port}/ · reader {config.ReaderPath} · data {config.DataDir}{(cloud is null ? "" : " · cloud fallback")}{(config.DryRun ? " · DRY RUN" : "")}");

var serving = http.RunAsync(stop.Token);
var scheduleReads = dtek.RunAsync(stop.Token);
long lastCompact = time.GetTimestamp();
try
{
    // The loop's own timing is monotonic: a wall-clock step never stalls polling. Readings keep their wall time.
    var schedule = new PollSchedule(time);
    while (!stop.IsCancellationRequested)
    {
        if (schedule.Due())
        {
            int delay;
            if (config.MacProblem is not null) delay = config.PollSec;   // no station to poll: the loop only idles
            else try { delay = await service.PollAsync(stop.Token); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // One failed poll (a locked database, a process that would not start) never stops the service.
                Log($"poll failed: {e.GetType().Name}: {e.Message}");
                delay = config.PollSec;
            }
            schedule.Next(delay);
            dtek.AfterPoll(service.State);   // the finished cut goes to the queue search; nothing comes back
        }
        // A page choice saved by POST /api/mode runs here, with the power-mode timeout, never the request's.
        try { await service.ApplyPendingAsync(stop.Token); }
        catch (Exception e) when (e is not OperationCanceledException) { Log($"mode change failed: {e.GetType().Name}: {e.Message}"); }
        if (time.GetElapsedTime(lastCompact).TotalHours >= 1) { service.Compact(); lastCompact = time.GetTimestamp(); }
        await service.WaitForWorkAsync(schedule.Wait, stop.Token);
    }
}
catch (OperationCanceledException)
{
    Log("stopping");
}
await serving;
await scheduleReads;
Log("stopped");
return 0;
