# Этап сборки
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Копируем только необходимые файлы для восстановления зависимостей
COPY TelegramBot/TelegramBot.csproj ./TelegramBot/
COPY ProjectTelegramBot.sln .
RUN dotnet restore

# Копируем остальные файлы проекта
COPY TelegramBot/. ./TelegramBot/

# Собираем и публикуем приложение
WORKDIR /src/TelegramBot
RUN dotnet publish -c Release -o /app/publish --no-restore

# Этап запуска
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app

# Копируем только необходимые файлы из этапа сборки
COPY --from=build /app/publish .

# Рекомендуемые переменные среды (значения нужно задать при запуске)
ENV BotToken="8044463785:AAGxFmlGzOGLJ821BYzEQz_8NxzvNeaFOW4"
ENV ASPNETCORE_ENVIRONMENT=Production
ENV DOTNET_NOLOGO=true
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1

# Оптимизация для контейнера
ENV COMPlus_EnableDiagnostics=0

ENTRYPOINT ["dotnet", "TelegramBot.dll"]
