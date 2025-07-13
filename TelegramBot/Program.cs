using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Net;
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
                Answers[questionNumber] = WebUtility.HtmlEncode(answer);
            }
        }
    }

    public class BotBackgroundService : BackgroundService
    {
        private readonly ILogger<BotBackgroundService> _logger;
        private readonly UserInfoStorage _userInfoStorage;
        private TelegramBotClient _botClient;
        private CancellationTokenSource _cts;

        private readonly List<string> _opgPantiesMessages = new()
        {
            "Наше ОПГ самое лучшее!!!",
            "ОПГ трусики: жёсткая посадка гарантирована!",
            "ОПГ трусики: нелегальный оборот кружев!",
            "Главный враг 'Опг трусики' – стиральная машина в режиме 'отжим'.",
            "Мы не прячем 'бабло' в офшорах – только в потайном кармашке для ключей.",
            "Нас 'крышует' не мафия, а хлопковая ткань с добавлением эластина.",
            "У нас строгая иерархия: кружево – это для боссов, хлопок – для солдат, а стринги – это наёмные убийцы.",
            "Наш лучший отмыватель денег – это отбеливатель.",
            "На зоне 'опущенные' носят розовое, а у нас это привилегия топ-моделей.",
            "В нашей банде новичков не 'крестят', а 'пристягивают'.",
            "Мы делаем предложение, от которого нельзя отказаться… потому что резинка слишком тугая.",
            "Фраза 'завалить дело' у нас означает 'сесть на стирку'.",
            "Если ты нас предал, мы не станем тебя убивать… мы просто отправим тебя в сушку на максимальных оборотах."
        };

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

            var receiverOptions = new ReceiverOptions
            {
                AllowedUpdates = Array.Empty<UpdateType>()
            };

            _botClient.StartReceiving(
                updateHandler: HandleUpdateAsync,
                pollingErrorHandler: HandlePollingErrorAsync,
                receiverOptions: receiverOptions,
                cancellationToken: _cts.Token
            );

            var me = await _botClient.GetMeAsync();
            _logger.LogInformation($"Бот @{me.Username} запущен!");

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

                if (message.Entities?.FirstOrDefault()?.Type == MessageEntityType.BotCommand)
                {
                    await HandleCommandAsync(message);
                    return;
                }

                if (message.ReplyToMessage?.Text?.Contains("добро пожаловать") ?? false)
                {
                    await HandleUserAnswers(message);
                    return;
                }

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
                case "/opgpanties":
                    await HandleOpgPantiesCommand(message);
                    break;
            }
        }

        private async Task HandleOpgPantiesCommand(Message message)
        {
            var random = new Random();
            int index = random.Next(_opgPantiesMessages.Count);
            string randomMessage = _opgPantiesMessages[index];

            await _botClient.SendTextMessageAsync(
                chatId: message.Chat.Id,
                text: randomMessage,
                replyToMessageId: message.MessageId,
                cancellationToken: _cts.Token);
        }

        private async Task HandleInfoCommand(Message message)
        {
            var parts = message.Text.Split(' ');

            if (parts.Length > 1 && parts[1].Equals("opg", StringComparison.OrdinalIgnoreCase))
            {
                var allInfos = _userInfoStorage.GetAllUserInfos().ToList();
                var response = new StringBuilder("📊 *Полная информация о всех пользователях:*\n\n");

                foreach (var userInfo in allInfos)
                {
                    var userText = FormatUserInfo(userInfo);
                    if (response.Length + userText.Length > 4000)
                    {
                        await _botClient.SendTextMessageAsync(
                            chatId: message.Chat.Id,
                            text: response.ToString(),
                            parseMode: ParseMode.Markdown,
                            cancellationToken: _cts.Token);
                        response.Clear();
                    }
                    response.AppendLine(userText).AppendLine();
                }

                if (response.Length > 0)
                {
                    await _botClient.SendTextMessageAsync(
                        chatId: message.Chat.Id,
                        text: response.ToString(),
                        parseMode: ParseMode.Markdown,
                        replyToMessageId: message.MessageId,
                        cancellationToken: _cts.Token);
                }
                return;
            }

            if (parts.Length == 1)
            {
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
                          "Для редактирования напишите:\n/edit @username\nномер. новое значение",
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
                    text: $"Для добавления пользователя @{username} напишите команду:\n/add @username\nномер. значение\nномер. значение\n...",
                    replyToMessageId: message.MessageId,
                    cancellationToken: _cts.Token);
                return;
            }

            var newUserInfo = new UserInfo
            {
                Username = username,
                UserId = -1
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

        private async Task HandleUserAnswers(Message message)
        {
            var userInfo = new UserInfo
            {
                UserId = message.From.Id,
                Username = message.From.Username,
                FirstName = message.From.FirstName,
                LastName = message.From.LastName
            };

            var text = message.Text;
            var answers = new Dictionary<int, string>();

            var matches = Regex.Matches(text, @"(\d+)[.)]\s*([^\n]+)");
            foreach (Match match in matches)
            {
                if (int.TryParse(match.Groups[1].Value, out var questionNumber))
                {
                    answers[questionNumber] = match.Groups[2].Value.Trim();
                }
            }

            if (answers.Count == 0)
            {
                await _botClient.SendTextMessageAsync(
                    chatId: message.Chat.Id,
                    text: "Вы написали информацию в неверном формате.\n" +
                          "Пожалуйста, укажи номер вопроса перед каждым ответом, пример:\n" +
                          "1. Имя\n2. Возраст\n3. Учёба\nи т.д",
                    replyToMessageId: message.MessageId,
                    cancellationToken: _cts.Token);
                return;
            }

            foreach (var (questionNumber, answer) in answers)
            {
                userInfo.UpdateAnswer(questionNumber, answer);
            }

            _userInfoStorage.AddOrUpdateUserInfo(message.From.Id, userInfo);

            await _botClient.SendTextMessageAsync(
                chatId: message.Chat.Id,
                text: "Спасибо за ответы! Я сохранил информацию по следующим пунктам: " +
                      string.Join(", ", answers.Keys.OrderBy(k => k)) + ".\n" +
                      "С помощью команды /info @username – вы можете просмотреть информацию о конкретном человеке\n" +
                      "С помощью команды /info opg – вы сможете просмотреть информацию по всем членам клуба\n" +
                      "А также вы можете прописать волшебную команду /OpgPanties и посмотреть что будет 😉",
                replyToMessageId: message.MessageId,
                cancellationToken: _cts.Token);
        }

        private string FormatUserInfo(UserInfo userInfo)
        {
            var username = !string.IsNullOrEmpty(userInfo.Username)
                ? $"@{userInfo.Username}"
                : string.IsNullOrEmpty(userInfo.FirstName)
                    ? $"[ID{userInfo.UserId}](tg://user?id={userInfo.UserId})"
                    : $"[{userInfo.FirstName} {userInfo.LastName}](tg://user?id={userInfo.UserId})";

            var infoLines = new List<string>
            {
                $"👤 *Пользователь:* {username}",
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
                {11, "Собираемые тайтлы"},
                {12, "Слышал ли про волшебные чебуреки?"},
                {13, "Есть ли они у тебя?"},
                {14, "Если есть, то сколько?"}
            };

            foreach (var (number, question) in questions)
            {
                if (userInfo.Answers.TryGetValue(number, out var answer))
                {
                    infoLines.Add($"*{question}:* {answer}");
                }
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
                "11. Какие тайтлы собираешь?\n" +
                "12. Ответь на вопрос, но только честно, слышал ли ты про волшебные чебуреки?)\n" +
                "13. Если слышал, есть ли они у тебя?\n" +
                "14. Если есть, то сколько?)))\n\n" +
                "Ответь на вопросы в формате:\n" +
                "1. ответ\n" +
                "2. ответ\n" +
                "и т.д";

            await _botClient.SendTextMessageAsync(
                chatId: chatId,
                text: welcomeMessage,
                parseMode: ParseMode.Markdown,
                cancellationToken: _cts.Token);
        }

        private async Task SendEditUsage(Message message)
        {
            await _botClient.SendTextMessageAsync(
                chatId: message.Chat.Id,
                text: "Использование команды /edit:\n" +
                      "/edit @username\n" +
                      "номер. новое значение\n" +
                      "номер. новое значение\n\n" +
                      "Пример:\n" +
                      "/edit @Superchel\n" +
                      "2. 25 лет\n" +
                      "5. Работаю программистом",
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
                      "1: Авдотья\n" +
                      "2: 25 лет\n" +
                      "5: Программист",
                replyToMessageId: message.MessageId,
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