# Этап сборки
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Копируем только .csproj и .sln для восстановления зависимостей
COPY ["TelegramBot/TelegramBot.csproj", "TelegramBot/"]
COPY ["ProjectTelegramBot.sln", "."]
RUN dotnet restore "ProjectTelegramBot.sln"

# Копируем весь исходный код
COPY . .

# Сборка проекта
WORKDIR "/src/TelegramBot"
RUN dotnet publish -c Release -o /app/publish --no-restore

# Финальный образ (только runtime + publish)
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app/publish .

# Переменные среды (BotToken задаётся при запуске контейнера)
ENV BotToken="8044463785:AAGxFmlGzOGLJ821BYzEQz_8NxzvNeaFOW4"
ENV ASPNETCORE_ENVIRONMENT=Production
ENV DOTNET_NOLOGO=true

ENTRYPOINT ["dotnet", "TelegramBot.dll"]
