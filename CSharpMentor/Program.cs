using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpClient("gemini", client =>
{
    client.BaseAddress = new Uri("https://generativelanguage.googleapis.com/");
    client.DefaultRequestHeaders.Accept.Add(
        new MediaTypeWithQualityHeaderValue("application/json"));
    client.Timeout = TimeSpan.FromSeconds(90);
});
builder.Services.AddHttpClient("files");

var app = builder.Build();

// ─────────────────────────────────────────────────────────────
// Конфигурация
// ─────────────────────────────────────────────────────────────
var config = app.Services.GetRequiredService<IConfiguration>();

var apiKey = Environment.GetEnvironmentVariable("GeminiApiKey")
          ?? Environment.GetEnvironmentVariable("GEMINI_API_KEY")
          ?? config["GeminiApiKey"] ?? "";

var botToken = Environment.GetEnvironmentVariable("TelegramBotToken")
            ?? Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN")
            ?? config["TelegramBotToken"] ?? "";

var httpFactory = app.Services.GetRequiredService<IHttpClientFactory>();
var logger      = app.Services.GetRequiredService<ILogger<Program>>();

// ─────────────────────────────────────────────────────────────
// Системные промпты
// ─────────────────────────────────────────────────────────────
const string SystemPrompt = """
Ты — дружелюбный и опытный ментор по языку программирования C#.
Твоя единственная задача — помогать студентам, которые изучают C#.

Правила:
1. Отвечай ТОЛЬКО на вопросы, связанные с C#, .NET, программированием или смежными технологиями.
2. Если вопрос не связан с C# или программированием — вежливо объясни, что ты специализируешься только на C#.
3. Объясняй простым и понятным языком, избегай излишнего академизма.
4. Всегда давай рабочий пример кода на современном C# (C# 12+).
5. Разбирай каждую строчку кода кратким комментарием.
6. Используй жизненные аналогии чтобы объяснить абстрактные концепции.
7. Структурируй ответ: объяснение → код с комментариями → разбор → аналогия/совет.
8. При объяснении ошибок: неправильный код → причина → исправленный код.
9. Если прислали скриншот — разбери код или ошибку на нём.
10. Если прислали .cs файл — проанализируй код, найди проблемы, предложи улучшения.
11. Форматируй код в блоках ```csharp.
""";

const string QuizSystemPrompt = """
Ты — строгий но справедливый экзаменатор по языку C#.
Твоя задача — проверять знания студентов через вопросы с вариантами ответов.

Правила генерации вопроса:
1. Задавай вопрос по одной конкретной теме C# (типы данных, ООП, LINQ, async/await, исключения и т.д.).
2. Давай РОВНО 4 варианта ответа: A, B, C, D.
3. Только один вариант правильный.
4. Вопрос должен быть практическим — про поведение кода, а не определения.
5. Формат ответа СТРОГО такой (без лишнего текста до и после):

❓ *Вопрос*

```csharp
// код если нужен
```

A) вариант
B) вариант
C) вариант
D) вариант

||Правильный ответ: X) вариант
Объяснение: почему именно этот ответ верный||
""";

// ─────────────────────────────────────────────────────────────
// Хранение состояния пользователей (в памяти)
// ─────────────────────────────────────────────────────────────

// Потокобезопасный словарь: userId → состояние
var userStates = new ConcurrentDictionary<long, UserState>();

UserState GetState(long userId)
{
    var state = userStates.GetOrAdd(userId, _ => new UserState());
    state.LastActivity = DateTime.UtcNow;
    return state;
}

void AddToHistory(UserState state, string role, string text)
{
    state.History.Add(new ChatMessage(role, text));
    // Держим не более 10 сообщений (5 пар вопрос-ответ)
    if (state.History.Count > 10)
        state.History.RemoveAt(0);
}

// ─────────────────────────────────────────────────────────────
// Inline-кнопки после ответа
// ─────────────────────────────────────────────────────────────
InlineKeyboardMarkup GetAnswerKeyboard() => new(new[]
{
    new[]
    {
        InlineKeyboardButton.WithCallbackData("👍 Понятно", "feedback_good"),
        InlineKeyboardButton.WithCallbackData("🔁 Другой пример", "another_example"),
    },
    new[]
    {
        InlineKeyboardButton.WithCallbackData("📖 Объясни проще", "explain_simpler"),
        InlineKeyboardButton.WithCallbackData("🎯 Ещё вопрос (quiz)", "start_quiz"),
    }
});

