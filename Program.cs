var builder = WebApplication.CreateBuilder(args);

// User-secrets по умолчанию грузятся только в Development. Сервер же запускается в Production
// (под ngrok-туннелем), поэтому подключаем хранилище секретов явно — токен виден в любом окружении.
builder.Configuration.AddUserSecrets<Program>(optional: true);

// Именованный HttpClient для WB Statistics API.
// Токен берётся из конфигурации (user-secrets), НЕ из кода.
builder.Services.AddHttpClient("WbStatistics", (sp, client) =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var token = config["Wildberries:Token"]
        ?? throw new InvalidOperationException("Не задан Wildberries:Token. Положи токен в user-secrets.");

    client.BaseAddress = new Uri("https://statistics-api.wildberries.ru");
    // WB ждёт токен в заголовке Authorization БЕЗ префикса Bearer.
    client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", token);
    client.Timeout = TimeSpan.FromSeconds(100); // статистика отвечает медленно
});

builder.Services
    .AddMcpServer()
    // Streamable HTTP — нужный транспорт для удалённых коннекторов Claude.
    // Stateless = true: сервер не держит сессию между запросами (для read-only инструментов это правильно).
    .WithHttpTransport(options => options.Stateless = true)
    // Находит все классы с [McpServerToolType] и регистрирует методы с [McpServerTool] как инструменты.
    .WithToolsFromAssembly();

var app = builder.Build();

// MCP-эндпоинт будет доступен по пути /mcp
app.MapMcp("/mcp");

// Жёстко фиксируем порт, чтобы туннель было проще настроить.
app.Run("http://localhost:5000");
