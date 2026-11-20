using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CustomWB_MCP.Wb;

/// <summary>
/// Низкоуровневый помощник для вызовов WB.
/// 429: НЕ штормим. Если WB прислал Retry-After/X-Ratelimit-Retry и ждать недолго (&lt;= 30с) —
/// ждём один раз и повторяем один раз; иначе сразу возвращаем "частично" с указанием, сколько ждать.
/// Это важно, потому что у Базового токена лимиты часовые — быстрыми повторами их не обойти, только усугубить.
/// 5xx — ограниченные повторы с backoff. 401/403 — без повторов.
/// </summary>
public static class WbHttp
{
    private const int Max5xxAttempts = 3;

    public sealed class WbCallException : Exception
    {
        public SourceStatus Status { get; }
        public WbCallException(SourceStatus status, string message) : base(message) => Status = status;
    }

    public static Task<JsonNode?> GetJsonAsync(HttpClient client, string path, CancellationToken ct)
        => SendJsonAsync(client, () => new HttpRequestMessage(HttpMethod.Get, path), ct);

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
        var retried429 = false;
        for (var attempt5xx = 1; ; attempt5xx++)
        {
            using var req = requestFactory();
            HttpResponseMessage resp;
            try
            {
                resp = await client.SendAsync(req, ct);
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new WbCallException(SourceStatus.Error, "таймаут запроса к WB");
            }

            switch ((int)resp.StatusCode)
            {
                case 204:
                    return null;
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
                        "403: нет прав; возможно, нужна подписка/категория или тип токена");
                case 429:
                {
                    var wait = ParseRetryAfter(resp);
                    // Ждём и повторяем ОДИН раз только если ждать недолго.
                    if (!retried429 && wait is { } w && w <= TimeSpan.FromSeconds(30))
                    {
                        retried429 = true;
                        await Task.Delay(w, ct);
                        continue;
                    }
                    var hint = wait is { } ww
                        ? $"повторить примерно через {(int)ww.TotalSeconds} с"
                        : "у Базового токена лимит часовой — повторить позже";
                    throw new WbCallException(SourceStatus.Partial, $"429: превышен лимит запросов WB ({hint})");
                }
                case >= 500:
                {
                    if (attempt5xx >= Max5xxAttempts)
                        throw new WbCallException(SourceStatus.Error, $"{(int)resp.StatusCode}: сервис WB недоступен");
                    await Task.Delay(TimeSpan.FromMilliseconds(500 * attempt5xx), ct);
                    continue;
                }
                case 404:
                    throw new WbCallException(SourceStatus.Error,
                        $"404: эндпоинт не найден ({req.Method} {req.RequestUri}). Сверить путь с актуальной докой WB.");
                default:
                    throw new WbCallException(SourceStatus.Error, $"{(int)resp.StatusCode}: {resp.ReasonPhrase}");
            }
        }
    }

    private static TimeSpan? ParseRetryAfter(HttpResponseMessage resp)
    {
        if (resp.Headers.TryGetValues("X-Ratelimit-Retry", out var vals)
            && int.TryParse(vals.FirstOrDefault(), out var secs) && secs > 0)
            return TimeSpan.FromSeconds(secs);
        if (resp.Headers.RetryAfter?.Delta is { } delta)
            return delta;
        return null;
    }
}
