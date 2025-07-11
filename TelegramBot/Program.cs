using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace TelegramBot
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            var host = Host.CreateDefaultBuilder(args)
                .ConfigureServices((context, services) =>
                {
                    services.AddHostedService<BotBackgroundService>();
                    services.AddSingleton<UserInfoStorage>();
                })
                .Build();

            await host.RunAsync();
        }
    }

    public class UserInfoStorage
    {
        private readonly ConcurrentDictionary<long, UserInfo> _userInfos = new();

        public void AddOrUpdateUserInfo(long userId, UserInfo info)
        {
            _userInfos.AddOrUpdate(userId, info, (_, _) => info);
        }

        public UserInfo GetUserInfo(long userId)
        {
            return _userInfos.TryGetValue(userId, out var info) ? info : new UserInfo { UserId = userId };
        }

        public UserInfo GetUserInfoByUsername(string username)
        {
            return _userInfos.Values.FirstOrDefault(x =>
                x.Username?.Equals(username, StringComparison.OrdinalIgnoreCase) ?? false);
        }

        public IEnumerable<UserInfo> GetAllUserInfos()
        {
            return _userInfos.Values;
        }
    }

    public class UserInfo
    {
        public long UserId { get; set; }
        public string Username { get; set; }
        public string FirstName { get; set; }
        public string LastName { get; set; }
        public Dictionary<int, string> Answers { get; } = new();

        public void UpdateAnswer(int questionNumber, string answer)
        {
            if (string.IsNullOrWhiteSpace(answer))
            {
                Answers.Remove(questionNumber);
            }
            else
            {
                Answers[questionNumber] = answer;
            }
        }
    }

    public class BotBackgroundService : BackgroundService
    {
        private readonly ILogger<BotBackgroundService> _logger;
        private readonly UserInfoStorage _userInfoStorage;
        private TelegramBotClient _botClient;
        private CancellationTokenSource _cts;

        // Замените на ваш токен
        private const string BotToken = "8044463785:AAGxFmlGzOGLJ821BYzEQz_8NxzvNeaFOW4";

        public BotBackgroundService(ILogger<BotBackgroundService> logger, UserInfoStorage userInfoStorage)
        {
            _logger = logger;
            _userInfoStorage = userInfoStorage;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _botClient = new TelegramBotClient(BotToken);
            _cts = new CancellationTokenSource();

            // Настройка обработчиков
            var receiverOptions = new ReceiverOptions
            {
                AllowedUpdates = Array.Empty<UpdateType>() // получаем все типы обновлений
            };

            _botClient.StartReceiving(
                updateHandler: HandleUpdateAsync,
                pollingErrorHandler: HandlePollingErrorAsync,
                receiverOptions: receiverOptions,
                cancellationToken: _cts.Token
            );

            var me = await _botClient.GetMeAsync();
            _logger.LogInformation($"Бот @{me.Username} запущен!");

            // Ожидаем отмены
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(1000, stoppingToken);
            }

            _cts.Cancel();
        }

        private async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
        {
            try
            {
                if (update.Message is not { } message)
                    return;

                // Обрабатываем команды
                if (message.Entities?.FirstOrDefault()?.Type == MessageEntityType.BotCommand)
                {
                    await HandleCommandAsync(message);
                    return;
                }

                // Обрабатываем ответы на вопросы
                if (message.ReplyToMessage?.Text?.Contains("добро пожаловать") ?? false)
                {
                    await HandleUserAnswers(message);
                    return;
                }

                // Обрабатываем новых участников
                if (message.NewChatMembers != null)
                {
                    foreach (var newMember in message.NewChatMembers)
                    {
                        if (newMember.IsBot)
                            continue;

                        await SendWelcomeMessage(message.Chat.Id, newMember);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка при обработке обновления");
            }
        }

        private async Task HandleCommandAsync(Message message)
        {
            var command = message.Text.Split(' ').First().ToLower();

            switch (command)
            {
                case "/info":
                    await HandleInfoCommand(message);
                    break;
                case "/edit":
                    await HandleEditCommand(message);
                    break;
                case "/add":
                    await HandleAddCommand(message);
                    break;
            }
        }

        private async Task HandleInfoCommand(Message message)
        {
            var parts = message.Text.Split(' ');

            // Если команда /info opg - показать всю информацию
            if (parts.Length > 1 && parts[1].Equals("opg", StringComparison.OrdinalIgnoreCase))
            {
                var allInfos = _userInfoStorage.GetAllUserInfos();
                var response = "📊 *Полная информация о всех пользователях:*\n\n" +
                              string.Join("\n\n", allInfos.Select(FormatUserInfo));

                await _botClient.SendTextMessageAsync(
                    chatId: message.Chat.Id,
                    text: response,
                    parseMode: ParseMode.Markdown,
                    replyToMessageId: message.MessageId,
                    cancellationToken: _cts.Token);
                return;
            }

            if (parts.Length == 1)
            {
                // Показать информацию обо всех пользователях (кратко)
                var allInfos = _userInfoStorage.GetAllUserInfos();
                var response = "👥 *Список пользователей:*\n\n" +
                              string.Join("\n", allInfos.Select(ui =>
                                  $"{(string.IsNullOrEmpty(ui.Username) ? $"{ui.FirstName} {ui.LastName}" : $"@{ui.Username}")}"));

                await _botClient.SendTextMessageAsync(
                    chatId: message.Chat.Id,
                    text: response,
                    parseMode: ParseMode.Markdown,
                    replyToMessageId: message.MessageId,
                    cancellationToken: _cts.Token);
            }
            else if (parts.Length == 2)
            {
                // Показать информацию о конкретном пользователе
                var username = parts[1].TrimStart('@');
                var userInfo = _userInfoStorage.GetUserInfoByUsername(username);

                if (userInfo == null)
                {
                    await _botClient.SendTextMessageAsync(
                        chatId: message.Chat.Id,
                        text: $"Пользователь @{username} не найден.",
                        replyToMessageId: message.MessageId,
                        cancellationToken: _cts.Token);
                    return;
                }

                await _botClient.SendTextMessageAsync(
                    chatId: message.Chat.Id,
                    text: FormatUserInfo(userInfo),
                    parseMode: ParseMode.Markdown,
                    replyToMessageId: message.MessageId,
                    cancellationToken: _cts.Token);
            }
        }

        private async Task HandleEditCommand(Message message)
        {
            var parts = message.Text.Split(new[] { '\n' }, 2);
            if (parts.Length < 1)
            {
                await SendEditUsage(message);
                return;
            }

            var commandParts = parts[0].Split(' ');
            if (commandParts.Length < 2)
            {
                await SendEditUsage(message);
                return;
            }

            var username = commandParts[1].TrimStart('@');
            var userInfo = _userInfoStorage.GetUserInfoByUsername(username);

            if (userInfo == null)
            {
                await _botClient.SendTextMessageAsync(
                    chatId: message.Chat.Id,
                    text: $"Пользователь @{username} не найден.",
                    replyToMessageId: message.MessageId,
                    cancellationToken: _cts.Token);
                return;
            }

            if (parts.Length == 1)
            {
                await _botClient.SendTextMessageAsync(
                    chatId: message.Chat.Id,
                    text: $"Текущая информация о пользователе @{username}:\n\n{FormatUserInfo(userInfo)}\n\n" +
                          "Для редактирования отправьте:\n/edit @username\nномер: новое значение",
                    parseMode: ParseMode.Markdown,
                    replyToMessageId: message.MessageId,
                    cancellationToken: _cts.Token);
                return;
            }

            var lines = parts[1].Split('\n');
            foreach (var line in lines)
            {
                var questionParts = line.Split(new[] { ':', '.', '-' }, 2, StringSplitOptions.TrimEntries);
                if (questionParts.Length == 2 && int.TryParse(questionParts[0], out var questionNumber))
                {
                    userInfo.UpdateAnswer(questionNumber, questionParts[1]);
                }
            }

            _userInfoStorage.AddOrUpdateUserInfo(userInfo.UserId, userInfo);

            await _botClient.SendTextMessageAsync(
                chatId: message.Chat.Id,
                text: $"Информация о пользователе @{username} обновлена!",
                replyToMessageId: message.MessageId,
                cancellationToken: _cts.Token);
        }

        private async Task HandleAddCommand(Message message)
        {
            var parts = message.Text.Split(new[] { '\n' }, 2);
            if (parts.Length < 1)
            {
                await SendAddUsage(message);
                return;
            }

            var commandParts = parts[0].Split(' ');
            if (commandParts.Length < 2)
            {
                await SendAddUsage(message);
                return;
            }

            var username = commandParts[1].TrimStart('@');

            // Проверяем, есть ли уже такой пользователь
            var existingUser = _userInfoStorage.GetUserInfoByUsername(username);
            if (existingUser != null)
            {
                await _botClient.SendTextMessageAsync(
                    chatId: message.Chat.Id,
                    text: $"Пользователь @{username} уже существует. Используйте команду /edit для изменения информации.",
                    replyToMessageId: message.MessageId,
                    cancellationToken: _cts.Token);
                return;
            }

            if (parts.Length == 1)
            {
                await _botClient.SendTextMessageAsync(
                    chatId: message.Chat.Id,
                    text: $"Для добавления пользователя @{username} отправьте:\n/add @username\nномер: значение\nномер: значение\n...",
                    replyToMessageId: message.MessageId,
                    cancellationToken: _cts.Token);
                return;
            }

            var newUserInfo = new UserInfo
            {
                Username = username,
                UserId = -1 // Временное значение, так как реальный ID неизвестен
            };

            var lines = parts[1].Split('\n');
            foreach (var line in lines)
            {
                var questionParts = line.Split(new[] { ':', '.', '-' }, 2, StringSplitOptions.TrimEntries);
                if (questionParts.Length == 2 && int.TryParse(questionParts[0], out var questionNumber))
                {
                    newUserInfo.UpdateAnswer(questionNumber, questionParts[1]);
                }
            }

            _userInfoStorage.AddOrUpdateUserInfo(newUserInfo.UserId, newUserInfo);

            await _botClient.SendTextMessageAsync(
                chatId: message.Chat.Id,
                text: $"Пользователь @{username} успешно добавлен!",
                replyToMessageId: message.MessageId,
                cancellationToken: _cts.Token);
        }

        private async Task SendEditUsage(Message message)
        {
            await _botClient.SendTextMessageAsync(
                chatId: message.Chat.Id,
                text: "Использование команды /edit:\n" +
                      "/edit @username\n" +
                      "номер: новое значение\n" +
                      "номер: новое значение\n\n" +
                      "Пример:\n" +
                      "/edit @user123\n" +
                      "2: 25 лет\n" +
                      "5: Работаю программистом",
                replyToMessageId: message.MessageId,
                cancellationToken: _cts.Token);
        }

        private async Task SendAddUsage(Message message)
        {
            await _botClient.SendTextMessageAsync(
                chatId: message.Chat.Id,
                text: "Использование команды /add:\n" +
                      "/add @username\n" +
                      "номер: значение\n" +
                      "номер: значение\n\n" +
                      "Пример:\n" +
                      "/add @user123\n" +
                      "1: Иван\n" +
                      "2: 25 лет\n" +
                      "5: Программист",
                replyToMessageId: message.MessageId,
                cancellationToken: _cts.Token);
        }

        private async Task HandleUserAnswers(Message message)
        {
            var userInfo = new UserInfo
            {
                UserId = message.From.Id,
                Username = message.From.Username,
                FirstName = message.From.FirstName,
                LastName = message.From.LastName
            };

            // Улучшенный парсинг ответов
            var text = message.Text;
            var answers = new Dictionary<int, string>();

            // Попробуем найти ответы в формате "1. ответ" или "1) ответ"
            var matches = Regex.Matches(text, @"(\d+)[.)]\s*([^\n]+)");
            foreach (Match match in matches)
            {
                if (int.TryParse(match.Groups[1].Value, out var questionNumber))
                {
                    answers[questionNumber] = match.Groups[2].Value.Trim();
                }
            }

            // Если не найдено ответов в структурированном формате,
            // попробуем проанализировать сплошной текст
            if (answers.Count == 0)
            {
                await _botClient.SendTextMessageAsync(
                    chatId: message.Chat.Id,
                    text: "Не удалось распознать ответы в структурированном формате.\n" +
                          "Пожалуйста, укажи номер вопроса перед каждым ответом, например:\n" +
                          "1. Имя\n2. Возраст\n3. Учёба\n...",
                    replyToMessageId: message.MessageId,
                    cancellationToken: _cts.Token);
                return;
            }

            // Сохраняем найденные ответы
            foreach (var (questionNumber, answer) in answers)
            {
                userInfo.UpdateAnswer(questionNumber, answer);
            }

            _userInfoStorage.AddOrUpdateUserInfo(message.From.Id, userInfo);

            await _botClient.SendTextMessageAsync(
                chatId: message.Chat.Id,
                text: "Спасибо за ответы! Я сохранил информацию по следующим пунктам: " +
                      string.Join(", ", answers.Keys.OrderBy(k => k)) + ".\n" +
                      "Вы можете добавить или изменить информацию с помощью команды /edit",
                replyToMessageId: message.MessageId,
                cancellationToken: _cts.Token);
        }

        private string FormatUserInfo(UserInfo userInfo)
        {
            var username = !string.IsNullOrEmpty(userInfo.Username)
                ? $"@{userInfo.Username}"
                : $"[{userInfo.FirstName} {userInfo.LastName}](tg://user?id={userInfo.UserId})";

            var infoLines = new List<string>
            {
                $"👤 *Пользователь:* {username}",
                $"🆔 *ID:* {userInfo.UserId}"
            };

            var questions = new Dictionary<int, string>
            {
                {1, "Имя"},
                {2, "Возраст"},
                {3, "Учёба"},
                {4, "Специальность"},
                {5, "Работа"},
                {6, "Занятие"},
                {7, "Страна"},
                {8, "Город"},
                {9, "Ник на баффе"},
                {10, "День рождения"},
                {11, "Собираемые тайтлы"}
            };

            foreach (var (number, question) in questions)
            {
                infoLines.Add(userInfo.Answers.TryGetValue(number, out var answer)
                    ? $"*{question}:* {answer}"
                    : $"*{question}:* неизвестно");
            }

            return string.Join("\n", infoLines);
        }

        private async Task SendWelcomeMessage(long chatId, User newUser)
        {
            string username = !string.IsNullOrEmpty(newUser.Username)
                ? $"@{newUser.Username}"
                : $"[{newUser.FirstName} {newUser.LastName}](tg://user?id={newUser.Id})";

            string welcomeMessage = $"{username}, добро пожаловать! 😁\n\n" +
                "И так рубрика стандартные вопросы:\n" +
                "1. Как тебя зовут?\n" +
                "2. Сколько тебе лет?\n" +
                "3. Где учишься?\n" +
                "4. Если не в школе, то на кого учишься?\n" +
                "5. Если не учишься вообще, то кем работаешь?\n" +
                "6. Если не работаешь, то чем занимаешься?\n" +
                "7. Из какой ты страны? (по желанию)\n" +
                "8. Из какого ты города? (по желанию)\n" +
                "9. Какой у тебя ник на баффе?\n" +
                "10. Когда у тебя день рождения?\n" +
                "11. Какие тайтлы собираешь?\n\n" +
                "Ответь на вопросы в формате:\n" +
                "1. ответ\n" +
                "2. ответ\n" +
                "...";

            await _botClient.SendTextMessageAsync(
                chatId: chatId,
                text: welcomeMessage,
                parseMode: ParseMode.Markdown,
                cancellationToken: _cts.Token);
        }

        private Task HandlePollingErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
        {
            var errorMessage = exception switch
            {
                ApiRequestException apiRequestException
                    => $"Telegram API Error:\n[{apiRequestException.ErrorCode}]\n{apiRequestException.Message}",
                _ => exception.ToString()
            };

            _logger.LogError(errorMessage);
            return Task.CompletedTask;
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            _cts.Cancel();
            await base.StopAsync(cancellationToken);
        }
    }
}