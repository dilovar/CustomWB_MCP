using System.ComponentModel;
using ModelContextProtocol.Server;

namespace CustomWB_MCP.Tools;

/// <summary>
/// Простейший инструмент для проверки, что Claude вообще видит сервер и может вызвать tool.
/// </summary>
[McpServerToolType]
public static class PingTool
{
    [McpServerTool, Description("Проверка связи с MCP-сервером. Возвращает 'pong' и переданное сообщение.")]
    public static string Ping(
        [Description("Любой текст для проверки эха")] string message)
        => $"pong: {message}";
}
