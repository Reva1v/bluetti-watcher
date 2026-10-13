using System.Globalization;
using System.Text.Json;

namespace BluettiWatcher.DtekWatcher;

/// <summary>
/// Pure: the schedule page -> <see cref="DtekSchedule"/>, the address answer -> <see cref="DtekHouse"/>. A challenge page
/// is "blocked" and an answer that does not fit is "format": neither is ever read as "no outages".
/// </summary>
internal static class DtekParser
{
    private const string FactName = "DisconSchedule.fact";
    private const string CsrfMeta = "name=\"csrf-token\"";
    private static readonly string[] BlockMarkers = ["_Incapsula_Resource", "Incapsula incident", "Request unsuccessful"];
    private const int MaxQueues = DtekTime.MaxQueues;
    public const int MaxUpdateText = 40;
    public const int MaxSiteText = 120;

    /// <summary>A queue name as the distributor writes it: GPV, one or two digits, a dot, one or two digits.</summary>
    public static bool IsQueue(string? name)
    {
        if (name is not { Length: >= 6 and <= 8 } || !name.StartsWith("GPV", StringComparison.Ordinal)) return false;
        int dot = name.IndexOf('.', StringComparison.Ordinal);
        return dot is 4 or 5 && name.Length - dot - 1 is 1 or 2 && name.AsSpan(3, dot - 3).IndexOfAnyExceptInRange('0', '9') < 0 && name.AsSpan(dot + 1).IndexOfAnyExceptInRange('0', '9') < 0;
    }

    /// <summary>A text of a source as it may be stored and shown: no control characters, at most <paramref name="max"/> long.</summary>
    public static string Clean(string? text, int max)
    {
        if (string.IsNullOrEmpty(text)) return "";
        string kept = string.Concat(text.Where(c => !char.IsControl(c))).Trim();
        return kept.Length <= max ? kept : kept[..max];
    }

    /// <summary>
    /// Reads the page line by line and keeps only the token and the schedule line, so the page itself is never held in
    /// memory. Stops reading once both are found.
    /// </summary>
    public static async Task<DtekPageResult> ParsePageAsync(TextReader page, CancellationToken ct)
    {
        string? csrf = null, fact = null;
        bool marker = false, any = false;
        while (await page.ReadLineAsync(ct) is { } line)
        {
            any |= line.Length > 0;
            if (csrf is null && line.Contains(CsrfMeta, StringComparison.Ordinal)) csrf = Attribute(line, "content");
            if (fact is null && line.Contains(FactName, StringComparison.Ordinal)) fact = line;
            if (!marker) marker = BlockMarkers.Any(m => line.Contains(m, StringComparison.Ordinal));
            if (csrf is not null && fact is not null) break;
        }
        if (fact is null)
        {
            // The site's own page carries the token even when the schedule moved; a challenge page carries neither.
            if (csrf is null && (marker || !any)) return new(DtekStatus.Blocked, null, null, marker ? "a challenge page instead of the schedule" : "an empty page");
            return new(DtekStatus.Format, null, csrf, $"no {FactName} on the page");
        }
        if (ParseFact(fact) is not { } schedule) return new(DtekStatus.Format, null, csrf, $"{FactName} is not the expected JSON");
        return csrf is null ? new(DtekStatus.Format, schedule, null, "no csrf-token on the page") : new(DtekStatus.Ok, schedule, csrf, "");
    }

    private static string? Attribute(string line, string name)
    {
        int meta = line.IndexOf(CsrfMeta, StringComparison.Ordinal);
        int open = line.LastIndexOf('<', meta);
        int close = line.IndexOf('>', meta);
        if (open < 0 || close < 0) return null;
        string tag = line[open..close];
        int at = tag.IndexOf(name + "=\"", StringComparison.Ordinal);
        if (at < 0) return null;
        at += name.Length + 2;
        int end = tag.IndexOf('"', at);
        return end > at ? tag[at..end] : null;
    }

