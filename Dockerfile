# Сборка
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY CustomWB_MCP.csproj ./
RUN dotnet restore
COPY . ./
RUN dotnet publish -c Release -o /app /p:UseAppHost=false

# Рантайм
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app ./
# Порт задаёт платформа (Railway -> PORT). Токен и путь MCP — через переменные окружения:
#   Wildberries__Token  — токен WB (read-only)
#   Mcp__Path           — приватный путь эндпоинта, напр. /mcp-9f3k2a
ENTRYPOINT ["dotnet", "CustomWB_MCP.dll"]
