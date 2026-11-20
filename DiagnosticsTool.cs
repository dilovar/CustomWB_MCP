using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using ModelContextProtocol.Server;
using CustomWB_MCP.Wb;

namespace CustomWB_MCP.Tools;

/// <summary>
/// Диагностика для отладки 401. Показывает: (1) видит ли сервер токен WB и нет ли в нём мусора
/// (без раскрытия значения), (2) достаёт ли Railway до WB через неавторизованный /ping.
/// Безопасно вызывать.
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

    [McpServerTool, Description("Диагностика 401: видит ли сервер токен WB (длина, мусор — без раскрытия значения) и достаёт ли Railway до WB через неавторизованный /ping. Для отладки.")]
    public async Task<string> WbDiagnostics(CancellationToken ct = default)
    {
        var token = _config["Wildberries:Token"];
        var present = !string.IsNullOrEmpty(token);

        // Проверка связи Railway -> WB: /ping категории Статистика не требует токена.
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

        var info = new
        {
            tokenConfigured = present,
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