    /// <summary>The object assigned on the line, cut at its closing brace (strings respected); null when it does not close.</summary>
    private static ReadOnlyMemory<char>? AssignedObject(string line)
    {
        int at = line.IndexOf(FactName, StringComparison.Ordinal) + FactName.Length;
        while (at < line.Length && char.IsWhiteSpace(line[at])) at++;
        if (at >= line.Length || line[at] != '=') return null;
        at++;
        while (at < line.Length && char.IsWhiteSpace(line[at])) at++;
        if (at >= line.Length || line[at] != '{') return null;
        int depth = 0;
        bool inString = false;
        for (int i = at; i < line.Length; i++)
        {
            char c = line[i];
            if (inString)
            {
                if (c == '\\') i++;
                else if (c == '"') inString = false;
            }
            else if (c == '"') inString = true;
            else if (c == '{') depth++;
            else if (c == '}' && --depth == 0) return line.AsMemory(at, i - at + 1);
        }
        return null;
    }

    private static DtekSchedule? ParseFact(string line)
    {
        if (AssignedObject(line) is not { } json) return null;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { return null; }
        using (doc) return ScheduleOf(doc.RootElement, "site");
    }

    /// <summary>
    /// The <c>fact</c> object, the same on the site's page and in the dataset; null when it is not that shape. Bounded: at
    /// most 4 days, at most 24 queues a day, queue names of the distributor's form only, hours 1 to 24 only. Text that is
    /// no valid string (a lone surrogate) makes the whole answer unusable.
    /// </summary>
    private static DtekSchedule? ScheduleOf(JsonElement fact, string source)
    {
        try
        {
            if (fact.ValueKind != JsonValueKind.Object || !fact.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return null;
            string update = Clean(fact.TryGetProperty("update", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : "", MaxUpdateText);
            var schedule = new DtekSchedule { Update = update, UpdateAt = ParseDate(update, DtekTime.Kyiv), Source = source };
            foreach (var day in data.EnumerateObject())
            {
                if (schedule.Days.Count >= DtekTime.MaxDays) return null;
                if (!long.TryParse(day.Name, NumberStyles.None, CultureInfo.InvariantCulture, out long start) || day.Value.ValueKind != JsonValueKind.Object) return null;
                var d = new DtekDay { Start = start };
                foreach (var queue in day.Value.EnumerateObject())
                {
                    if (queue.Value.ValueKind != JsonValueKind.Object || d.Queues.Count >= MaxQueues || !IsQueue(queue.Name)) return null;
                    foreach (var hour in queue.Value.EnumerateObject())
                    {
                        if (!int.TryParse(hour.Name, NumberStyles.None, CultureInfo.InvariantCulture, out int n) || n is < 1 or > 24) return null;
                    }
                    d.Queues[queue.Name] = Cells(queue.Value);
                }
                if (d.Queues.Count == 0) return null;
                schedule.Days.Add(d);
            }
            if (schedule.Days.Count == 0) return null;
            schedule.Days.Sort((a, b) => a.Start.CompareTo(b.Start));
            return schedule;
        }
        catch (InvalidOperationException) { return null; }   // a name or a value that is no valid text
    }

    /// <summary>
    /// The public dataset: <c>fact</c> as on the site's page, and <c>lastUpdated</c> (ISO, UTC), the moment the dataset's
    /// own collector read the site, which is the schedule's collection time.
    /// </summary>
    public static DtekDatasetResult ParseDataset(string body, string? etag)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(body); }
        catch (JsonException) { return new(DtekStatus.Format, null, null, false, "the dataset is not JSON"); }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("fact", out var fact) || ScheduleOf(fact, "dataset") is not { } schedule)
                return new(DtekStatus.Format, null, null, false, "the dataset has no fact of the expected shape");
            if (!root.TryGetProperty("lastUpdated", out var at) || at.ValueKind != JsonValueKind.String || !at.TryGetDateTimeOffset(out var collected))
                return new(DtekStatus.Format, null, null, false, "the dataset has no lastUpdated time");
            schedule.CollectedAt = collected;
            return new(DtekStatus.Ok, schedule, etag, false, "");
        }
    }

    /// <summary>24 characters, one per hour; an hour that is missing or not a known word is <c>?</c>.</summary>
    private static string Cells(JsonElement hours) => string.Create(24, hours, static (span, h) =>
    {
        for (int n = 1; n <= 24; n++)
        {
            span[n - 1] = h.TryGetProperty(n.ToString(CultureInfo.InvariantCulture), out var v) && v.ValueKind == JsonValueKind.String ? Code(v) : '?';
        }
    });

    /// <summary>The cell's code without building its string: a value that is no valid text is unknown like any other word.</summary>
    private static char Code(JsonElement cell)
    {
        foreach (var word in Words) if (cell.ValueEquals(word)) return Code(word);
        return '?';
    }

    private static readonly string[] Words = ["yes", "no", "first", "second", "maybe", "mfirst", "msecond"];

    internal static char Code(string? word) => word switch
    {
        "yes" => '1',
        "no" => '0',
        "first" => 'f',
        "second" => 's',
        "maybe" => 'm',
        "mfirst" => 'a',
        "msecond" => 'b',
        _ => '?',
    };

    /// <summary>The two half hours of a cell.</summary>
    internal static (DtekSlot First, DtekSlot Second) Halves(char code) => code switch
    {
        '1' => (DtekSlot.On, DtekSlot.On),
        '0' => (DtekSlot.Off, DtekSlot.Off),
        'f' => (DtekSlot.Off, DtekSlot.On),
        's' => (DtekSlot.On, DtekSlot.Off),
        'm' => (DtekSlot.Possible, DtekSlot.Possible),
        'a' => (DtekSlot.Possible, DtekSlot.On),
        'b' => (DtekSlot.On, DtekSlot.Possible),
        _ => (DtekSlot.Unknown, DtekSlot.Unknown),
    };

    /// <summary>The address answer: the queues of <paramref name="house"/> and its current outage, if the site names one.</summary>
    public static DtekHouseResult ParseHouse(string body, string house)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(body); }
        catch (JsonException)
        {
            bool marker = BlockMarkers.Any(m => body.Contains(m, StringComparison.Ordinal));
            return new(marker ? DtekStatus.Blocked : DtekStatus.Format, null, marker ? "a challenge page instead of the address answer" : "the address answer is not JSON");
        }
        using (doc)
        {
            try { return HouseOf(doc.RootElement, house); }
            catch (InvalidOperationException) { return new(DtekStatus.Format, null, "the address answer carries text that is no valid string"); }
        }
    }

    /// <summary>Bounded: queue names of the distributor's form only, at most 24; the outage texts cleaned and cut to 120 characters.</summary>
    private static DtekHouseResult HouseOf(JsonElement root, string house)
    {
        {
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("result", out var ok) || ok.ValueKind != JsonValueKind.True
                || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                return new(DtekStatus.Format, null, "the address answer has no result/data");
            JsonElement? found = null;
            foreach (var h in data.EnumerateObject())
            {
                if (string.Equals(h.Name.Trim(), house.Trim(), StringComparison.OrdinalIgnoreCase)) { found = h.Value; break; }
            }
            if (found is not { ValueKind: JsonValueKind.Object } e) return new(DtekStatus.Format, null, "the configured house is not in the street's answer");
            var queues = new List<string>();
            if (e.TryGetProperty("sub_type_reason", out var q) && q.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in q.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && item.GetString() is { } name && IsQueue(name) && !queues.Contains(name) && queues.Count < MaxQueues) queues.Add(name);
                }
            }
            string Text(string key) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? Clean(v.GetString(), MaxSiteText) : "";
            string kind = Text("sub_type");
            return new(DtekStatus.Ok, new DtekHouse { Queues = [.. queues], Kind = kind.Length > 0 ? kind : Text("type"), StartText = Text("start_date"), EndText = Text("end_date") }, "");
        }
    }

    private static readonly string[] DateFormats =
    [
        "HH:mm dd.MM.yyyy", "H:mm dd.MM.yyyy", "dd.MM.yyyy HH:mm", "dd.MM.yyyy H:mm", "dd.MM.yyyy HH:mm:ss",
        "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm",
    ];

    /// <summary>A date of the site as Kyiv wall time; null when it is none of the known shapes (the raw text is shown then).</summary>
    public static DateTimeOffset? ParseDate(string text, TimeZoneInfo tz)
    {
        if (DateTimeOffset.TryParseExact(text, "yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture, DateTimeStyles.None, out var exact)) return exact;
        if (!DateTime.TryParseExact(text.Trim(), DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var wall)) return null;
        return DtekTime.Resolve(wall, tz, earlier: true);
    }
}
