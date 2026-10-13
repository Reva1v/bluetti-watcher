using System.Net;
using System.Text.Json;
using BluettiWatcher.Io;
using static BluettiWatcher.Tests.Fx;

namespace BluettiWatcher.Tests;

/// <summary><c>--cloud-login</c>: the authorize address, the code exchange and the token file, against canned answers only.</summary>
public sealed class CloudLoginTests : IDisposable
{
    private const string Access = "ACCESS-LOGIN-5d1e";
    private const string Refresh = "REFRESH-LOGIN-c2a9";
    private const string TokenUrl = "https://sso.bluettipower.com/oauth2/token";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bluetti-login-test-" + Guid.NewGuid().ToString("N"));
    private readonly CannedHandler _http = new();
    private readonly List<string> _printed = [];
    private readonly ManualClock _clock = new(T0);

    public CloudLoginTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (var line in _printed)
        {
            Assert.DoesNotContain(Access, line, StringComparison.Ordinal);
            Assert.DoesNotContain(Refresh, line, StringComparison.Ordinal);
        }
        Assert.All(_http.Calls, c => Assert.Equal(TokenUrl, c.Url));
        _http.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private StationConfig Config() => new() { DataDir = _dir };

    private CloudLogin Login(Action<Uri> browser) => new(Config(), _http, _clock, l => { lock (_printed) _printed.Add(l); }, browser);

    private static string TokenAnswer(string? refresh = Refresh) =>
        $$"""{ "access_token": "{{Access}}", {{(refresh is null ? "" : $"\"refresh_token\": \"{refresh}\", ")}}"token_type": "bearer", "expires_in": 2592000 }""";

    [Fact]
    public void Cloud_login_takes_the_token_file_from_the_data_dir()
    {
        var cfg = StationConfig.Load(["--cloud-login", "--data-dir", _dir]);
        Assert.True(StationConfig.ParseArgs(["--cloud-login", "--data-dir", _dir]).ContainsKey("cloud-login"));
        Assert.Equal(Path.Combine(_dir, "cloud-token.json"), cfg.CloudTokenFile);
    }

    [Fact]
    public void Authorize_address_is_the_integrations_grant_endpoint_with_the_code_flow()
    {
        var url = CloudLogin.BuildAuthorizeUrl("http://127.0.0.1:50123/cb", "abc123");
        Assert.Equal("https://sso.bluettipower.com/oauth2/grant?response_type=code&client_id=HomeAssistant&redirect_uri=http%3A%2F%2F127.0.0.1%3A50123%2Fcb&state=abc123", url.AbsoluteUri);
    }

    [Fact]
    public async Task Code_exchange_posts_the_authorization_code_grant_and_reads_the_pair()
    {
        _http.Json(TokenAnswer());
        var (token, why) = await Login(_ => { }).ExchangeAsync("CODE-1", "http://127.0.0.1:50123/cb", CancellationToken.None);
        Assert.Equal("", why);
        Assert.Equal((Access, Refresh, 2_592_000L, T0.ToUnixTimeSeconds()), (token!.Access, token.Refresh, token.ExpiresIn, token.CreatedAt));
        var call = Assert.Single(_http.Calls);
        Assert.Equal(HttpMethod.Post, call.Method);
        var form = call.Body.Split('&').Select(kv => kv.Split('=')).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
        Assert.Equal("authorization_code", form["grant_type"]);
        Assert.Equal("CODE-1", form["code"]);
        Assert.Equal("http://127.0.0.1:50123/cb", form["redirect_uri"]);
        Assert.Equal("HomeAssistant", form["client_id"]);
        Assert.Equal("SG9tZUFzc2lzdGFudA==", form["client_secret"]);
    }