InlineKeyboardMarkup GetQuizKeyboard() => new(new[]
{
    new[]
    {
        InlineKeyboardButton.WithCallbackData("➡️ Следующий вопрос", "quiz_next"),
        InlineKeyboardButton.WithCallbackData("🚪 Выйти из квиза", "quiz_exit"),
    }
});

// ─────────────────────────────────────────────────────────────
// Скачать файл из Telegram
// ─────────────────────────────────────────────────────────────
async Task<(byte[] Data, string MimeType)?> DownloadTelegramFileAsync(
    ITelegramBotClient bot, string fileId, string defaultMime)
{
    try
    {
        var file = await bot.GetFile(fileId);
        if (file.FilePath is null) return null;

        var url  = $"https://api.telegram.org/file/bot{botToken}/{file.FilePath}";
        var http = httpFactory.CreateClient("files");
        var data = await http.GetByteArrayAsync(url);

        var ext  = Path.GetExtension(file.FilePath).ToLowerInvariant();
        var mime = ext switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png"            => "image/png",
            ".gif"            => "image/gif",
            ".webp"           => "image/webp",
            _                 => defaultMime
        };
        return (data, mime);
    }
    catch (Exception ex)
    {
        logger.LogError("Ошибка скачивания файла: {Msg}", ex.Message);
        return null;
    }
}

// ─────────────────────────────────────────────────────────────
// Вызов Gemini API — общий метод
// ─────────────────────────────────────────────────────────────
async Task<string> CallGeminiAsync(object body)
{
    var content = new StringContent(
        JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
    var url = $"v1beta/models/gemini-3.5-flash-lite:generateContent?key={apiKey}";

    try
    {
        var http     = httpFactory.CreateClient("gemini");
        var response = await http.PostAsync(url, content);
        var raw      = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            try
            {
                using var errDoc = JsonDocument.Parse(raw);
                var msg = errDoc.RootElement
                    .GetProperty("error").GetProperty("message").GetString();
                return $"❌ Ошибка Gemini: {msg}";
            }
            catch { return $"❌ Ошибка Gemini: {raw}"; }
        }

        using var doc = JsonDocument.Parse(raw);
        return doc.RootElement
            .GetProperty("candidates")[0]
            .GetProperty("content")
            .GetProperty("parts")[0]
            .GetProperty("text")
            .GetString() ?? "Пустой ответ.";
    }
    catch (Exception ex) { return $"❌ Ошибка сети: {ex.Message}"; }
}

// ─────────────────────────────────────────────────────────────
// Gemini — текст с историей диалога
// ─────────────────────────────────────────────────────────────
async Task<string> AskGeminiWithHistoryAsync(UserState state, string newQuestion)
{
    if (string.IsNullOrWhiteSpace(apiKey))
        return "❌ API-ключ Gemini не настроен.";

    // Формируем contents из истории + новый вопрос
    var contents = new List<object>();
    foreach (var msg in state.History)
        contents.Add(new { role = msg.Role, parts = new[] { new { text = msg.Text } } });
    contents.Add(new { role = "user", parts = new[] { new { text = newQuestion } } });

    var body = new
    {
        system_instruction = new { parts = new[] { new { text = SystemPrompt } } },
        contents,
        generationConfig = new { temperature = 0.7, maxOutputTokens = 8192 }
    };

    return await CallGeminiAsync(body);
}

// ─────────────────────────────────────────────────────────────
// Gemini — фото + текст
// ─────────────────────────────────────────────────────────────
async Task<string> AskGeminiWithImageAsync(string question, byte[] imageData, string mimeType)
{
    if (string.IsNullOrWhiteSpace(apiKey)) return "❌ API-ключ Gemini не настроен.";

    var base64 = Convert.ToBase64String(imageData);
    var body = new
    {
        system_instruction = new { parts = new[] { new { text = SystemPrompt } } },
        contents = new[]
        {
            new
            {
                role = "user",
                parts = new object[]
                {
                    new { inline_data = new { mime_type = mimeType, data = base64 } },
                    new { text = string.IsNullOrWhiteSpace(question)
                        ? "Разбери код или ошибку на этом скриншоте. Объясни что происходит и как исправить."
                        : question }
                }
            }
        },
        generationConfig = new { temperature = 0.7, maxOutputTokens = 8192 }
    };
    return await CallGeminiAsync(body);
}

// ─────────────────────────────────────────────────────────────
// Gemini — .cs файл
// ─────────────────────────────────────────────────────────────
async Task<string> AskGeminiWithFileAsync(string question, byte[] fileData, string fileName, UserState state)
{
    var fileText = Encoding.UTF8.GetString(fileData);
    var prompt = string.IsNullOrWhiteSpace(question)
        ? $"Проанализируй этот C# код из файла `{fileName}`. Найди проблемы, объясни что он делает и предложи улучшения:\n\n```csharp\n{fileText}\n```"
        : $"{question}\n\nВот код из файла `{fileName}`:\n\n```csharp\n{fileText}\n```";

    return await AskGeminiWithHistoryAsync(state, prompt);
}

