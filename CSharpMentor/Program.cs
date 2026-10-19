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
Ты — экзаменатор по языку C# для начинающих. Генерируй вопросы по БАЗОВЫМ темам.

Темы (только из этого списка, чередуй их):
- Типы данных (int, string, bool, double, char)
- Переменные и константы (var, const)
- Арифметические и логические операторы
- Условия (if / else / switch)
- Циклы (for, while, foreach, do-while)
- Методы: объявление, параметры, возвращаемые значения
- Массивы: объявление, индексы, длина
- List<T>: Add, Remove, Count, Contains
- Строки: Length, Substring, Contains, Replace, ToUpper
- Null и nullable типы (?), проверка на null

Правила:
1. Вопрос ОБЯЗАТЕЛЬНО должен быть про КОНКРЕТНОЕ поведение кода — что выведет программа, какое значение получится.
2. РОВНО 4 варианта: A, B, C, D. Только один правильный.
3. Уровень сложности — начинающий (первые 2-3 недели изучения C#).
4. НЕ повторяй тему из списка уже использованных тем в этой сессии.
5. Отвечай СТРОГО в формате JSON (без markdown вокруг, без пояснений):

{
  "question": "Текст вопроса",
  "code": "// C# код если нужен, иначе пустая строка",
  "a": "Вариант A",
  "b": "Вариант B",
  "c": "Вариант C",
  "d": "Вариант D",
  "correct": "A",
  "explanation": "Краткое объяснение почему этот ответ верный",
  "topic": "Название темы из списка выше"
}
""";

const string ChallengeSystemPrompt = """
Ты — тренер по C# который даёт задания на исправление сломанного кода.

Темы заданий (чередуй, не повторяй использованные):
- Неверный тип переменной
- Ошибка в условии if/else
- Ошибка в цикле (for, while, foreach)
- Ошибка в объявлении/вызове метода
- Выход за границы массива
- NullReferenceException
- Неверный оператор сравнения (= вместо ==)
- Ошибка в работе со строками
- Ошибка с List<T>
- Неверное возвращаемое значение метода

Правила генерации задания:
1. Дай короткий C# код (5-15 строк) с ОДНОЙ намеренной ошибкой из темы.
2. Уровень — начинающий (первые недели изучения C#).
3. Ошибка реалистичная — такую реально делают новички.
4. Отвечай СТРОГО в формате JSON:

{
  "topic": "Название темы из списка выше",
  "description": "Краткое описание задачи что должен делать код",
  "broken_code": "// сломанный C# код",
  "error_hint": "Подсказка без спойлера",
  "fixed_code": "// правильный C# код",
  "explanation": "Объяснение что было не так и почему"
}
""";

const string InterviewSystemPrompt = """
Ты — строгий технический интервьюер в IT-компании. Проводишь собеседование на позицию Junior C# разработчика.

Правила:
1. Задавай вопросы по одному, жди ответа студента.
2. Темы: основы C#, ООП, коллекции, исключения, базовый LINQ, async/await.
3. После ответа студента: оцени ответ (правильно/частично/неверно), дай краткое объяснение, задай следующий вопрос.
4. Будь строгим но справедливым. Не подсказывай заранее.
5. После 5 вопросов подведи итог: что знает хорошо, что нужно подтянуть, общая оценка.
6. Первое сообщение: поприветствуй как интервьюер и задай первый вопрос.
7. Формат строгий деловой, без лишних эмодзи.
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
// Rate Limiting — не более 25 запросов в час на пользователя
// ─────────────────────────────────────────────────────────────
const int MaxRequestsPerHour = 25;

bool IsRateLimited(UserState state)
{
    var now = DateTime.UtcNow;
    // Чистим запросы старше 1 часа
    state.RequestTimestamps.RemoveAll(t => (now - t).TotalHours >= 1);
    return state.RequestTimestamps.Count >= MaxRequestsPerHour;
}

void TrackRequest(UserState state)
{
    state.RequestTimestamps.Add(DateTime.UtcNow);
}

string GetRateLimitMessage(UserState state)
{
    var oldest = state.RequestTimestamps.Min();
    var resetIn = oldest.AddHours(1) - DateTime.UtcNow;
    return $"⏳ Лимит запросов исчерпан ({MaxRequestsPerHour}/час).\n" +
           $"Попробуй через {(int)resetIn.TotalMinutes + 1} мин.";
}
InlineKeyboardMarkup GetAnswerKeyboard() => new(new[]
{
    new[]
    {
        InlineKeyboardButton.WithCallbackData("� Другой пример", "another_example"),
        InlineKeyboardButton.WithCallbackData("� Проще", "explain_simpler"),
    },
    new[]
    {
        InlineKeyboardButton.WithCallbackData("� Понятно", "feedback_good"),
        InlineKeyboardButton.WithCallbackData("🎯 Квиз", "start_quiz"),
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
// Gemini — квиз: генерация вопроса → парсинг JSON
// ─────────────────────────────────────────────────────────────
async Task<QuizQuestion?> GenerateQuizQuestionAsync(List<string> usedTopics)
{
    if (string.IsNullOrWhiteSpace(apiKey)) return null;

    // Подсказываем Gemini какие темы уже были
    var usedHint = usedTopics.Count > 0
        ? $"Уже использованные темы (НЕ повторяй их): {string.Join(", ", usedTopics)}."
        : "Это первый вопрос в сессии.";

    var body = new
    {
        system_instruction = new { parts = new[] { new { text = QuizSystemPrompt } } },
        contents = new[]
        {
            new { role = "user", parts = new[] { new { text = $"Сгенерируй новый вопрос по C# для начинающего. {usedHint}" } } }
        },
        generationConfig = new { temperature = 1.0, maxOutputTokens = 1024, responseMimeType = "application/json" }
    };

    var raw = await CallGeminiAsync(body);

    try
    {
        var json = raw.Trim();
        if (json.StartsWith("```")) json = json.Split('\n', 2)[1];
        if (json.EndsWith("```")) json = json[..json.LastIndexOf("```")];

        var q = JsonSerializer.Deserialize<QuizQuestion>(json.Trim(),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        return q;
    }
    catch (Exception ex)
    {
        logger.LogError("Ошибка парсинга квиза: {Msg}\nRaw: {Raw}", ex.Message, raw);
        return null;
    }
}

// Формирует красивый текст вопроса для отправки
string FormatQuizQuestion(QuizQuestion q)
{
    var sb = new StringBuilder();
    sb.AppendLine("❓ *Вопрос:*");
    sb.AppendLine(q.Question);
    if (!string.IsNullOrWhiteSpace(q.Code))
    {
        sb.AppendLine();
        sb.AppendLine("```csharp");
        sb.AppendLine(q.Code);
        sb.AppendLine("```");
    }
    sb.AppendLine();
    sb.AppendLine($"A) {q.A}");
    sb.AppendLine($"B) {q.B}");
    sb.AppendLine($"C) {q.C}");
    sb.AppendLine($"D) {q.D}");
    sb.AppendLine();
    sb.Append("👇 Выбери ответ:");
    return sb.ToString();
}

// Кнопки A/B/C/D для квиза
InlineKeyboardMarkup GetQuizAnswerKeyboard() => new(new[]
{
    new[]
    {
        InlineKeyboardButton.WithCallbackData("A", "quiz_answer_A"),
        InlineKeyboardButton.WithCallbackData("B", "quiz_answer_B"),
        InlineKeyboardButton.WithCallbackData("C", "quiz_answer_C"),
        InlineKeyboardButton.WithCallbackData("D", "quiz_answer_D"),
    }
});

// Кнопки для challenge
InlineKeyboardMarkup GetChallengeKeyboard() => new(new[]
{
    new[]
    {
        InlineKeyboardButton.WithCallbackData("💡 Подсказка", "challenge_hint"),
        InlineKeyboardButton.WithCallbackData("✅ Показать ответ", "challenge_answer"),
    },
    new[]
    {
        InlineKeyboardButton.WithCallbackData("➡️ Новое задание", "challenge_next"),
        InlineKeyboardButton.WithCallbackData("🚪 Выйти", "challenge_exit"),
    }
});

// Кнопка выхода из интервью
InlineKeyboardMarkup GetInterviewKeyboard() => new(new[]
{
    new[]
    {
        InlineKeyboardButton.WithCallbackData("🚪 Завершить интервью", "interview_exit"),
    }
});

// ─────────────────────────────────────────────────────────────
// Gemini — генерация задания для challenge
// ─────────────────────────────────────────────────────────────
async Task<(string BrokenCode, string Hint, string FixedCode, string Explanation, string Description, string Topic)?> GenerateChallengeAsync(List<string> usedTopics)
{
    if (string.IsNullOrWhiteSpace(apiKey)) return null;

    var usedHint = usedTopics.Count > 0
        ? $"Уже использованные темы (НЕ повторяй): {string.Join(", ", usedTopics)}."
        : "Это первое задание в сессии.";

    var body = new
    {
        system_instruction = new { parts = new[] { new { text = ChallengeSystemPrompt } } },
        contents = new[]
        {
            new { role = "user", parts = new[] { new { text = $"Сгенерируй новое задание на исправление кода для начинающего C# разработчика. {usedHint}" } } }
        },
        generationConfig = new { temperature = 1.0, maxOutputTokens = 1024, responseMimeType = "application/json" }
    };

    var raw = await CallGeminiAsync(body);
    try
    {
        var json = raw.Trim();
        if (json.StartsWith("```")) json = json.Split('\n', 2)[1];
        if (json.EndsWith("```")) json = json[..json.LastIndexOf("```")];

        using var doc = JsonDocument.Parse(json.Trim());
        var root = doc.RootElement;
        return (
            root.GetProperty("broken_code").GetString() ?? "",
            root.GetProperty("error_hint").GetString() ?? "",
            root.GetProperty("fixed_code").GetString() ?? "",
            root.GetProperty("explanation").GetString() ?? "",
            root.GetProperty("description").GetString() ?? "",
            root.TryGetProperty("topic", out var t) ? t.GetString() ?? "" : ""
        );
    }
    catch (Exception ex)
    {
        logger.LogError("Ошибка парсинга challenge: {Msg}\nRaw: {Raw}", ex.Message, raw);
        return null;
    }
}

// Форматирует задание challenge для отправки
string FormatChallenge(string description, string brokenCode) =>
    $"🔧 *Задание: исправь код*\n\n" +
    $"📋 *Что должен делать код:*\n{description}\n\n" +
    $"❌ *Сломанный код:*\n```csharp\n{brokenCode}\n```\n\n" +
    $"✏️ Напиши исправленный вариант или нажми кнопку:";

// ─────────────────────────────────────────────────────────────
// Gemini — интервью с историей
// ─────────────────────────────────────────────────────────────
async Task<string> ConductInterviewAsync(UserState state, string userMessage)
{
    if (string.IsNullOrWhiteSpace(apiKey)) return "❌ API-ключ Gemini не настроен.";

    var contents = new List<object>();
    foreach (var msg in state.InterviewHistory)
        contents.Add(new { role = msg.Role, parts = new[] { new { text = msg.Text } } });
    contents.Add(new { role = "user", parts = new[] { new { text = userMessage } } });

    var body = new
    {
        system_instruction = new { parts = new[] { new { text = InterviewSystemPrompt } } },
        contents,
        generationConfig = new { temperature = 0.8, maxOutputTokens = 2048 }
    };

    return await CallGeminiAsync(body);
}

// ─────────────────────────────────────────────────────────────
// Roadmap — план изучения C#
// ─────────────────────────────────────────────────────────────
const string RoadmapText = """
🗺 *Роадмап изучения C# — от нуля до Junior*

━━━━━━━━━━━━━━━━━━━━━━
*📦 Этап 1 — Основы* (2-3 недели)
━━━━━━━━━━━━━━━━━━━━━━
• Типы данных: int, string, bool, double
• Переменные, константы, операторы
• Условия: if / else / switch
• Циклы: for / while / foreach
• Массивы и коллекции (List, Dictionary)
• Методы и параметры
• Null и nullable типы

━━━━━━━━━━━━━━━━━━━━━━
*🏗 Этап 2 — ООП* (3-4 недели)
━━━━━━━━━━━━━━━━━━━━━━
• Классы и объекты
• Конструкторы и свойства
• Наследование и полиморфизм
• Интерфейсы и абстрактные классы
• Инкапсуляция (public / private / protected)
• Перегрузка методов и операторов
• Generics (List<T>, Dictionary<K,V>)

━━━━━━━━━━━━━━━━━━━━━━
*⚡ Этап 3 — Продвинутый C#* (3-4 недели)
━━━━━━━━━━━━━━━━━━━━━━
• Исключения (try / catch / finally)
• Делегаты, события, лямбды
• LINQ (Where, Select, OrderBy, GroupBy)
• async / await и Task
• Работа с файлами (File, Stream)
• Строки и регулярные выражения

━━━━━━━━━━━━━━━━━━━━━━
*🌐 Этап 4 — .NET экосистема* (4-6 недель)
━━━━━━━━━━━━━━━━━━━━━━
• ASP.NET Core — создание веб-API
• Entity Framework Core — работа с БД
• Dependency Injection
• NuGet — пакетный менеджер
• Git и GitHub
• Юнит-тесты (xUnit / NUnit)

━━━━━━━━━━━━━━━━━━━━━━
*💼 Этап 5 — Перед первой работой*
━━━━━━━━━━━━━━━━━━━━━━
• Паттерны проектирования (Repository, CQRS)
• REST API, JSON, HTTP
• Docker основы
• Подготовка к техническому интервью

━━━━━━━━━━━━━━━━━━━━━━
💡 *Совет:* Практикуй каждую тему на реальных задачах.
Задавай вопросы — я объясню любую тему из роадмапа!

/quiz — проверь свои знания
""";

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
                        var question = await GenerateQuizQuestionAsync(state.UsedQuizTopics);
                        if (question is null)
                        {
                            await bot.SendMessage(chatId, "❌ Не удалось сгенерировать вопрос. Попробуй ещё раз.", cancellationToken: ct);
                            break;
                        }
                        state.CurrentQuiz = question;
                        state.QuizTotal++;
                        // Запоминаем тему чтобы не повторять
                        if (!string.IsNullOrWhiteSpace(question.Topic))
                            state.UsedQuizTopics.Add(question.Topic);
                        // После 10 вопросов сбрасываем список тем — начинаем новый круг
                        if (state.UsedQuizTopics.Count >= 10)
                            state.UsedQuizTopics.Clear();
                        await bot.SendMessage(chatId, FormatQuizQuestion(question),
                            parseMode: ParseMode.Markdown,
                            replyMarkup: GetQuizAnswerKeyboard(),
                            cancellationToken: ct);
                        break;

                    case "quiz_exit":
                        var score = $"📊 Твой результат: *{state.QuizCorrect}* из *{state.QuizTotal}*";
                        state.Mode = UserMode.Normal;
                        state.CurrentQuiz = null;
                        state.QuizCorrect = 0;
                        state.QuizTotal   = 0;
                        state.UsedQuizTopics.Clear();
                        await bot.SendMessage(chatId,
                            $"Вышел из режима квиза. {score}\n\nЗадавай вопросы по C# 💬",
                            parseMode: ParseMode.Markdown,
                            cancellationToken: ct);
                        break;

                    // ── Challenge кнопки ──────────────────────
                    case "challenge_hint":
                    {
                        // Извлекаем подсказку из сохранённого задания
                        var hint = state.CurrentChallenge.Contains("||HINT||")
                            ? state.CurrentChallenge.Split("||HINT||")[1].Split("||FIXED||")[0]
                            : "Подсказка недоступна.";
                        await bot.SendMessage(chatId, $"💡 *Подсказка:*\n{hint}",
                            parseMode: ParseMode.Markdown, cancellationToken: ct);
                        break;
                    }

                    case "challenge_answer":
                    {
                        var parts2 = state.CurrentChallenge.Split("||FIXED||");
                        var fixedCode = parts2.Length > 1 ? parts2[1].Split("||EXPLAIN||")[0] : "";
                        var explain   = parts2.Length > 1 && parts2[1].Contains("||EXPLAIN||")
                            ? parts2[1].Split("||EXPLAIN||")[1] : "";
                        await bot.SendMessage(chatId,
                            $"✅ *Правильный код:*\n```csharp\n{fixedCode}\n```\n\n📖 *Объяснение:*\n{explain}",
                            parseMode: ParseMode.Markdown,
                            replyMarkup: GetChallengeKeyboard(),
                            cancellationToken: ct);
                        break;
                    }

                    case "challenge_next":
                    {
                        await bot.SendChatAction(chatId, ChatAction.Typing, cancellationToken: ct);
                        var ch = await GenerateChallengeAsync(state.UsedChallengeTopics);
                        if (ch is null)
                        {
                            await bot.SendMessage(chatId, "❌ Не удалось сгенерировать задание.", cancellationToken: ct);
                            break;
                        }
                        // Запоминаем тему
                        if (!string.IsNullOrWhiteSpace(ch.Value.Topic))
                            state.UsedChallengeTopics.Add(ch.Value.Topic);
                        if (state.UsedChallengeTopics.Count >= 10)
                            state.UsedChallengeTopics.Clear();
                        state.CurrentChallenge = $"{ch.Value.BrokenCode}||HINT||{ch.Value.Hint}||FIXED||{ch.Value.FixedCode}||EXPLAIN||{ch.Value.Explanation}";
                        await bot.SendMessage(chatId, FormatChallenge(ch.Value.Description, ch.Value.BrokenCode),
                            parseMode: ParseMode.Markdown,
                            replyMarkup: GetChallengeKeyboard(),
                            cancellationToken: ct);
                        break;
                    }

                    case "challenge_exit":
                        state.Mode = UserMode.Normal;
                        state.CurrentChallenge = "";
                        await bot.SendMessage(chatId,
                            "Вышел из режима заданий. Задавай вопросы по C# 💬",
                            cancellationToken: ct);
                        break;

                    // ── Interview кнопки ──────────────────────
                    case "interview_exit":
                    {
                        state.Mode = UserMode.Normal;
                        var qCount = state.InterviewQuestionCount;
                        state.InterviewHistory.Clear();
                        state.InterviewQuestionCount = 0;
                        await bot.SendMessage(chatId,
                            $"Интервью завершено досрочно после {qCount} вопр.\n\nЗадавай вопросы по C# 💬",
                            cancellationToken: ct);
                        break;
                    }

                    case "quiz_answer_A":
                    case "quiz_answer_B":                    case "quiz_answer_C":
                    case "quiz_answer_D":
                    {
                        var chosen = data.Replace("quiz_answer_", ""); // "A", "B", "C" или "D"
                        var quiz   = state.CurrentQuiz;

                        if (quiz is null)
                        {
                            await bot.SendMessage(chatId, "Нет активного вопроса. Напиши /quiz чтобы начать.", cancellationToken: ct);
                            break;
                        }

                        var isCorrect = chosen.Equals(quiz.Correct, StringComparison.OrdinalIgnoreCase);
                        if (isCorrect) state.QuizCorrect++;

                        // Формируем ответ
                        var chosenText = chosen switch
                        {
                            "A" => quiz.A, "B" => quiz.B, "C" => quiz.C, _ => quiz.D
                        };
                        var correctText = quiz.Correct switch
                        {
                            "A" => quiz.A, "B" => quiz.B, "C" => quiz.C, _ => quiz.D
                        };

                        var result = isCorrect
                            ? $"✅ *Правильно!* Ты выбрал {chosen}) {chosenText}\n\n"
                            : $"❌ *Неверно.* Ты выбрал {chosen}) {chosenText}\n✅ Правильный ответ: *{quiz.Correct})* {correctText}\n\n";

                        result += $"📖 *Объяснение:*\n{quiz.Explanation}\n\n" +
                                  $"📊 Счёт: *{state.QuizCorrect}/{state.QuizTotal}*";

                        state.CurrentQuiz = null;

                        await bot.SendMessage(chatId, result,
                            parseMode: ParseMode.Markdown,
                            replyMarkup: GetQuizKeyboard(),
                            cancellationToken: ct);
                        break;
                    }
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

                // Главное меню — кнопки-плитки внизу экрана
                var mainMenu = new ReplyKeyboardMarkup(new[]
                {
                    new[] { new KeyboardButton("\U0001F3AF Квиз"),         new KeyboardButton("\U0001F527 Задание") },
                    new[] { new KeyboardButton("\U0001F3A4 Собеседование"), new KeyboardButton("\U0001F5FA Роадмап") },
                    new[] { new KeyboardButton("\u2753 Помощь") },
                })
                { ResizeKeyboard = true, OneTimeKeyboard = false };

                await bot.SendMessage(msgChatId,
                    $"\U0001F44B Привет, *{username}*!\n\n" +
                    "Я — *C# Mentor*, твой ИИ-помощник по языку C#.\n\n" +
                    "Выбери режим кнопкой или просто задай вопрос:",
                    parseMode: ParseMode.Markdown,
                    replyMarkup: mainMenu,
                    cancellationToken: ct);
                return;
            }

            // /help
            if (message.Text == "/help")
            {
                await bot.SendMessage(msgChatId,
                    "*Команды:*\n\n" +
                    "/start — начать заново\n" +
                    "/roadmap — план изучения C# 🗺\n" +
                    "/quiz — проверка знаний 🎯\n" +
                    "/challenge — задание: исправь код 🔧\n" +
                    "/interview — симуляция собеседования 🎤\n" +
                    "/clear — очистить историю диалога\n" +
                    "/help — эта справка\n\n" +
                    "*Можно присылать:*\n" +
                    "💬 Текстовый вопрос\n" +
                    "📷 Скриншот с кодом или ошибкой\n" +
                    "📄 Файл .cs с кодом\n\n" +
                    $"⏳ Лимит: {MaxRequestsPerHour} запросов в час",
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
                msgState.Mode         = UserMode.Quiz;
                msgState.QuizCorrect  = 0;
                msgState.QuizTotal    = 0;
                msgState.CurrentQuiz  = null;
                msgState.UsedQuizTopics.Clear();
                await bot.SendChatAction(msgChatId, ChatAction.Typing, cancellationToken: ct);
                await bot.SendMessage(msgChatId,
                    "🎯 *Режим квиза активирован!*\n\nОтвечай на вопросы нажимая кнопки A / B / C / D.",
                    parseMode: ParseMode.Markdown, cancellationToken: ct);

                var q = await GenerateQuizQuestionAsync(msgState.UsedQuizTopics);
                if (q is null)
                {
                    await bot.SendMessage(msgChatId, "❌ Не удалось сгенерировать вопрос. Попробуй ещё раз.", cancellationToken: ct);
                    return;
                }
                msgState.CurrentQuiz = q;
                msgState.QuizTotal++;
                if (!string.IsNullOrWhiteSpace(q.Topic))
                    msgState.UsedQuizTopics.Add(q.Topic);
                await bot.SendMessage(msgChatId, FormatQuizQuestion(q),
                    parseMode: ParseMode.Markdown,
                    replyMarkup: GetQuizAnswerKeyboard(),
                    cancellationToken: ct);
                return;
            }

            // /roadmap — план изучения C#
            if (message.Text == "/roadmap")
            {
                await bot.SendMessage(msgChatId, RoadmapText,
                    parseMode: ParseMode.Markdown, cancellationToken: ct);
                return;
            }

            // /challenge — задание на исправление кода
            if (message.Text == "/challenge")
            {
                msgState.Mode = UserMode.Challenge;
                msgState.UsedChallengeTopics.Clear();
                await bot.SendChatAction(msgChatId, ChatAction.Typing, cancellationToken: ct);
                await bot.SendMessage(msgChatId,
                    "🔧 *Режим заданий активирован!*\n\nЯ дам тебе сломанный C# код — найди и исправь ошибку.\nМожешь написать исправленный код или воспользоваться кнопками.",
                    parseMode: ParseMode.Markdown, cancellationToken: ct);

                var ch = await GenerateChallengeAsync(msgState.UsedChallengeTopics);
                if (ch is null)
                {
                    await bot.SendMessage(msgChatId, "❌ Не удалось сгенерировать задание. Попробуй ещё раз.", cancellationToken: ct);
                    return;
                }
                if (!string.IsNullOrWhiteSpace(ch.Value.Topic))
                    msgState.UsedChallengeTopics.Add(ch.Value.Topic);
                msgState.CurrentChallenge = $"{ch.Value.BrokenCode}||HINT||{ch.Value.Hint}||FIXED||{ch.Value.FixedCode}||EXPLAIN||{ch.Value.Explanation}";
                await bot.SendMessage(msgChatId, FormatChallenge(ch.Value.Description, ch.Value.BrokenCode),
                    parseMode: ParseMode.Markdown,
                    replyMarkup: GetChallengeKeyboard(),
                    cancellationToken: ct);
                return;
            }

            // /interview — симуляция технического собеседования
            if (message.Text == "/interview")
            {
                msgState.Mode = UserMode.Interview;
                msgState.InterviewHistory.Clear();
                msgState.InterviewQuestionCount = 0;
                await bot.SendChatAction(msgChatId, ChatAction.Typing, cancellationToken: ct);
                await bot.SendMessage(msgChatId,
                    "🎤 *Режим технического собеседования*\n\nЯ буду задавать вопросы как на реальном Junior C# интервью. Отвечай своими словами — оценю и задам следующий вопрос.\n\nПосле 5 вопросов подведу итог.\n\nГотов? Начинаем!",
                    parseMode: ParseMode.Markdown, cancellationToken: ct);

                // Первый вопрос от интервьюера
                var firstQ = await ConductInterviewAsync(msgState, "Начни интервью. Поприветствуй и задай первый вопрос.");
                msgState.InterviewHistory.Add(new ChatMessage("model", firstQ));
                await SendAnswerAsync(bot, msgChatId, firstQ, ct, GetInterviewKeyboard());
                return;
            }

            // ── Rate Limiting ─────────────────────────────────
            if (IsRateLimited(msgState))
            {
                await bot.SendMessage(msgChatId, GetRateLimitMessage(msgState), cancellationToken: ct);
                return;
            }
            TrackRequest(msgState);

            await bot.SendChatAction(msgChatId, ChatAction.Typing, cancellationToken: ct);

            // ── Фото
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

                // ── Обработка кнопок главного меню ───────────
                var menuAction = text switch
                {
                    "\U0001F3AF Квиз"          => "/quiz",
                    "\U0001F527 Задание"        => "/challenge",
                    "\U0001F3A4 Собеседование"  => "/interview",
                    "\U0001F5FA Роадмап"        => "/roadmap",
                    "\u2753 Помощь"             => "/help",
                    _                           => null
                };
                if (menuAction != null)
                {
                    // Эмулируем команду — переназначаем text и продолжаем обработку
                    // через повторный вызов соответствующего блока
                    await bot.SendMessage(msgChatId, $"Запускаю {text}...",
                        cancellationToken: ct);
                    // Отправляем боту команду от имени пользователя
                    _ = Task.Run(async () =>
                    {
                        // Небольшая задержка чтобы сообщение дошло
                        await Task.Delay(100);
                        // Вызываем нужную логику напрямую через команды
                        switch (menuAction)
                        {
                            case "/quiz":
                                msgState.Mode = UserMode.Quiz;
                                msgState.QuizCorrect = 0; msgState.QuizTotal = 0;
                                msgState.CurrentQuiz = null; msgState.UsedQuizTopics.Clear();
                                await bot.SendChatAction(msgChatId, ChatAction.Typing, cancellationToken: ct);
                                await bot.SendMessage(msgChatId,
                                    "🎯 *Режим квиза!*\n\nОтвечай нажимая кнопки A / B / C / D.",
                                    parseMode: ParseMode.Markdown, cancellationToken: ct);
                                var q = await GenerateQuizQuestionAsync(msgState.UsedQuizTopics);
                                if (q != null)
                                {
                                    msgState.CurrentQuiz = q; msgState.QuizTotal++;
                                    if (!string.IsNullOrWhiteSpace(q.Topic)) msgState.UsedQuizTopics.Add(q.Topic);
                                    await bot.SendMessage(msgChatId, FormatQuizQuestion(q),
                                        parseMode: ParseMode.Markdown, replyMarkup: GetQuizAnswerKeyboard(), cancellationToken: ct);
                                }
                                break;
                            case "/challenge":
                                msgState.Mode = UserMode.Challenge;
                                msgState.UsedChallengeTopics.Clear();
                                await bot.SendChatAction(msgChatId, ChatAction.Typing, cancellationToken: ct);
                                await bot.SendMessage(msgChatId,
                                    "🔧 *Режим заданий!*\n\nНайди и исправь ошибку в коде.",
                                    parseMode: ParseMode.Markdown, cancellationToken: ct);
                                var ch = await GenerateChallengeAsync(msgState.UsedChallengeTopics);
                                if (ch != null)
                                {
                                    if (!string.IsNullOrWhiteSpace(ch.Value.Topic))
                                        msgState.UsedChallengeTopics.Add(ch.Value.Topic);
                                    msgState.CurrentChallenge = $"{ch.Value.BrokenCode}||HINT||{ch.Value.Hint}||FIXED||{ch.Value.FixedCode}||EXPLAIN||{ch.Value.Explanation}";
                                    await bot.SendMessage(msgChatId, FormatChallenge(ch.Value.Description, ch.Value.BrokenCode),
                                        parseMode: ParseMode.Markdown, replyMarkup: GetChallengeKeyboard(), cancellationToken: ct);
                                }
                                break;
                            case "/interview":
                                msgState.Mode = UserMode.Interview;
                                msgState.InterviewHistory.Clear(); msgState.InterviewQuestionCount = 0;
                                await bot.SendChatAction(msgChatId, ChatAction.Typing, cancellationToken: ct);
                                await bot.SendMessage(msgChatId,
                                    "🎤 *Симуляция собеседования*\n\nОтвечай на вопросы — после 5 вопросов подведу итог.",
                                    parseMode: ParseMode.Markdown, cancellationToken: ct);
                                var firstQ = await ConductInterviewAsync(msgState, "Начни интервью. Поприветствуй и задай первый вопрос.");
                                msgState.InterviewHistory.Add(new ChatMessage("model", firstQ));
                                await SendAnswerAsync(bot, msgChatId, firstQ, ct, GetInterviewKeyboard());
                                break;
                            case "/roadmap":
                                await bot.SendMessage(msgChatId, RoadmapText,
                                    parseMode: ParseMode.Markdown, cancellationToken: ct);
                                break;
                            case "/help":
                                await bot.SendMessage(msgChatId,
                                    "*Команды:*\n\n" +
                                    "/quiz — проверка знаний 🎯\n" +
                                    "/challenge — исправь код 🔧\n" +
                                    "/interview — собеседование 🎤\n" +
                                    "/roadmap — план изучения 🗺\n" +
                                    "/clear — очистить историю\n" +
                                    "/start — главное меню\n\n" +
                                    "Или просто напиши вопрос по C# 💬",
                                    parseMode: ParseMode.Markdown, cancellationToken: ct);
                                break;
                        }
                    }, ct);
                    return;
                }

                // В режиме квиза — только кнопки
                if (msgState.Mode == UserMode.Quiz)
                {
                    await bot.SendMessage(msgChatId,
                        "Выбери ответ нажав кнопку *A*, *B*, *C* или *D* 👆",
                        parseMode: ParseMode.Markdown, cancellationToken: ct);
                    return;
                }

                // В режиме challenge — проверяем попытку студента
                if (msgState.Mode == UserMode.Challenge)
                {
                    var brokenCode = msgState.CurrentChallenge.Contains("||HINT||")
                        ? msgState.CurrentChallenge.Split("||HINT||")[0] : "";
                    var fixedCode  = msgState.CurrentChallenge.Contains("||FIXED||")
                        ? msgState.CurrentChallenge.Split("||FIXED||")[1].Split("||EXPLAIN||")[0] : "";
                    var explain    = msgState.CurrentChallenge.Contains("||EXPLAIN||")
                        ? msgState.CurrentChallenge.Split("||EXPLAIN||")[1] : "";

                    var checkPrompt = $"Студент пытается исправить сломанный C# код.\n\n" +
                                      $"Сломанный код:\n```csharp\n{brokenCode}\n```\n\n" +
                                      $"Правильный код:\n```csharp\n{fixedCode}\n```\n\n" +
                                      $"Ответ студента:\n```csharp\n{text}\n```\n\n" +
                                      $"Оцени ответ студента: правильно ли он исправил ошибку? " +
                                      $"Если да — похвали. Если нет или частично — объясни что не так. Будь конкретным.";

                    var checkResult = await AskGeminiWithHistoryAsync(msgState, checkPrompt);
                    await SendAnswerAsync(bot, msgChatId, checkResult, ct, GetChallengeKeyboard());
                    return;
                }

                // В режиме interview — передаём ответ интервьюеру
                if (msgState.Mode == UserMode.Interview)
                {
                    msgState.InterviewHistory.Add(new ChatMessage("user", text));
                    msgState.InterviewQuestionCount++;

                    var interviewReply = await ConductInterviewAsync(msgState, text);
                    msgState.InterviewHistory.Add(new ChatMessage("model", interviewReply));

                    // Держим историю интервью не более 20 сообщений
                    if (msgState.InterviewHistory.Count > 20)
                        msgState.InterviewHistory.RemoveAt(0);

                    // После 5 ответов студента — завершаем
                    var isFinished = msgState.InterviewQuestionCount >= 5;
                    if (isFinished) msgState.Mode = UserMode.Normal;

                    await SendAnswerAsync(bot, msgChatId, interviewReply, ct,
                        isFinished ? null : GetInterviewKeyboard());
                    return;
                }

                var answer = await AskGeminiWithHistoryAsync(msgState, text);

                msgState.LastUserQuestion = text;
                msgState.LastBotAnswer    = answer;
                AddToHistory(msgState, "user",  text);
                AddToHistory(msgState, "model", answer);

                await SendAnswerAsync(bot, msgChatId, answer, ct, GetAnswerKeyboard());
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

        // ── Регистрируем команды — появятся в меню "/" у пользователя
        await botClient.SetMyCommands(new[]
        {
            new BotCommand { Command = "start",     Description = "🏠 Главное меню" },
            new BotCommand { Command = "quiz",      Description = "🎯 Проверить знания" },
            new BotCommand { Command = "challenge", Description = "🔧 Задание: исправь код" },
            new BotCommand { Command = "interview", Description = "🎤 Симуляция собеседования" },
            new BotCommand { Command = "roadmap",   Description = "🗺 План изучения C#" },
            new BotCommand { Command = "clear",     Description = "🗑 Очистить историю диалога" },
            new BotCommand { Command = "help",      Description = "❓ Справка" },
        });
        logger.LogInformation("✅ Команды зарегистрированы");
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

enum UserMode { Normal, Quiz, Challenge, Interview }

record ChatMessage(string Role, string Text);

// Вопрос квиза — десериализуется из JSON-ответа Gemini
class QuizQuestion
{
    public string Question    { get; set; } = "";
    public string Code        { get; set; } = "";
    public string A           { get; set; } = "";
    public string B           { get; set; } = "";
    public string C           { get; set; } = "";
    public string D           { get; set; } = "";
    public string Correct     { get; set; } = ""; // "A", "B", "C" или "D"
    public string Explanation { get; set; } = "";
    public string Topic       { get; set; } = ""; // тема вопроса для отслеживания повторений
}

class UserState
{
    public UserMode Mode { get; set; } = UserMode.Normal;
    public List<ChatMessage> History { get; } = new();
    public string LastBotAnswer { get; set; } = "";
    public string LastUserQuestion { get; set; } = "";
    public DateTime LastActivity { get; set; } = DateTime.UtcNow;
    // Для rate limiting
    public List<DateTime> RequestTimestamps { get; } = new();
    // Текущий вопрос квиза
    public QuizQuestion? CurrentQuiz { get; set; } = null;
    // Счёт в квизе
    public int QuizCorrect { get; set; } = 0;
    public int QuizTotal   { get; set; } = 0;
    // Темы уже заданных вопросов в этой сессии (для исключения повторений)
    public List<string> UsedQuizTopics { get; } = new();
    // Challenge — текущее задание
    public string CurrentChallenge { get; set; } = "";
    // Темы уже использованных заданий (для исключения повторений)
    public List<string> UsedChallengeTopics { get; } = new();
    // Interview — история диалога собеседования
    public List<ChatMessage> InterviewHistory { get; } = new();
    public int InterviewQuestionCount { get; set; } = 0;
}
