using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace GitDevExLite.Core.Hosting;

/// <summary>Thin JSON-over-HTTP helper shared by the providers.</summary>
internal sealed class ApiClient
{
    private readonly HttpClient _http;
    private readonly AuthenticationHeaderValue _auth;

    public ApiClient(HttpClient http, AuthenticationHeaderValue auth)
    {
        _http = http;
        _auth = auth;
    }

    /// <summary>Azure DevOps PATs use Basic auth; Entra tokens (JWTs) use Bearer. GitHub always uses Bearer.</summary>
    public static AuthenticationHeaderValue AuthFor(HostKind kind, string token)
    {
        if (kind == HostKind.AzureDevOps && !token.StartsWith("eyJ", StringComparison.Ordinal))
            return new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(":" + token)));
        return new("Bearer", token);
    }

    public async Task<JsonElement> SendAsync(
        HttpMethod method, string url, object? body, CancellationToken ct, Action<HttpRequestMessage>? configure = null)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = _auth;
        request.Headers.UserAgent.ParseAdd("GitDevExLite/1.0");
        request.Headers.Accept.ParseAdd("application/json");
        configure?.Invoke(request);
        if (body is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new HostingException($"Could not reach the server: {ex.Message}", 0, ex);
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                var message = ExtractMessage(text) ?? response.ReasonPhrase ?? "Request failed.";
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    throw new HostingAuthException(message, (int)response.StatusCode);
                throw new HostingException(message, (int)response.StatusCode);
            }
            // Azure DevOps answers an expired or missing sign-in with a 200 HTML page.
            if (text.TrimStart().StartsWith('<'))
                throw new HostingAuthException("The server returned a sign-in page; credentials are missing or expired.", 401);
            if (string.IsNullOrWhiteSpace(text)) return default;
            return JsonDocument.Parse(text).RootElement.Clone();
        }
    }

    /// <summary>Fetches raw text content (used for file contents).</summary>
    public async Task<string?> GetTextAsync(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = _auth;
        request.Headers.UserAgent.ParseAdd("GitDevExLite/1.0");
        try
        {
            using var response = await _http.SendAsync(request, ct);
            return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(ct) : null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    private static string? ExtractMessage(string text)
    {
        try
        {
            var root = JsonDocument.Parse(text).RootElement;
            var message = root.Str("message");
            if (message.Length > 0)
            {
                var detail = string.Join("; ", root.Arr("errors")
                    .Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : x.Str("message"))
                    .Where(s => s.Length > 0));
                return detail.Length > 0 ? $"{message} ({detail})" : message;
            }
        }
        catch (JsonException)
        {
            // Not JSON; fall through to the raw text.
        }
        return string.IsNullOrWhiteSpace(text) || text.Length > 300 ? null : text.Trim();
    }
}

internal static class Json
{
    public static string Str(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    public static string Id(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind == JsonValueKind.Number ? v.GetInt64().ToString() : v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : ""
            : "";

    public static int Int(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    public static bool Bool(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    public static JsonElement Obj(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : default;

    public static IEnumerable<JsonElement> Arr(this JsonElement e, string? name = null)
    {
        var target = name is null ? e : e.Obj(name);
        return target.ValueKind == JsonValueKind.Array ? target.EnumerateArray() : Enumerable.Empty<JsonElement>();
    }

    public static DateTimeOffset? Date(this JsonElement e, string name) =>
        DateTimeOffset.TryParse(e.Str(name), out var d) ? d : null;
}
