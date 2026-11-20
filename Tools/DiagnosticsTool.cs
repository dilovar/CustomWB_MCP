using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using ModelContextProtocol.Server;
using CustomWB_MCP.Wb;

namespace CustomWB_MCP.Tools;

/// <summary>
/// Диагностика для отладки 401. Показывает: видит ли сервер токен (через конфигурацию и напрямую
/// из окружения), какие переменные окружения с token/wild/berr реально переданы контейнеру (ИМЕНА, без значений),
/// и достаёт ли Railway до WB через неавторизованный /ping. Значение токена НЕ раскрывается.
/// </summary>
[McpServerToolType]
public sealed class DiagnosticsTool
{
    private readonly IConfiguration _config;
    private readonly IHttpClientFactory _factory;

    public DiagnosticsTool(IConfiguration config, IHttpClientFactory factory)
    {
        _config = config;
        _factory = factory;
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    [McpServerTool, Description("Диагностика 401: видит ли сервер токен WB (через конфиг и напрямую из env), имена релевантных переменных окружения (без значений) и доступность WB /ping. Для отладки.")]
    public async Task<string> WbDiagnostics(CancellationToken ct = default)
    {
        // Токен через конфигурацию (.NET маппит Wildberries__Token -> Wildberries:Token)
        var cfgToken = _config["Wildberries:Token"];
        // Токен напрямую из окружения (без маппинга)
        var envDoubleUnderscore = Environment.GetEnvironmentVariable("Wildberries__Token");
        var envWbToken = Environment.GetEnvironmentVariable("WB_TOKEN");

        // Имена всех переменных окружения, похожих на токен WB (ТОЛЬКО имена, без значений)
        var relevantEnvNames = Environment.GetEnvironmentVariables()
            .Keys.Cast<string>()
            .Where(k => k.Contains("token", StringComparison.OrdinalIgnoreCase)
                     || k.Contains("wild", StringComparison.OrdinalIgnoreCase)
                     || k.Contains("berr", StringComparison.OrdinalIgnoreCase))
            .OrderBy(k => k)
            .ToList();

        // Проверка связи Railway -> WB (без токена)
        string wbPing;
        try
        {
            var client = _factory.CreateClient(WbClients.Statistics);
            using var resp = await client.GetAsync("/ping", ct);
            wbPing = $"HTTP {(int)resp.StatusCode} {resp.StatusCode}";
        }
        catch (Exception ex)
        {
            wbPing = $"ОШИБКА СВЯЗИ: {ex.GetType().Name}: {ex.Message}";
        }

        var token = cfgToken;
        var present = !string.IsNullOrEmpty(token);

        var info = new
        {
            // откуда виден токен
            tokenViaConfig = !string.IsNullOrEmpty(cfgToken),
            tokenViaEnv_Wildberries__Token = !string.IsNullOrEmpty(envDoubleUnderscore),
            tokenViaEnv_WB_TOKEN = !string.IsNullOrEmpty(envWbToken),
            // какие имена реально есть в окружении контейнера
            relevantEnvNames,
            // качество значения (по конфиг-токену)
            tokenLength = token?.Length ?? 0,
            hasSurroundingWhitespace = present && token != token!.Trim(),
            containsNewline = present && (token!.Contains('\n') || token.Contains('\r')),
            containsQuotes = present && (token!.StartsWith("\"") || token!.EndsWith("\"")),
            startsWithBearer = present && token!.TrimStart().StartsWith("Bearer", StringComparison.OrdinalIgnoreCase),
            looksLikeJwt = present && token!.Split('.').Length == 3,
            mcpPath = _config["Mcp:Path"] ?? "(не задан, используется /mcp)",
            wbPingResult = wbPing
        };
        return JsonSerializer.Serialize(info, Json);
    }
}
