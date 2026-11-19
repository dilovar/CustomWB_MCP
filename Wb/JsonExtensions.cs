using System.Globalization;
using System.Text.Json.Nodes;

namespace CustomWB_MCP.Wb;

/// <summary>
/// Защищённое чтение полей из ответа WB. Поля читаем по нескольким кандидатам-именам,
/// потому что схемы WB местами расходятся между версиями. Отсутствующее поле -> null/0,
/// но на уровне паспорта это НЕ превращается в "ноль продаж".
/// </summary>
public static class JsonExtensions
{
    public static JsonArray AsArraySafe(this JsonNode? node)
        => node as JsonArray ?? new JsonArray();

    public static JsonNode? Pick(this JsonNode? node, params string[] keys)
    {
        if (node is not JsonObject obj) return null;
        foreach (var k in keys)
            if (obj.TryGetPropertyValue(k, out var v) && v is not null)
                return v;
        return null;
    }

    public static long GetLong(this JsonNode? node, params string[] keys)
    {
        var v = node.Pick(keys);
        if (v is null) return 0;
        try { return v.GetValue<long>(); }
        catch { return long.TryParse(v.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var r) ? r : 0; }
    }

    public static int GetInt(this JsonNode? node, params string[] keys)
        => (int)node.GetLong(keys);

    public static decimal GetDecimal(this JsonNode? node, params string[] keys)
    {
        var v = node.Pick(keys);
        if (v is null) return 0m;
        try { return v.GetValue<decimal>(); }
        catch { return decimal.TryParse(v.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var r) ? r : 0m; }
    }

    public static string? GetStr(this JsonNode? node, params string[] keys)
        => node.Pick(keys)?.ToString();

    public static DateTime? GetDate(this JsonNode? node, params string[] keys)
    {
        var s = node.GetStr(keys);
        return DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var d)
            ? d : null;
    }
}
