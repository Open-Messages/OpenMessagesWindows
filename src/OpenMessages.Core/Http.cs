using System.Text.Json.Nodes;

namespace OpenMessages.Core;

internal static class Http
{
    public static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>Sends <paramref name="request"/> and returns the JSON body, turning Google API errors into readable messages.</summary>
    public static async Task<JsonNode?> SendAsync(HttpRequestMessage request, CancellationToken ct = default)
    {
        using var response = await Client.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        var body = string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
        if (!response.IsSuccessStatusCode) throw new ApiException((int)response.StatusCode, body);
        return body;
    }
}

public sealed class ApiException(int status, JsonNode? body) : Exception(Describe(status, body))
{
    public int Status { get; } = status;

    private static string Describe(int status, JsonNode? body)
    {
        var error = body?["error"];
        var message = error switch
        {
            JsonObject o => o["message"]?.GetValue<string>(),
            JsonValue v => body?["error_description"] is { } d ? $"{v}: {d}" : v.ToString(),
            _ => null,
        };
        return $"Request failed ({status}): {message ?? "unknown error"}";
    }
}
