using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using BluettiWatcher.Http;

namespace BluettiWatcher.Io;

/// <summary>
/// <c>--cloud-login</c>: the OAuth authorization-code login of the official BLUETTI Home Assistant integration (its
/// client, its authorize and token endpoints), with a one-shot catcher on 127.0.0.1 as the redirect. The token goes into
/// the token file the cloud reader uses, written atomically; no token is ever printed or logged.
/// </summary>
internal sealed class CloudLogin(StationConfig config, HttpMessageHandler handler, TimeProvider time, Action<string> print, Action<Uri> openBrowser)
{
    internal const string AuthorizeUrl = "https://sso.bluettipower.com/oauth2/grant";
    internal const string CallbackPath = "/cb";

    /// <summary>How long the catcher waits for the browser to come back.</summary>
    public TimeSpan Wait { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>The cap on the token request.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    internal static Uri BuildAuthorizeUrl(string redirectUri, string state) => new(
        $"{AuthorizeUrl}?response_type=code&client_id={Uri.EscapeDataString(BluettiCloudReader.ClientId)}"
        + $"&redirect_uri={Uri.EscapeDataString(redirectUri)}&state={Uri.EscapeDataString(state)}");

    /// <summary>0 = token saved · 1 = the login or the exchange failed · 2 = could not run.</summary>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        string state = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var answer = new TaskCompletionSource<(string? Code, string Why)>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<HttpResponse> Catch(HttpRequest r, CancellationToken _)
        {
            if (r.Path != CallbackPath) return Task.FromResult(HttpResponse.Text(404, "not found"));
            if (!r.Query.TryGetValue("state", out var s) || s != state) return Task.FromResult(HttpResponse.Text(400, "this login was not started here"));
            if (r.Query.TryGetValue("code", out var code) && code.Length > 0)
            {
                answer.TrySetResult((code, ""));
                return Task.FromResult(HttpResponse.Text(200, "Bluetti Watcher: signed in. You can close this tab."));
            }
            string error = r.Query.TryGetValue("error", out var e) ? e : "no code";
            answer.TrySetResult((null, $"the login answered: {error}"));
            return Task.FromResult(HttpResponse.Text(400, $"Bluetti Watcher: the login failed ({error})."));
        }

        using var server = new MiniHttpServer(IPAddress.Loopback, 0, Catch, _ => { });
        try { server.Start(); }
        catch (System.Net.Sockets.SocketException e)
        {
            print($"COULD NOT RUN: no local port for the login ({e.SocketErrorCode})");
            return 2;
        }
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var serving = server.RunAsync(stop.Token);
        try
        {
            string redirect = string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{server.BoundPort}{CallbackPath}");
            var url = BuildAuthorizeUrl(redirect, state);
            print($"Open this address to sign in to BLUETTI: {url}");
            openBrowser(url);
            var (code, why) = await answer.Task.WaitAsync(Wait, time, ct);
            if (code is null) { print($"login failed: {why}"); return 1; }
            var (token, failed) = await ExchangeAsync(code, redirect, ct);
            if (token is null) { print($"login failed: {failed}"); return 1; }
            string path = config.CloudTokenFile;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            BluettiCloudReader.Save(path, token);
            print($"token saved to {path}, expires {TimeZoneInfo.ConvertTime(token.ExpiresAt, time.LocalTimeZone).ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture)}");
            return 0;
        }
        catch (TimeoutException)
        {
            print(string.Create(CultureInfo.InvariantCulture, $"login failed: no answer from the browser within {Wait.TotalSeconds:0} s"));
            return 1;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            print($"login failed: the token file could not be written ({e.GetType().Name})");
            return 1;
        }
        finally
        {
            await stop.CancelAsync();
            await serving;
        }
    }

    /// <summary>The code for a token pair; the reason names the failure's kind only, never a token or the answer's text.</summary>
    internal async Task<(CloudToken? Token, string Why)> ExchangeAsync(string code, string redirectUri, CancellationToken ct)
    {
        using var http = new HttpClient(handler, disposeHandler: false) { Timeout = System.Threading.Timeout.InfiniteTimeSpan, MaxResponseContentBufferSize = 1 << 20 };
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(BluettiCloudReader.TokenUrl))
        {
            Content = new FormUrlEncodedContent([
                new("grant_type", "authorization_code"), new("code", code), new("redirect_uri", redirectUri),
                new("client_id", BluettiCloudReader.ClientId), new("client_secret", BluettiCloudReader.ClientSecret)]),
        };
        using var cap = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cap.CancelAfter(Timeout);
        string body;
        try
        {
            using var resp = await http.SendAsync(req, cap.Token);
            if (resp.StatusCode != HttpStatusCode.OK) return (null, string.Create(CultureInfo.InvariantCulture, $"the token request answered HTTP {(int)resp.StatusCode}"));
            body = await resp.Content.ReadAsStringAsync(cap.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return (null, "the token request timed out"); }
        catch (HttpRequestException e) { return (null, $"the token request failed ({e.HttpRequestError})"); }
        try
        {
            using var doc = JsonDocument.Parse(body);
            var token = BluettiCloudReader.Renewed(doc.RootElement, new CloudToken("", "", "bearer", 0, 0), time.GetUtcNow().ToUnixTimeSeconds());
            if (token is null) return (null, "the token answer has no access token");
            if (token.Refresh.Length == 0) return (null, "the token answer has no refresh token");
            if (token.ExpiresIn <= 0) return (null, "the token answer has no valid expires_in");
            return (token, "");
        }
        catch (JsonException) { return (null, "the token answer is not JSON"); }
    }
}
