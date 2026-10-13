using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace BluettiWatcher.Tests;

/// <summary>The page's language switch: every text key the page uses exists in English and in Ukrainian.</summary>
public sealed partial class PageI18nTests
{
    private static string Page([CallerFilePath] string here = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "src", "BluettiWatcher", "Web", "station.html")));

    [GeneratedRegex(@"^\s+(\w+):\s""", RegexOptions.Multiline)]
    private static partial Regex DictKey();

    [GeneratedRegex(@"\bt\('(\w+)'")]
    private static partial Regex ScriptKey();

    [GeneratedRegex(@"\bt\([^()]*?\?\s*'(\w+)'\s*:\s*'(\w+)'")]
    private static partial Regex ChosenKey();

    [GeneratedRegex(@"data-i18n(?:-title|-aria)?=""(\w+)""")]
    private static partial Regex MarkupKey();

    [GeneratedRegex(@">([^<]*)<")]
    private static partial Regex TextNode();

    /// <summary>The page's I18N block split at its Ukrainian dictionary: (start, uk, end) offsets into a LF-normalised page.</summary>
    private static (int Start, int Uk, int End) Blocks(string page)
    {
        int start = page.IndexOf("const I18N = {", StringComparison.Ordinal);
        int uk = page.IndexOf("\nuk: {", Math.Max(0, start), StringComparison.Ordinal);
        int end = uk < 0 ? -1 : page.IndexOf("\n};", uk, StringComparison.Ordinal);
        Assert.True(start >= 0 && uk > start && end > uk, "the I18N block is not where the test reads it");
        return (start, uk, end);
    }

    private static (HashSet<string> En, HashSet<string> Uk) Dictionaries(string page)
    {
        var (start, uk, end) = Blocks(page);
        HashSet<string> Keys(string block) => DictKey().Matches(block).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        return (Keys(page[start..uk]), Keys(page[uk..end]));
    }

    [Fact]
    public void Every_key_the_page_uses_exists_in_both_languages()
    {
        string page = Page().Replace("\r\n", "\n", StringComparison.Ordinal);
        var (en, uk) = Dictionaries(page);
        Assert.True(en.Count > 50, $"only {en.Count} English keys read");
        var used = ScriptKey().Matches(page).Select(m => m.Groups[1].Value)
            .Concat(ChosenKey().Matches(page).SelectMany(m => new[] { m.Groups[1].Value, m.Groups[2].Value }))
            .Concat(MarkupKey().Matches(page).Select(m => m.Groups[1].Value))
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("fromMeasured", used);   // the reader sees the script's calls
        Assert.Contains("modeAutoTip", used);    // and the markup's attributes
        Assert.Empty(used.Where(k => !en.Contains(k)).Order());
        Assert.Empty(used.Where(k => !uk.Contains(k)).Order());
        Assert.Equal(en.Order(), uk.Order());
    }

    [Fact]
    public void Every_code_the_service_sends_has_a_key_in_both_languages()
    {
        var (en, uk) = Dictionaries(Page().Replace("\r\n", "\n", StringComparison.Ordinal));
        var keys = PageCodes.Keys().ToList();
        Assert.Contains("n_max_held", keys);
        Assert.Contains("om_INVERTER_OUTPUT", keys);
        Assert.DoesNotContain(keys, k => !en.Contains(k));
        Assert.DoesNotContain(keys, k => !uk.Contains(k));
        // the list the keys come from carries every code constant
        var consts = typeof(PageCodes).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!).Order(StringComparer.Ordinal);
        Assert.Equal(consts, PageCodes.Notes.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("Max held back: battery emergency", "max-held", "")]
    [InlineData("Eco held back: battery emergency", "eco-held", "")]
    [InlineData("Auto: eco not lifted: battery emergency", "auto-held", "")]
    [InlineData("Eco dropped: mains came back before it ran", "mode-dropped-back", "Eco")]
    [InlineData("Max dropped: mains was lost before it ran", "mode-dropped-lost", "Max")]
    [InlineData("Max dropped: something new", "mode-dropped", "Max|something new")]
    [InlineData("Auto: the power-mode flag is unreadable - nothing lifted", "flag-unreadable", "Auto")]
    [InlineData("Eco: exit 1: boom", "mode-failed", "Eco|exit 1: boom")]
    [InlineData("anything else", "mode-other", "anything else")]
    public void A_saved_mode_error_gets_its_code(string error, string code, string args)
    {
        var n = Http.StationApi.ModeErrorNote(error);
        Assert.NotNull(n);
        Assert.Equal((code, error, args), (n.Code, n.Text, string.Join('|', n.Args)));
        Assert.Contains(code, PageCodes.Notes);
        Assert.Null(Http.StationApi.ModeErrorNote(""));
    }

    [GeneratedRegex(@"^\s+(\w+):\s""((?:[^""\\]|\\.)*)""", RegexOptions.Multiline)]
    private static partial Regex DictEntry();

    /// <summary>
    /// Rough width of a 12 px Segoe UI label in pixels: per-character advances, a little generous so a label that passes
    /// fits the browser's rendering.
    /// </summary>
    internal static double LabelWidth(string text)
    {
        double w = 0;
        foreach (char ch in text)
        {
            w += ch switch
            {
                ' ' => 3.3, '.' or ',' or ':' or '·' => 3.0, '(' or ')' => 3.8, '-' => 4.0, '%' => 9.6, >= '0' and <= '9' => 6.7,
                'i' or 'j' or 'l' => 2.8, 'f' or 't' or 'r' => 4.0, 's' => 5.2, 'm' => 9.7, 'w' => 8.7, 'I' => 3.2, 'M' or 'W' => 10.5,
                'ж' or 'ш' or 'щ' or 'ю' or 'м' => 8.8, 'т' or 'г' => 5.4, 'Ж' or 'Ш' or 'Щ' or 'Ю' or 'М' => 10.5,
                >= 'a' and <= 'z' => 6.3, >= 'A' and <= 'Z' => 7.7, >= 'а' and <= 'я' => 6.6, >= 'А' and <= 'Я' => 8.0,
                _ => 7.0,
            };
        }
        return w;
    }

    // The labels of the DTEK and cycles rows (a 150 px column) and of the load statistics (110 px), in both languages.
    [Theory]
    [InlineData(150, "queue,schedule,sources,nextOutage,plannedReturn,battery,expectedReturn,plan,pastCuts,siteSays,cycBase,cycCount,cycAvgLen,cycAvgExtra,cycShare,cycNow")]
    [InlineData(110, "statBase,statTypical,statAvg,statPeak,statOnTop,statEnergy")]
    public void Row_labels_fit_their_column_in_both_languages(int column, string keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        string page = Page().Replace("\r\n", "\n", StringComparison.Ordinal);
        var (start, ukAt, end) = Blocks(page);
        Dictionary<string, string> Entries(string block) => DictEntry().Matches(block).ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value, StringComparer.Ordinal);
        var en = Entries(page[start..ukAt]);
        var uk = Entries(page[ukAt..end]);
        var tooWide = keys.Split(',').SelectMany(k => new[] { en[k], uk[k] }).Where(text => LabelWidth(text) > column - 2)
            .Select(text => $"{text} ({LabelWidth(text):0} px)").ToList();
        Assert.Empty(tooWide);
        Assert.True(LabelWidth("Наступне планове відключення") > 150, "the estimate is blind to a label known to truncate");
    }

    /// <summary>The four Russian letters Ukrainian does not have, both cases, written as escapes so this file holds none.</summary>
    private static readonly System.Buffers.SearchValues<char> NotUkrainian = System.Buffers.SearchValues.Create("\u044B\u042B\u044D\u042D\u0451\u0401\u044A\u042A");

    [Fact]
    public void The_page_holds_no_letter_Ukrainian_lacks()
    {
        var lines = Page().Split('\n');
        var bad = lines.Select((line, i) => (line, n: i + 1)).Where(x => x.line.AsSpan().IndexOfAny(NotUkrainian) >= 0)
            .Select(x => $"line {x.n}: {x.line.Trim()[..Math.Min(60, x.line.Trim().Length)]}").ToList();
        Assert.Empty(bad);
        Assert.True("\u0438\u0437\u0020\u044D\u0442\u043E\u0433\u043E".AsSpan().IndexOfAny(NotUkrainian) >= 0, "the letter list finds nothing");
    }

    /// <summary>Russian words (as escapes: no, now, this, what, for, all) that Ukrainian spells differently or not at all.</summary>
    [GeneratedRegex("(?<!\\p{L})(\u043D\u0435\u0442|\u0441\u0435\u0439\u0447\u0430\u0441|\u044D\u0442\u043E|\u0447\u0442\u043E|\u0434\u043B\u044F|\u0432\u0441\u0451|\u0432\u0441\u0435)(?!\\p{L})", RegexOptions.IgnoreCase)]
    private static partial Regex RussianWord();

    [Fact]
    public void The_Ukrainian_dictionary_holds_no_Russian_word()
    {
        string page = Page().Replace("\r\n", "\n", StringComparison.Ordinal);
        var (_, uk, end) = Blocks(page);
        var bad = RussianWord().Matches(page[uk..end]).Select(m => m.Value).ToList();
        Assert.Empty(bad);
        // controls: the pattern finds a Russian sentence and passes the Ukrainian words that look alike
        Assert.Equal(2, RussianWord().Count("\u044D\u0442\u043E \u0441\u0435\u0439\u0447\u0430\u0441, \u043D\u0435 \u0432\u0441\u0435\u0433\u043E"));
        Assert.Empty(RussianWord().Matches("\u0443\u0441\u0435 \u0449\u043E \u043D\u0456\u0447\u043E\u0433\u043E"));
    }

    /// <summary>Runs page script under node (stdin), in the time zone given.</summary>
    private static string RunNode(string script, string? timeZone = null)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(NodeFactAttribute.NodePath()!, "-")
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };
        if (timeZone is not null) psi.Environment["TZ"] = timeZone;
        using var p = System.Diagnostics.Process.Start(psi)!;
        p.StandardInput.Write(script);
        p.StandardInput.Close();
        var err = p.StandardError.ReadToEndAsync();
        string output = p.StandardOutput.ReadToEnd();
        Assert.True(p.WaitForExit(30_000), "node did not finish");
        Assert.True(p.ExitCode == 0, $"node failed: {err.Result}");
        return output.Trim();
    }

    /// <summary>The text of a top-level function of the page, from its first line to the closing brace at column 0.</summary>
    private static string FunctionText(string page, string name)
    {
        var m = Regex.Match(page.Replace("\r\n", "\n", StringComparison.Ordinal), $@"^function {name}\(.*?^\}}$", RegexOptions.Multiline | RegexOptions.Singleline);
        Assert.True(m.Success, $"function {name} is not where the test reads it");
        return m.Value;
    }

    private sealed record LangCase(string? Stored, bool GetThrows, bool SetThrows, string? Browser, string ExpectLang, string? ExpectWritten);

    [NodeFact]
    public void The_language_defaults_to_the_browser_and_remembers_the_choice()
    {
        string page = Page();
        // two buttons, English and Ukrainian, and no Russian
        Assert.Contains("<button data-lang=\"en\" data-i18n=\"langEn\"></button><button data-lang=\"uk\" data-i18n=\"langUk\"></button>", page, StringComparison.Ordinal);
        Assert.DoesNotContain("data-lang=\"ru\"", page, StringComparison.Ordinal);
        Assert.DoesNotContain("langRu", page, StringComparison.Ordinal);
        Assert.Contains("localStorage.setItem('station.lang'", page, StringComparison.Ordinal);   // the switch writes the choice

        // the page's own startLang, run with a fake localStorage and navigator
        var cases = new LangCase[]
        {
            new("ru", false, false, "en-US", "uk", "uk"),          // an older Russian choice becomes Ukrainian and is written back
            new("ru", false, true, "en-US", "uk", null),           // ... and a failing write changes nothing else
            new("en", false, false, "uk-UA", "en", null),          // a stored choice beats the browser
            new("uk", false, false, "en-US", "uk", null),
            new(null, false, false, "uk", "uk", null),
            new(null, false, false, "uk-UA", "uk", null),
            new(null, false, false, "UK-ua", "uk", null),
            new(null, false, false, "ru-RU", "en", null),          // Russian browsers get English
            new(null, false, false, "en-US", "en", null),
            new(null, false, false, null, "en", null),             // no language at all
            new("de", false, false, "uk", "uk", null),             // an unknown stored value is ignored
            new(null, true, false, "uk-UA", "uk", null),           // a throwing localStorage (private window)
            new(null, true, true, "en-US", "en", null),
            new("ru", true, true, "uk", "uk", null),
        };
        string json = System.Text.Json.JsonSerializer.Serialize(cases.Select(c => new { stored = c.Stored, get = c.GetThrows, set = c.SetThrows, nav = c.Browser }));
        string script = $$"""
            const src = {{System.Text.Json.JsonSerializer.Serialize(FunctionText(page, "startLang"))}};
            const cases = {{json}};
            const out = cases.map((c) => {
              const writes = [];
              const ls = {
                getItem: () => { if (c.get) throw new Error('private'); return c.stored; },
                setItem: (k, v) => { if (c.set) throw new Error('private'); writes.push(k + '=' + v); },
              };
              const nav = c.nav === null ? {} : { language: c.nav };
              return { lang: new Function('localStorage', 'navigator', src + '; return startLang();')(ls, nav), written: writes.length ? writes.join(',') : null };
            });
            console.log(JSON.stringify(out));
            """;
        using var doc = System.Text.Json.JsonDocument.Parse(RunNode(script));
        var got = doc.RootElement.EnumerateArray().Select(e => (e.GetProperty("lang").GetString(), e.GetProperty("written").GetString())).ToList();
        var want = cases.Select(c => (c.ExpectLang, c.ExpectWritten is null ? null : "station.lang=" + c.ExpectWritten)).ToList();
        for (int i = 0; i < cases.Length; i++)
            Assert.True(want[i] == got[i], $"case {i} {cases[i]}: expected {want[i]}, got {got[i]}");
    }

    [NodeFact]
    public void The_eco_countdown_shows_the_browser_clock()
    {
        string page = Page().Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.DoesNotContain("kyivHm(at)", page, StringComparison.Ordinal);
        string Line(string pattern)
        {
            var m = Regex.Match(page, pattern, RegexOptions.Multiline);
            Assert.True(m.Success, $"{pattern} is not where the test reads it");
            return m.Value;
        }
        // the page's own pad, hm and tick (the one the eco chip runs), with a fixed clock: 90 s before the switch at 18:30 UTC
        string pageCode = string.Join("\n", Line(@"^const pad = .*;$"), Line(@"^const hm = \(t\) => .*;$"), Line(@"^\s*const tick = \(\) => \{.*\};$"));
        string script = $$"""
            const src = {{System.Text.Json.JsonSerializer.Serialize(pageCode)}};
            const NOW = Date.UTC(2026, 9, 13, 18, 28, 30), AT = Date.UTC(2026, 9, 13, 18, 30, 0);
            const run = (now) => {
              const RealDate = Date;
              class FakeDate extends RealDate { constructor(...a) { if (a.length) super(...a); else super(now); } static now() { return now; } }
              const e = { lastChild: {} };
              const t = (k, p) => JSON.stringify([k, p]);
              new Function('e', 't', 'at', 'Date', src + '; tick();')(e, t, AT, FakeDate);
              return e.lastChild.textContent;
            };
            console.log(JSON.stringify([run(NOW), run(AT + 1000)]));
            """;
        foreach (var (zone, clock) in new[] { ("Asia/Tokyo", "03:30"), ("America/New_York", "14:30"), ("Europe/Kyiv", "21:30") })
        {
            using var doc = System.Text.Json.JsonDocument.Parse(RunNode(script, zone));
            var shown = doc.RootElement.EnumerateArray().Select(e => e.GetString()).ToList();
            Assert.Equal($$"""["ecoIn",{"m":1,"s":"30","at":"{{clock}}"}]""", shown[0]);
            Assert.Equal("""["ecoNext",null]""", shown[1]);   // the moment has passed: the next reading
        }
    }

    [NodeFact]
    public void The_schedule_rows_name_Kyiv_time_only_when_the_browser_clock_differs()
    {
        string page = Page();
        string code = FunctionText(page, "kyivZone") + "\n" + FunctionText(page, "kyivOffsetDiffers");
        string script = $$"""
            const src = {{System.Text.Json.JsonSerializer.Serialize(code)}};
            const make = (intl) => new Function('Intl', src + '; return { kyivZone, kyivOffsetDiffers };')(intl);
            // a browser that knows only the former name, and one that knows neither
            const only = (known) => ({ DateTimeFormat: function (loc, o) { if (o && o.timeZone && !known.includes(o.timeZone)) throw new RangeError('unknown zone'); return new Intl.DateTimeFormat(loc, o); } });
            const real = make(Intl);
            const win = Date.UTC(2026, 0, 15, 12), sum = Date.UTC(2026, 9, 13, 12);
            const out = {
              zone: real.kyivZone(), old: make(only(['Europe/Kiev'])).kyivZone(), none: make(only([])).kyivZone(),
              noThrow: make(only([])).kyivOffsetDiffers(sum),
              winter: real.kyivOffsetDiffers(win), summer: real.kyivOffsetDiffers(sum),
              oldBrowser: make(only(['Europe/Kiev'])).kyivOffsetDiffers(sum),
            };
            console.log(JSON.stringify(out));
            """;
        string Result(string zone, string field) => System.Text.Json.JsonDocument.Parse(RunNode(script, zone)).RootElement.GetProperty(field).ToString();

        Assert.True(Result("Asia/Tokyo", "zone") is "Europe/Kyiv" or "Europe/Kiev");
        Assert.Equal("Europe/Kiev", Result("Asia/Tokyo", "old"));   // the old name is the fallback
        Assert.Equal("", Result("Asia/Tokyo", "none"));             // null: neither known
        Assert.Equal("False", Result("Asia/Tokyo", "noThrow"));     // and nothing throws out of it
        Assert.Equal("False", Result("Europe/Kyiv", "winter"));
        Assert.Equal("False", Result("Europe/Kyiv", "summer"));
        Assert.Equal("True", Result("Asia/Tokyo", "winter"));
        Assert.Equal("True", Result("Asia/Tokyo", "summer"));
        Assert.Equal("True", Result("Europe/Berlin", "summer"));    // one hour apart
        Assert.Equal("True", Result("Europe/Istanbul", "winter"));  // +3 all year: equal to Kyiv in summer only
        Assert.Equal("False", Result("Europe/Istanbul", "summer"));
        Assert.Equal("True", Result("Asia/Tokyo", "oldBrowser"));
        // the schedule rows carry the tag, and only through that check
        Assert.Contains("kyivOffsetDiffers(Date.now()) ?", page, StringComparison.Ordinal);
        Assert.Contains("from(w.today) + kyivTag", page, StringComparison.Ordinal);
        Assert.Contains("from(w.tomorrow) + kyivTag", page, StringComparison.Ordinal);
    }

    /// <summary>A fact that runs page script under node: skipped, with the reason, when node is not on PATH.</summary>
    private sealed class NodeFactAttribute : FactAttribute
    {
        internal static string? NodePath() =>
            (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(d => Path.Combine(d, OperatingSystem.IsWindows() ? "node.exe" : "node")).FirstOrDefault(File.Exists);

        public NodeFactAttribute()
        {
            if (NodePath() is null) Skip = "node is not on PATH: this test runs the page's script under node";
        }
    }

    [Fact]
    public void The_page_shows_queues_without_the_GPV_prefix()
    {
        string page = Page();
        Assert.Contains("const qd = (q) => String(q ?? '').split('+').map((x) => x.replace(/^GPV(?=\\d)/, '')).join(' + ');", page, StringComparison.Ordinal);
        foreach (string use in new[] { "esc(qd(w.queue))", "w.queues.map(qd)", "esc(qd(w.nextQueue))", "(set ? day : `${qd(q.queue)} · ${day}`)", "row(label(w.today)", "row(label(w.tomorrow)", "val(qd(a))", "qd(i.queues.join('+'))" })
            Assert.Contains(use, page, StringComparison.Ordinal);
        Assert.DoesNotContain("esc(w.queue)", page, StringComparison.Ordinal);
    }

    [GeneratedRegex(@"const qd = \(q\) => String\(q \?\? ''\)\.split\('(.)'\)\.map\(\(x\) => x\.replace\(/(.+?)/, ''\)\)\.join\('(.+?)'\);")]
    private static partial Regex PageQd();

    [GeneratedRegex(@"const set = String\(q\.queue\)\.includes\('(.)'\);\s*const label = \(day\) => \(set \? day : `\$\{qd\(q\.queue\)\}(.+?)\$\{day\}`\);")]
    private static partial Regex PageLabel();

    /// <summary>The window-row label as the page builds it: its own qd (split, prefix pattern, join) and label code, read from station.html.</summary>
    private static string WindowLabel(string page, string queue, string day)
    {
        var qd = PageQd().Match(page);
        var label = PageLabel().Match(page);
        Assert.True(qd.Success && label.Success, "the page's qd or window-row label is not where the test reads it");
        string shown = string.Join(qd.Groups[3].Value, queue.Split(qd.Groups[1].Value[0]).Select(x => Regex.Replace(x, qd.Groups[2].Value, "")));
        return queue.Contains(label.Groups[1].Value[0], StringComparison.Ordinal) ? day : shown + label.Groups[2].Value + day;
    }

    // The rows a queue or a set of queues adds (150 px column), built by the page's own code. A set's rows carry the day alone.
    [Theory]
    [InlineData("GPV6.2", "6.2 · 08.10.2026")]
    [InlineData("GPV12.12", "12.12 · 08.10.2026")]
    [InlineData("GPV4.1+GPV6.2", "08.10.2026")]
    [InlineData("GPV12.12+GPV12.12+GPV12.12+GPV12.12", "08.10.2026")]
    public void Window_row_labels_of_a_queue_or_a_set_fit_their_column(string queue, string label)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(label);
        string page = Page();
        Assert.Equal(label, WindowLabel(page, queue, "08.10.2026"));
        Assert.True(LabelWidth(label) <= 148, $"{label} ({LabelWidth(label):0} px)");
        Assert.True(LabelWidth(WindowLabel(page, queue, "28.12.2026")) <= 148);
        // the page's qd of a set, as the queue row shows it
        var qd = PageQd().Match(page);
        Assert.Equal("4.1 + 6.2", string.Join(qd.Groups[3].Value, "GPV4.1+GPV6.2".Split(qd.Groups[1].Value[0]).Select(x => Regex.Replace(x, qd.Groups[2].Value, ""))));
    }

    [Fact]
    public void The_markup_holds_no_text_of_its_own()
    {
        string page = Page();
        int a = page.IndexOf("<main>", StringComparison.Ordinal), b = page.IndexOf("</main>", StringComparison.Ordinal);
        Assert.True(a >= 0 && b > a);
        var texts = TextNode().Matches(page[a..(b + 1)]).Select(m => m.Groups[1].Value.Trim()).Where(s => s.Any(char.IsLetter)).ToList();
        Assert.Empty(texts);
    }
}