// ─────────────────────────────────────────────────────────────
// Gemini — квиз: генерация вопроса
// ─────────────────────────────────────────────────────────────
async Task<string> GenerateQuizQuestionAsync()
{
    if (string.IsNullOrWhiteSpace(apiKey)) return "❌ API-ключ Gemini не настроен.";

    var body = new
    {
        system_instruction = new { parts = new[] { new { text = QuizSystemPrompt } } },
        contents = new[]
        {
            new { role = "user", parts = new[] { new { text = "Сгенерируй новый вопрос по C# для студента уровня Junior." } } }
        },
        generationConfig = new { temperature = 1.0, maxOutputTokens = 1024 }
    };
    return await CallGeminiAsync(body);
}

// ─────────────────────────────────────────────────────────────
// Отправить ответ с разбивкой на части
// ─────────────────────────────────────────────────────────────
async Task SendAnswerAsync(ITelegramBotClient bot, long chatId, string answer,
    CancellationToken ct, InlineKeyboardMarkup? keyboard = null)
{
    const int maxLen = 4000;

    async Task SendChunk(string chunk, bool withKeyboard)
    {
        try
        {
            await bot.SendMessage(chatId, chunk,
                parseMode: ParseMode.Markdown,
                replyMarkup: withKeyboard ? keyboard : null,
                cancellationToken: ct);
        }
        catch
        {
            await bot.SendMessage(chatId, chunk,
                replyMarkup: withKeyboard ? keyboard : null,
                cancellationToken: ct);
        }
    }

    if (answer.Length <= maxLen)
    {
        await SendChunk(answer, true);
        return;
    }

    var parts = new List<string>();
    for (int i = 0; i < answer.Length; i += maxLen)
        parts.Add(answer.Substring(i, Math.Min(maxLen, answer.Length - i)));

    for (int i = 0; i < parts.Count; i++)
    {
        await SendChunk(parts[i], i == parts.Count - 1); // кнопки только в последней части
        if (i < parts.Count - 1) await Task.Delay(300, ct);
    }
}

