using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CustomWB_MCP.Wb;

/// <summary>
/// Низкоуровневый помощник для вызовов WB: ретраи на 429 (с учётом Retry-After)
/// и временные 5xx с экспоненциальным backoff. 401/403/подписку НЕ лечим повторами.
/// </summary>
public static class WbHttp
{
    private const int MaxAttempts = 4;

    public sealed class WbCallException : Exception
    {
        public SourceStatus Status { get; }
        public WbCallException(SourceStatus status, string message) : base(message) => Status = status;
    }

    /// <summary>GET с ретраями, возвращает корневой JsonNode.</summary>
    public static Task<JsonNode?> GetJsonAsync(HttpClient client, string path, CancellationToken ct)
        => SendJsonAsync(client, () => new HttpRequestMessage(HttpMethod.Get, path), ct);

    /// <summary>POST с JSON-телом и ретраями, возвращает корневой JsonNode.</summary>
    public static Task<JsonNode?> PostJsonAsync(HttpClient client, string path, object body, CancellationToken ct)
        => SendJsonAsync(client, () =>
        {
            var json = JsonSerializer.Serialize(body);
            return new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }, ct);

    private static async Task<JsonNode?> SendJsonAsync(
        HttpClient client, Func<HttpRequestMessage> requestFactory, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var req = requestFactory();
            HttpResponseMessage resp;
            try
            {
                resp = await client.SendAsync(req, ct);
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                if (attempt >= MaxAttempts)
                    throw new WbCallException(SourceStatus.Error, "таймаут запроса к WB");
                await Task.Delay(Backoff(attempt), ct);
                continue;
            }

            switch ((int)resp.StatusCode)
            {
                case >= 200 and < 300:
                {
                    var text = await resp.Content.ReadAsStringAsync(ct);
                    if (string.IsNullOrWhiteSpace(text)) return null;
                    return JsonNode.Parse(text);
                }
                case 401:
                    throw new WbCallException(SourceStatus.NoAccess,
                        "401: токен недействителен или его категория не совпадает с этим API");
                case 403:
                    throw new WbCallException(SourceStatus.NoAccess,
                        "403: нет прав; возможно, требуется подписка (напр. Джем) или метод заблокирован");
                case 429:
                {
                    if (attempt >= MaxAttempts)
                        throw new WbCallException(SourceStatus.Partial, "429: превышен лимит запросов WB");
                    var retryAfter = ParseRetryAfter(resp) ?? Backoff(attempt);
                    await Task.Delay(retryAfter, ct);
                    continue;
                }
                case >= 500:
                {
                    if (attempt >= MaxAttempts)
                        throw new WbCallException(SourceStatus.Error, $"{(int)resp.StatusCode}: сервис WB недоступен");
                    await Task.Delay(Backoff(attempt), ct);
                    continue;
                }
                default:
                    throw new WbCallException(SourceStatus.Error,
                        $"{(int)resp.StatusCode}: {resp.ReasonPhrase}");
            }
        }
    }

    private static TimeSpan Backoff(int attempt) => TimeSpan.FromMilliseconds(400 * Math.Pow(2, attempt - 1));

    private static TimeSpan? ParseRetryAfter(HttpResponseMessage resp)
    {
        // WB отдаёт X-Ratelimit-Retry (секунды) и/или стандартный Retry-After.
        if (resp.Headers.TryGetValues("X-Ratelimit-Retry", out var vals)
            && int.TryParse(vals.FirstOrDefault(), out var secs) && secs > 0)
            return TimeSpan.FromSeconds(Math.Min(secs, 30));
        if (resp.Headers.RetryAfter?.Delta is { } delta)
            return delta;
        return null;
    }
}
