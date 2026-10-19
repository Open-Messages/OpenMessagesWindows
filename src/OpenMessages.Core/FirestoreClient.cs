using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace OpenMessages.Core;

/// <summary>Key parameters the phone publishes at <c>users/{uid}.otpSync</c>.</summary>
public sealed record RemoteKeyInfo(byte[] Salt, int Iterations, OtpCrypto.Sealed Check);

/// <summary>One encrypted record from <c>users/{uid}/otps</c>.</summary>
/// <param name="Name">Full document resource name, used to delete it.</param>
/// <param name="CreatedAt">Raw RFC 3339 value, reused as the query cursor so no precision is lost.</param>
public sealed record RemoteOtp(string Name, OtpCrypto.Sealed Sealed, string CreatedAt, DateTimeOffset? ExpiresAt);

/// <summary>The few Firestore REST calls OTP sync needs.</summary>
public sealed class FirestoreClient(AppConfig config)
{
    private string DocumentsUrl => $"https://firestore.googleapis.com/v1/projects/{config.ProjectId}/databases/(default)/documents";

    /// <summary>Null when the account has never set up a sync key.</summary>
    public async Task<RemoteKeyInfo?> GetKeyInfoAsync(string uid, string idToken, CancellationToken ct = default)
    {
        using var request = Request(HttpMethod.Get, $"{DocumentsUrl}/users/{uid}", idToken);
        JsonNode? json;
        try
        {
            json = await Http.SendAsync(request, ct);
        }
        catch (ApiException e) when (e.Status == 404)
        {
            return null;
        }
        return ParseKeyInfo(json);
    }

    public static RemoteKeyInfo? ParseKeyInfo(JsonNode? document)
    {
        var meta = MapFields(document?["fields"]?["otpSync"]);
        var check = MapFields(meta?["check"]);
        if (StringValue(meta?["salt"]) is not { } salt || IntValue(meta?["iterations"]) is not { } iterations
            || StringValue(check?["iv"]) is not { } iv || StringValue(check?["ct"]) is not { } ctValue)
            return null;
        return new RemoteKeyInfo(Convert.FromBase64String(salt), iterations, new OtpCrypto.Sealed(iv, ctValue));
    }

    /// <summary>Newest records first. With <paramref name="since"/>, only records created at or after that cursor.</summary>
    public async Task<IReadOnlyList<RemoteOtp>> GetOtpsAsync(string uid, string idToken, string? since, int limit = 50, CancellationToken ct = default)
    {
        var query = new JsonObject
        {
            ["from"] = new JsonArray(new JsonObject { ["collectionId"] = "otps" }),
            ["orderBy"] = new JsonArray(new JsonObject
            {
                ["field"] = new JsonObject { ["fieldPath"] = "createdAt" },
                ["direction"] = "DESCENDING",
            }),
            ["limit"] = limit,
        };
        if (since != null)
        {
            query["where"] = new JsonObject
            {
                ["fieldFilter"] = new JsonObject
                {
                    ["field"] = new JsonObject { ["fieldPath"] = "createdAt" },
                    ["op"] = "GREATER_THAN_OR_EQUAL",
                    ["value"] = new JsonObject { ["timestampValue"] = since },
                },
            };
        }
        using var request = Request(HttpMethod.Post, $"{DocumentsUrl}/users/{uid}:runQuery", idToken);
        request.Content = JsonContent.Create(new JsonObject { ["structuredQuery"] = query });
        return ParseOtps(await Http.SendAsync(request, ct));
    }

    public static IReadOnlyList<RemoteOtp> ParseOtps(JsonNode? rows)
    {
        var result = new List<RemoteOtp>();
        foreach (var row in rows as JsonArray ?? [])
        {
            var doc = row?["document"];
            var fields = doc?["fields"];
            if (doc?["name"]?.GetValue<string>() is not { } name || StringValue(fields?["iv"]) is not { } iv
                || StringValue(fields?["ct"]) is not { } ctValue || TimestampValue(fields?["createdAt"]) is not { } createdAt)
                continue;
            var expires = TimestampValue(fields?["expiresAt"]) is { } e ? ParseTimestamp(e) : null;
            result.Add(new RemoteOtp(name, new OtpCrypto.Sealed(iv, ctValue), createdAt, expires));
        }
        return result;
    }

    public async Task DeleteAsync(string name, string idToken, CancellationToken ct = default)
    {
        using var request = Request(HttpMethod.Delete, $"https://firestore.googleapis.com/v1/{name}", idToken);
        await Http.SendAsync(request, ct);
    }

    /// <summary>Parses RFC 3339 with any number of fractional digits (Firestore sends up to nanoseconds).</summary>
    public static DateTimeOffset? ParseTimestamp(string value)
    {
        var text = value;
        var dot = text.IndexOf('.');
        if (dot >= 0)
        {
            var zone = text.IndexOfAny(['Z', '+', '-'], dot);
            if (zone > dot + 8) text = text[..(dot + 8)] + text[zone..]; // DateTimeOffset takes 7 digits max.
        }
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;
    }

    private static HttpRequestMessage Request(HttpMethod method, string url, string idToken)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", idToken);
        return request;
    }

    // Firestore value decoding

    private static JsonNode? MapFields(JsonNode? value) => value?["mapValue"]?["fields"];
    private static string? StringValue(JsonNode? value) => value?["stringValue"]?.GetValue<string>();
    private static string? TimestampValue(JsonNode? value) => value?["timestampValue"]?.GetValue<string>();

    private static int? IntValue(JsonNode? value) =>
        int.TryParse(value?["integerValue"]?.ToString(), out var i) ? i : null;
}
