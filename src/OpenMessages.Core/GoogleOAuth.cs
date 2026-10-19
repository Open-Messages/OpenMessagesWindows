using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Web;

namespace OpenMessages.Core;

/// <summary>
/// "Sign in with Google" for a desktop app: authorization code flow with PKCE, opened in the
/// default browser, with the redirect caught on a one-shot loopback listener (127.0.0.1:random).
/// </summary>
public sealed class GoogleOAuth(AppConfig config)
{
    /// <summary>Returns a Google ID token for the chosen account.</summary>
    public async Task<string> SignInAsync(CancellationToken ct)
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(16));

        // TcpListener rather than HttpListener: binding 127.0.0.1 needs no URL ACL or admin rights.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var redirectUri = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/";
            var url = "https://accounts.google.com/o/oauth2/v2/auth?" + Query(new()
            {
                ["client_id"] = config.GoogleClientId,
                ["redirect_uri"] = redirectUri,
                ["response_type"] = "code",
                ["scope"] = "openid email profile",
                ["code_challenge"] = challenge,
                ["code_challenge_method"] = "S256",
                ["state"] = state,
                ["prompt"] = "select_account",
            });
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

            var query = await WaitForRedirectAsync(listener, ct);
            if (query["error"] is { } error) throw new OAuthException($"Google sign-in failed: {error}.");
            if (query["state"] != state || query["code"] is not { } code) throw new OAuthException("Google returned an unexpected response.");

            using var request = new HttpRequestMessage(HttpMethod.Post, "https://oauth2.googleapis.com/token")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = config.GoogleClientId,
                    // Desktop OAuth clients have a "secret" that Google documents as not confidential.
                    ["client_secret"] = config.GoogleClientSecret,
                    ["code"] = code,
                    ["code_verifier"] = verifier,
                    ["grant_type"] = "authorization_code",
                    ["redirect_uri"] = redirectUri,
                }),
            };
            var json = await Http.SendAsync(request, ct);
            return json?["id_token"]?.GetValue<string>() ?? throw new OAuthException("Google didn't return an ID token.");
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>Accepts connections until the browser hits the redirect URI, answers it, and returns its query.</summary>
    private static async Task<System.Collections.Specialized.NameValueCollection> WaitForRedirectAsync(TcpListener listener, CancellationToken ct)
    {
        while (true)
        {
            using var client = await listener.AcceptTcpClientAsync(ct);
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            var requestLine = await reader.ReadLineAsync(ct) ?? "";
            var target = requestLine.Split(' ') is [_, var t, ..] ? t : "/";
            var queryIndex = target.IndexOf('?');
            var query = HttpUtility.ParseQueryString(queryIndex >= 0 ? target[(queryIndex + 1)..] : "");
            var done = query["code"] != null || query["error"] != null;

            var page = done
                ? "<h2>You're signed in.</h2><p>You can close this tab and return to OpenMessages.</p>"
                : "<p>Waiting for Google sign-in…</p>";
            var html = $"<!doctype html><meta charset=utf-8><title>OpenMessages</title><body style=\"font-family:system-ui;margin:3em\">{page}</body>";
            var bytes = Encoding.UTF8.GetBytes(html);
            var header = $"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(header), ct);
            await stream.WriteAsync(bytes, ct);
            if (done) return query;
        }
    }

    private static string Query(Dictionary<string, string> fields) =>
        string.Join("&", fields.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));

    private static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public sealed class OAuthException(string message) : Exception(message);