// ─────────────────────────────────────────────────────────────
// Telegram Bot
// ─────────────────────────────────────────────────────────────
if (!string.IsNullOrWhiteSpace(botToken))
{
    var botClient = new TelegramBotClient(botToken);
    var cts       = new CancellationTokenSource();

    botClient.StartReceiving(
        updateHandler: async (bot, update, ct) =>
        {
            // ── Обработка нажатий на inline-кнопки ──────────
            if (update.CallbackQuery is { } query)
            {
                var userId   = query.From.Id;
                var chatId   = query.Message!.Chat.Id;
                var state    = GetState(userId);
                var data     = query.Data ?? "";

                await bot.AnswerCallbackQuery(query.Id, cancellationToken: ct);

                switch (data)
                {
                    case "feedback_good":
                        await bot.SendMessage(chatId,
                            "Отлично! 💪 Задавай следующий вопрос или напиши /quiz для проверки знаний.",
                            cancellationToken: ct);
                        break;

                    case "another_example":
                        await bot.SendChatAction(chatId, ChatAction.Typing, cancellationToken: ct);
                        var anotherEx = await AskGeminiWithHistoryAsync(state,
                            "Покажи ещё один пример по той же теме, другой и более наглядный.");
                        AddToHistory(state, "model", anotherEx);
                        await SendAnswerAsync(bot, chatId, anotherEx, ct, GetAnswerKeyboard());
                        break;

                    case "explain_simpler":
                        await bot.SendChatAction(chatId, ChatAction.Typing, cancellationToken: ct);
                        var simpler = await AskGeminiWithHistoryAsync(state,
                            "Объясни то же самое, но ещё проще — как будто я полный новичок. Используй бытовую аналогию.");
                        AddToHistory(state, "model", simpler);
                        await SendAnswerAsync(bot, chatId, simpler, ct, GetAnswerKeyboard());
                        break;

                    case "start_quiz":
                    case "quiz_next":
                        state.Mode = UserMode.Quiz;
                        await bot.SendChatAction(chatId, ChatAction.Typing, cancellationToken: ct);
                        var question = await GenerateQuizQuestionAsync();
                        await SendAnswerAsync(bot, chatId, question, ct, GetQuizKeyboard());
                        break;

                    case "quiz_exit":
                        state.Mode = UserMode.Normal;
                        await bot.SendMessage(chatId,
                            "Вышел из режима квиза. Задавай вопросы по C# 💬",
                            cancellationToken: ct);
                        break;
                }
                return;
            }

            // ── Обработка обычных сообщений ──────────────────
            if (update.Message is not { } message) return;

            var msgChatId  = message.Chat.Id;
            var msgUserId  = message.From?.Id ?? 0;
            var msgState   = GetState(msgUserId);
            var username   = message.From?.FirstName ?? "Студент";
            var caption    = message.Caption ?? "";

            // /start
            if (message.Text == "/start")
            {
                msgState.History.Clear();
                msgState.Mode = UserMode.Normal;
                await bot.SendMessage(msgChatId,
                    $"👋 Привет, *{username}*!\n\n" +
                    "Я — *C# Mentor*, твой ИИ-помощник по C#.\n\n" +
                    "Что умею:\n" +
                    "💬 Отвечать на вопросы по C# и .NET\n" +
                    "🧠 Помню контекст нашего диалога\n" +
                    "📷 Разбирать скриншоты с кодом и ошибками\n" +
                    "📄 Анализировать .cs файлы\n" +
                    "🎯 Проверять знания через квиз (/quiz)\n\n" +
                    "Просто напиши вопрос! 🚀",
                    parseMode: ParseMode.Markdown, cancellationToken: ct);
                return;
            }

            // /help
            if (message.Text == "/help")
            {
                await bot.SendMessage(msgChatId,
                    "*Команды:*\n\n" +
                    "/start — начать заново\n" +
                    "/quiz — режим проверки знаний\n" +
                    "/clear — очистить историю диалога\n" +
                    "/help — эта справка\n\n" +
                    "*Можно присылать:*\n" +
                    "💬 Текстовый вопрос\n" +
                    "📷 Скриншот с кодом или ошибкой\n" +
                    "📄 Файл .cs с кодом",
                    parseMode: ParseMode.Markdown, cancellationToken: ct);
                return;
            }

            // /clear — сброс истории
            if (message.Text == "/clear")
            {
                msgState.History.Clear();
                msgState.Mode = UserMode.Normal;
                await bot.SendMessage(msgChatId,
                    "🗑 История очищена. Начинаем с чистого листа!",
                    cancellationToken: ct);
                return;
            }

            // /quiz — запуск квиза
            if (message.Text == "/quiz")
            {
                msgState.Mode = UserMode.Quiz;
                await bot.SendChatAction(msgChatId, ChatAction.Typing, cancellationToken: ct);
                await bot.SendMessage(msgChatId,
                    "🎯 *Режим квиза активирован!*\n\nОтвечай на вопросы — проверим твои знания C#.",
                    parseMode: ParseMode.Markdown, cancellationToken: ct);
                var q = await GenerateQuizQuestionAsync();
                await SendAnswerAsync(bot, msgChatId, q, ct, GetQuizKeyboard());
                return;
            }

            await bot.SendChatAction(msgChatId, ChatAction.Typing, cancellationToken: ct);

            // ── Фото ─────────────────────────────────────────
            if (message.Photo is { } photos && photos.Length > 0)
            {
                logger.LogInformation("TG [{User}]: фото", username);
                var file = await DownloadTelegramFileAsync(bot, photos[^1].FileId, "image/jpeg");
                if (file is null)
                {
                    await bot.SendMessage(msgChatId, "❌ Не удалось скачать фото.", cancellationToken: ct);
                    return;
                }
                var ans = await AskGeminiWithImageAsync(caption, file.Value.Data, file.Value.MimeType);
                msgState.LastBotAnswer = ans;
                AddToHistory(msgState, "user", string.IsNullOrWhiteSpace(caption) ? "[фото с кодом]" : caption);
                AddToHistory(msgState, "model", ans);
                await SendAnswerAsync(bot, msgChatId, ans, ct, GetAnswerKeyboard());
                return;
            }

            // ── Документ ─────────────────────────────────────
            if (message.Document is { } doc)
            {
                var fileName = doc.FileName ?? "file";
                var ext      = Path.GetExtension(fileName).ToLowerInvariant();
                var isImage  = ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp";
                var isCode   = ext is ".cs" or ".txt" or ".json" or ".xml" or ".csproj";

                logger.LogInformation("TG [{User}]: файл '{File}'", username, fileName);

                var file = await DownloadTelegramFileAsync(bot, doc.FileId,
                    isImage ? "image/jpeg" : "text/plain");

                if (file is null)
                {
                    await bot.SendMessage(msgChatId, "❌ Не удалось скачать файл.", cancellationToken: ct);
                    return;
                }

                string ans;
                if (isImage)
                    ans = await AskGeminiWithImageAsync(caption, file.Value.Data, file.Value.MimeType);
                else if (isCode)
                    ans = await AskGeminiWithFileAsync(caption, file.Value.Data, fileName, msgState);
                else
                {
                    await bot.SendMessage(msgChatId,
                        $"⚠️ Формат `{ext}` не поддерживается.\nПришли `.cs`, `.txt` или скриншот.",
                        cancellationToken: ct);
                    return;
                }

                msgState.LastBotAnswer = ans;
                AddToHistory(msgState, "user", $"[файл {fileName}] {caption}".Trim());
                AddToHistory(msgState, "model", ans);
                await SendAnswerAsync(bot, msgChatId, ans, ct, GetAnswerKeyboard());
                return;
            }

            // ── Текст ─────────────────────────────────────────
            if (message.Text is { } text)
            {
                logger.LogInformation("TG [{User}] [{Mode}]: {Text}", username, msgState.Mode, text);

                string answer;

                if (msgState.Mode == UserMode.Quiz)
                {
                    // В режиме квиза — принимаем ответ студента и оцениваем
                    var evalPrompt = $"Студент ответил: \"{text}\"\n\nОцени ответ. Если правильно — похвали и объясни почему верно. Если неверно — объясни ошибку и дай правильный ответ с объяснением.";
                    answer = await AskGeminiWithHistoryAsync(msgState, evalPrompt);
                }
                else
                {
                    // Обычный режим — с историей диалога
                    answer = await AskGeminiWithHistoryAsync(msgState, text);
                }

                msgState.LastUserQuestion = text;
                msgState.LastBotAnswer    = answer;
                AddToHistory(msgState, "user",  text);
                AddToHistory(msgState, "model", answer);

                var keyboard = msgState.Mode == UserMode.Quiz ? GetQuizKeyboard() : GetAnswerKeyboard();
                await SendAnswerAsync(bot, msgChatId, answer, ct, keyboard);
                return;
            }

            // Всё остальное (стикеры, голосовые и т.д.)
            await bot.SendMessage(msgChatId,
                "Понимаю текст, фото и .cs файлы.\nГолосовые пока не поддерживаю 🎤",
                cancellationToken: ct);
        },

        errorHandler: (bot, ex, src, ct) =>
        {
            logger.LogError(ex, "Ошибка Telegram polling");
            return Task.CompletedTask;
        },
        receiverOptions: new ReceiverOptions { AllowedUpdates = [UpdateType.Message, UpdateType.CallbackQuery] },
        cancellationToken: cts.Token
    );

    try
    {
        var me = await botClient.GetMe();
        logger.LogInformation("✅ Telegram бот запущен: @{Username}", me.Username);
    }
    catch (Exception ex)
    {
        logger.LogError("❌ Ошибка подключения к Telegram: {Msg}", ex.Message);
    }
}
else
{
    logger.LogWarning("TelegramBotToken не задан — бот не запущен.");
}

