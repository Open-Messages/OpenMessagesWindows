using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace OpenMessages.Core;

/// <summary>A Firebase Auth session, persisted so the app stays signed in.</summary>
public sealed record AuthSession(string Uid, string? Email, string IdToken, string RefreshToken, DateTimeOffset ExpiresAt);

/// <summary>Firebase Auth over its REST API (Identity Toolkit + Secure Token).</summary>
public sealed class FirebaseAuthClient(AppConfig config)
{
    public async Task<AuthSession> SignInAsync(string googleIdToken, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://identitytoolkit.googleapis.com/v1/accounts:signInWithIdp?key={config.ApiKey}")
        {
            Content = JsonContent.Create(new
            {
                postBody = $"id_token={googleIdToken}&providerId=google.com",
                requestUri = "http://localhost",
                returnSecureToken = true,
                returnIdpCredential = true,
            }),
        };
        var json = await Http.SendAsync(request, ct) ?? new JsonObject();
        return new AuthSession(
            Uid: Required(json, "localId"),
            Email: json["email"]?.GetValue<string>(),
            IdToken: Required(json, "idToken"),
            RefreshToken: Required(json, "refreshToken"),
            ExpiresAt: DateTimeOffset.UtcNow.AddSeconds(Seconds(json["expiresIn"])));
    }

    /// <summary>Returns <paramref name="session"/> with a fresh ID token if the current one expires within five minutes.</summary>
    public async Task<AuthSession> RefreshedAsync(AuthSession session, CancellationToken ct = default)
    {
        if (session.ExpiresAt - DateTimeOffset.UtcNow > TimeSpan.FromMinutes(5)) return session;
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://securetoken.googleapis.com/v1/token?key={config.ApiKey}")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = session.RefreshToken,
            }),
        };
        var json = await Http.SendAsync(request, ct) ?? new JsonObject();
        return session with
        {
            IdToken = Required(json, "id_token"),
            RefreshToken = Required(json, "refresh_token"),
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(Seconds(json["expires_in"])),
        };
    }

    private static string Required(JsonNode json, string name) =>
        json[name]?.GetValue<string>() ?? throw new ApiException(200, json);

    private static double Seconds(JsonNode? value) =>
        double.TryParse(value?.ToString(), out var s) ? s : 3600;
}