    [Fact]
    public async Task Code_exchange_reads_a_pair_wrapped_in_data()
    {
        _http.Json($$"""{ "msgCode": 0, "data": {{TokenAnswer()}} }""");
        var (token, _) = await Login(_ => { }).ExchangeAsync("C", "http://127.0.0.1:1/cb", CancellationToken.None);
        Assert.Equal(Access, token!.Access);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "{}", "the token request answered HTTP 400")]
    [InlineData(HttpStatusCode.OK, "not json", "the token answer is not JSON")]
    [InlineData(HttpStatusCode.OK, """{ "error": "invalid_grant" }""", "the token answer has no access token")]
    public async Task Failed_exchange_names_the_kind_only(HttpStatusCode status, string body, string why)
    {
        _http.Json(body, status);
        var (token, said) = await Login(_ => { }).ExchangeAsync("C", "http://127.0.0.1:1/cb", CancellationToken.None);
        Assert.Null(token);
        Assert.Equal(why, said);
    }

    [Fact]
    public async Task A_token_answer_with_an_expires_in_that_is_not_a_number_is_refused()
    {
        _http.Json("""{ "access_token": "A", "refresh_token": "R", "expires_in": "soon" }""");
        var (token, why) = await Login(_ => { }).ExchangeAsync("C", "http://127.0.0.1:1/cb", CancellationToken.None);
        Assert.Null(token);
        Assert.Equal("the token answer has no valid expires_in", why);
    }

    [Theory]
    [InlineData("""{ "access_token": 12345, "refresh_token": "R", "expires_in": "soon" }""")]
    [InlineData("""{ "access_token": "A", "refresh_token": "R", "expires_in": "soon" }""")]
    [InlineData("""{ "access_token": "A", "refresh_token": "R", "expires_in": 9223372036854775807 }""")]
    [InlineData("""{ "access_token": "A", "refresh_token": "R", "expires_in": -9223372036854775808 }""")]
    [InlineData("""{ "access_token": "A", "refresh_token": "R", "expires_in": 1e400 }""")]
    [InlineData("""{ "access_token": ["A"], "refresh_token": {"x": 1}, "expires_in": [1] }""")]
    [InlineData("""{ "data": "A" }""")]
    [InlineData("""{ "data": [] }""")]
    [InlineData("""[1, 2]""")]
    [InlineData("""null""")]
    [InlineData("""12""")]
    public async Task A_token_answer_of_the_wrong_types_ends_in_a_message_or_a_token_never_an_exception(string body)
    {
        _http.Json(body);
        var (token, why) = await Login(_ => { }).ExchangeAsync("C", "http://127.0.0.1:1/cb", CancellationToken.None);
        Assert.True(token is not null || why.StartsWith("the token answer", StringComparison.Ordinal), why);
        if (token is not null) _ = token.ExpiresAt;
    }

    [Fact]
    public async Task Answer_without_a_refresh_token_is_refused()
    {
        _http.Json(TokenAnswer(refresh: null));
        var (token, why) = await Login(_ => { }).ExchangeAsync("C", "http://127.0.0.1:1/cb", CancellationToken.None);
        Assert.Null(token);
        Assert.Equal("the token answer has no refresh token", why);
    }

    /// <summary>The browser's part: follows the address to the catcher with a code (or an error) and the given state.</summary>
    private static Action<Uri> Browser(string query, bool keepState = true) => url =>
    {
        var q = url.Query.TrimStart('?').Split('&').Select(kv => kv.Split('=')).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
        string state = keepState ? q["state"] : "forged";
        _ = Task.Run(async () =>
        {
            using var client = new HttpClient();
            using var _ = await client.GetAsync(new Uri($"{q["redirect_uri"]}?{query}&state={state}"));
        });
    };

    [Fact]
    public async Task Login_catches_the_code_and_writes_the_token_file_in_the_readers_format()
    {
        _http.Json(TokenAnswer());
        int exit = await Login(Browser("code=CODE-9")).RunAsync(CancellationToken.None);
        Assert.Equal(0, exit);
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(_dir, "cloud-token.json")));
        var t = doc.RootElement;
        Assert.Equal(Access, t.GetProperty("access_token").GetString());
        Assert.Equal(Refresh, t.GetProperty("refresh_token").GetString());
        Assert.Equal("bearer", t.GetProperty("token_type").GetString());
        Assert.Equal(2_592_000, t.GetProperty("expires_in").GetInt64());
        Assert.Equal(T0.ToUnixTimeSeconds(), t.GetProperty("created_at").GetInt64());
        Assert.False(File.Exists(Path.Combine(_dir, "cloud-token.json.tmp")));
        Assert.Equal($"token saved to {Path.Combine(_dir, "cloud-token.json")}, expires 05.11.2026 20:00", _printed[^1]);
        Assert.Contains("CODE-9", _http.Calls.Single().Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Login_refused_in_the_browser_writes_nothing()
    {
        int exit = await Login(Browser("error=access_denied")).RunAsync(CancellationToken.None);
        Assert.Equal(1, exit);
        Assert.Equal("login failed: the login answered: access_denied", _printed[^1]);
        Assert.False(File.Exists(Path.Combine(_dir, "cloud-token.json")));
        Assert.Empty(_http.Calls);
    }

    [Fact]
    public async Task A_callback_with_a_foreign_state_is_ignored()
    {
        var login = Login(Browser("code=CODE-X", keepState: false));
        login.Wait = TimeSpan.FromSeconds(3);
        int exit = await login.RunAsync(CancellationToken.None);
        Assert.Equal(1, exit);
        Assert.StartsWith("login failed: no answer from the browser", _printed[^1], StringComparison.Ordinal);
        Assert.Empty(_http.Calls);
    }
}
