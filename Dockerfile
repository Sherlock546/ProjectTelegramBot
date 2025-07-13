# Этап сборки
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Копируем файлы решения и проекта
COPY ProjectTelegramBot.sln .
COPY TelegramBot/TelegramBot.csproj ./TelegramBot/
RUN dotnet restore

# Копируем весь код
COPY . .

# Собираем приложение
WORKDIR /src/TelegramBot
RUN dotnet publish -c Release -o /app/publish

# Этап запуска
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

# Переменная среды (значение можно задать при запуске контейнера)
ENV BotToken="8044463785:AAGxFmlGzOGLJ821BYzEQz_8NxzvNeaFOW4"

# Запускаем бота
ENTRYPOINT ["dotnet", "TelegramBot.dll"]