// ─────────────────────────────────────────────────────────────
// Веб-часть
// ─────────────────────────────────────────────────────────────
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapPost("/api/ask", async (AskRequest request) =>
{
    if (string.IsNullOrWhiteSpace(apiKey))
        return Results.Json(new { error = "API-ключ не найден." }, statusCode: 500);
    if (string.IsNullOrWhiteSpace(request.Question))
        return Results.BadRequest(new { error = "Вопрос не может быть пустым." });

    // Для веб-версии — без истории (stateless)
    var body = new
    {
        system_instruction = new { parts = new[] { new { text = SystemPrompt } } },
        contents = new[] { new { role = "user", parts = new[] { new { text = request.Question } } } },
        generationConfig = new { temperature = 0.7, maxOutputTokens = 8192 }
    };
    var answer = await CallGeminiAsync(body);
    return answer.StartsWith("❌")
        ? Results.Json(new { error = answer }, statusCode: 500)
        : Results.Json(new { answer });
});

app.Run();

record AskRequest(string Question);

// ─────────────────────────────────────────────────────────────
// Типы данных (должны быть после top-level statements)
// ─────────────────────────────────────────────────────────────

enum UserMode { Normal, Quiz }

record ChatMessage(string Role, string Text);

class UserState
{
    public UserMode Mode { get; set; } = UserMode.Normal;
    public List<ChatMessage> History { get; } = new();
    public string LastBotAnswer { get; set; } = "";
    public string LastUserQuestion { get; set; } = "";
    public DateTime LastActivity { get; set; } = DateTime.UtcNow;
}
