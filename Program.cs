using CustomWB_MCP.Wb;

var builder = WebApplication.CreateBuilder(args);

// --- Токен WB ---
// Берётся ТОЛЬКО из конфигурации (user-secrets/переменные окружения), никогда из кода.
//   dotnet user-secrets set "Wildberries:Token" "<твой токен>"
// WB ждёт токен в заголовке Authorization БЕЗ префикса Bearer.
var wbToken = builder.Configuration["Wildberries:Token"];

// Регистрируем по именованному HttpClient на каждый WB-домен.
// Если токена нет — клиенты всё равно создаются, но вызовы вернут понятную ошибку авторизации.
foreach (var (name, baseUrl) in WbClients.BaseUrls)
{
    builder.Services.AddHttpClient(name, client =>
    {
        client.BaseAddress = new Uri(baseUrl);
        client.Timeout = TimeSpan.FromSeconds(120); // отчёты WB отвечают медленно
        if (!string.IsNullOrWhiteSpace(wbToken))
            client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", wbToken);
    });
}

// Общий шлюз к WB (типизированные read-only методы + паспорт данных).
builder.Services.AddSingleton<WbGateway>();

builder.Services
    .AddMcpServer()
    // Streamable HTTP — транспорт для удалённых коннекторов Claude.
    .WithHttpTransport(options => options.Stateless = true)
    .WithToolsFromAssembly();

var app = builder.Build();

// Путь MCP-эндпоинта. По умолчанию /mcp; на публичном хосте задай неугадываемый путь
// через переменную Mcp__Path (например /mcp-9f3k2a) — это защита от случайных заходов,
// пока не настроен OAuth. Это сокрытие, а не полноценная аутентификация.
var mcpPath = builder.Configuration["Mcp:Path"] ?? "/mcp";
app.MapMcp(mcpPath);

// Railway/облако задаёт порт через переменную PORT; локально — 5000.
// Биндимся на 0.0.0.0, иначе снаружи контейнера сервер не виден.
var port = Environment.GetEnvironmentVariable("PORT") ?? "5000";
app.Run($"http://0.0.0.0:{port}");
