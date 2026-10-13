using System.Globalization;
using System.Text;

namespace BluettiWatcher.Io;

/// <summary>service.log in the data directory, one line per event; rolled to service.log.1 past 1 MB.</summary>
internal sealed class FileLog(string path)
{
    private readonly Lock _gate = new();
    private static readonly UTF8Encoding Utf8 = new(false);

    public void Write(string text)
    {
        string line = DateTimeOffset.Now.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture) + "  " + text + "\n";
        lock (_gate)
        {
            try
            {
                var fi = new FileInfo(path);
                if (fi.Exists && fi.Length > 1_000_000) File.Move(path, path + ".1", overwrite: true);
                File.AppendAllText(path, line, Utf8);
            }
            catch (IOException)
            {
                // The log is best effort: a locked log file never stops a poll. Nothing else to report it to.
                return;
            }
        }
    }
}
