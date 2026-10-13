using System.Text.Json;
using System.Text.Json.Serialization;

namespace BluettiWatcher.DtekWatcher;

/// <summary>
/// <c>dtekWatcher</c> in config.json. The house's queues and address live only in the data directory.
/// Decision code never reads this (design.md §11 rule 2).
/// </summary>
internal sealed class DtekConfig
{
    public const string DefaultHost = "www.dtek-krem.com.ua";
    public const string DefaultDatasetUrl = "https://raw.githubusercontent.com/Baskerville42/outage-data-ua/main/data/kyiv-region.json";

    /// <summary>false = the module is off and no request is ever made.</summary>
    public bool Enabled { get; set; }
    /// <summary>The primary source: a public dataset with the same schedule as the site's page.</summary>
    public string DatasetUrl { get; set; } = DefaultDatasetUrl;
    /// <summary>The schedule is stale when its newest collection is older than this.</summary>
    public int StaleAfterMin { get; set; } = 90;
    /// <summary>The house's queues such as GPV1.1; without them the site's address answer names them.</summary>
    public string[]? Queues { get; set; }
    /// <summary>
    /// The house's queue, a hint the real cuts can refute: one queue (<c>"GPV1.1"</c>) or a set the house loses power for
    /// all of (<c>["GPV1.1", "GPV2.1"]</c>, held as <c>"GPV1.1+GPV2.1"</c>: its planned outage is the union).
    /// </summary>
    [JsonConverter(typeof(QueueSetConverter))]
    public string? Group { get; set; }
    /// <summary>false (the default) = the distributor's site is never asked; true = it is read as a browser would.</summary>
    public bool Site { get; set; }
    /// <summary>The site's host: with the dataset's, the only hosts a request may go to.</summary>
    public string Host { get; set; } = DefaultHost;
    /// <summary>The address, for the site only; optional.</summary>
    public string City { get; set; } = "";
    public string Street { get; set; } = "";
    public string House { get; set; } = "";

    /// <summary>The site can be asked about the house.</summary>
    public bool HasAddress => !string.IsNullOrWhiteSpace(City) && !string.IsNullOrWhiteSpace(Street) && !string.IsNullOrWhiteSpace(House);

    /// <summary>The dataset's address; null when <see cref="DatasetUrl"/> is no plain https one.</summary>
    public Uri? DatasetUri() =>
        Uri.TryCreate(DatasetUrl, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && u.IsDefaultPort && u.UserInfo.Length == 0 ? u : null;

    /// <summary>The most queues a set in <see cref="Group"/> may name.</summary>
    public const int MaxSet = 4;

    /// <summary>Why an enabled module cannot run; null when it can.</summary>
    public string? Problem()
    {
        if (DatasetUri() is null) return "dtekWatcher.datasetUrl must be an https address";
        if (Group == QueueSetConverter.Unreadable) return "dtekWatcher.group must be a queue name or an array of queue names";
        if (Group is { } g && DtekTime.Members(g) is { } members && (members.Count > MaxSet || members.Any(m => m.Length > DtekSnapshot.MaxName)))
            return $"dtekWatcher.group takes at most {MaxSet} queues of at most {DtekSnapshot.MaxName} characters";
        return Uri.CheckHostName(Host) == UriHostNameType.Dns ? null : "dtekWatcher.host must be a plain host name";
    }
}

/// <summary>
/// <c>dtekWatcher.group</c>: a string as it is, an array of strings joined with <see cref="DtekTime.SetJoin"/> (blanks and
/// repeats dropped), null as null. Any other value (a number, an object, a null or a number in the array) reads as
/// <see cref="Unreadable"/>: the module is off with the reason (design.md §11 rule 8), the service runs on.
/// </summary>
internal sealed class QueueSetConverter : JsonConverter<string?>
{
    /// <summary>A group value that is no queue name and no array of them; <see cref="DtekConfig.Problem"/> names it.</summary>
    public const string Unreadable = "\u0001unreadable";

    public override bool HandleNull => true;

    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null: return null;
            case JsonTokenType.String: return reader.GetString();
            case JsonTokenType.StartArray:
                var names = new List<string>();
                bool sound = true;
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    if (reader.TokenType != JsonTokenType.String)
                    {
                        sound = false;
                        reader.Skip();   // a nested array or object is passed over whole
                        continue;
                    }
                    if (reader.GetString() is { } name && !string.IsNullOrWhiteSpace(name) && !names.Contains(name.Trim(), StringComparer.Ordinal)) names.Add(name.Trim());
                }
                return !sound ? Unreadable : names.Count == 0 ? null : string.Join(DtekTime.SetJoin, names);
            default:
                reader.Skip();
                return Unreadable;
        }
    }

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (value is null) { writer.WriteNullValue(); return; }
        var members = DtekTime.Members(value);
        if (members.Count < 2) { writer.WriteStringValue(value); return; }
        writer.WriteStartArray();
        foreach (var m in members) writer.WriteStringValue(m);
        writer.WriteEndArray();
    }
}
