using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
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
// Инициализация SQLite БД
// ─────────────────────────────────────────────────────────────
var dbPath = Environment.GetEnvironmentVariable("DB_PATH") ?? "/data/mentor.db";
// Локально храним рядом с приложением
if (!Directory.Exists(Path.GetDirectoryName(dbPath)!))
    dbPath = "mentor.db";
var userDb = new UserDb(dbPath);
logger.LogInformation("SQLite БД: {Path}", dbPath);

// ─────────────────────────────────────────────────────────────
// Системные промпты
// ─────────────────────────────────────────────────────────────
const string SystemPrompt = """
Ты — опытный C# разработчик и наставник, который помогает студентам учиться программированию.
Ты общаешься как живой человек: дружелюбно, с юмором когда уместно, без занудства.

Главные принципы:
1. Отвечай ТОЛЬКО на вопросы про C#, .NET и программирование. На остальное — мягко откажи.
2. Говори как человек, а не как учебник. Можно сказать «смотри, тут прикол в том что...» или «классический новичковый баг, я сам на это налетал».
3. Структуру ответа выбирай сам под вопрос — не всегда нужны заголовки и списки. Простой вопрос = простой ответ.
4. Код давай рабочий, на современном C# (12+), с комментариями только там где реально нужно пояснение.
5. Аналогии используй когда они реально помогают понять, не ради галочки.
6. Если студент сделал ошибку — объясни почему, без осуждения. Все через это проходят.
7. Можешь задать уточняющий вопрос если задача неясна.
8. Иногда можешь добавить интересный факт или лайфхак по теме — не всегда, только когда это реально полезно.
9. Форматируй код в блоках ```csharp.
10. Длина ответа — по ситуации. Не раздувай там где можно сказать коротко.
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

const string DuelQuizSystemPrompt = """
Ты — экзаменатор по C# для дуэлей между игроками. Генерируй разнообразные вопросы по ВСЕМ темам C#.

Темы (выбирай случайно, максимальное разнообразие):
- Типы данных и переменные
- Условия и циклы
- Методы и параметры
- Массивы и коллекции (List, Dictionary, HashSet)
- Строки и их методы
- ООП: классы, наследование, полиморфизм, интерфейсы
- Исключения (try/catch/finally)
- Делегаты и события
- LINQ (Where, Select, FirstOrDefault, OrderBy, Count)
- async/await и Task
- Generics
- Null-операторы (??, ?., !)
- Паттерны matching (switch expressions, is, as)
- Работа с файлами
- Атрибуты и рефлексия

Правила:
1. Вопрос про КОНКРЕТНОЕ поведение кода — что выведет, что вернёт, что произойдёт.
2. РОВНО 4 варианта: A, B, C, D. Только один правильный.
3. Уровень сложности — MIX: 40% простые, 40% средние, 20% сложные.
4. Каждый вопрос УНИКАЛЕН — не повторяй уже использованные вопросы.
5. Код в вопросе должен быть нетривиальным — пусть игрок реально подумает.
6. Отвечай СТРОГО в формате JSON:

{
  "question": "Текст вопроса",
  "code": "// C# код если нужен, иначе пустая строка",
  "a": "Вариант A",
  "b": "Вариант B",
  "c": "Вариант C",
  "d": "Вариант D",
  "correct": "A",
  "explanation": "Краткое объяснение почему этот ответ верный",
  "topic": "Тема вопроса"
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
Ты — опытный Senior C# разработчик, проводишь техническое собеседование на Junior позицию.
Ведёшь себя как живой человек: профессионально, но без излишней сухости.

Правила:
1. Задавай вопросы по одному, жди ответа.
2. Темы: основы C#, ООП, коллекции, исключения, LINQ, async/await.
3. После ответа — коротко оцени (верно/частично/неверно) и объясни. Можешь сказать «да, именно» или «не совсем, тут дело в том что...»
4. Не подсказывай заранее.
5. После 5 вопросов подведи итог: сильные стороны, что подтянуть, общее впечатление.
6. Первое сообщение: поприветствуй по-человечески и задай первый вопрос.
""";

// ─────────────────────────────────────────────────────────────
// Структура роадмапа — этапы и темы
// ─────────────────────────────────────────────────────────────
var LearnStages = new Dictionary<string, string[]>
{
    ["📦 Основы"] = new[]
    {
        "Типы данных (int, string, bool, double)",
        "Переменные и константы",
        "Арифметические операторы",
        "Логические операторы",
        "Условия if / else / switch",
        "Цикл for",
        "Цикл while и do-while",
        "Цикл foreach",
        "Массивы",
        "Методы и параметры",
        "Null и nullable типы"
    },
    ["🏗 ООП"] = new[]
    {
        "Классы и объекты",
        "Конструкторы",
        "Свойства (get / set)",
        "Наследование",
        "Полиморфизм",
        "Интерфейсы",
        "Абстрактные классы",
        "Инкапсуляция (public / private)",
        "Generics (List<T>)"
    },
    ["⚡ Продвинутый C#"] = new[]
    {
        "Исключения (try / catch / finally)",
        "Делегаты",
        "События (events)",
        "Лямбда-выражения",
        "LINQ: Where и Select",
        "LINQ: OrderBy и GroupBy",
        "async / await",
        "Task и многопоточность",
        "Работа с файлами"
    },
    ["🌐 .NET экосистема"] = new[]
    {
        "ASP.NET Core — основы",
        "REST API и HTTP",
        "Entity Framework Core",
        "Dependency Injection",
        "Юнит-тесты (xUnit)",
        "NuGet пакеты",
        "Docker основы"
    }
};

const string LearnSystemPrompt = """
Ты — опытный C# разработчик, объясняешь тему студенту как живой человек.

Структура урока — гибкая, адаптируй под тему:
- Объясни суть своими словами, без занудства
- Покажи рабочий пример кода (C# 12+), прокомментируй ключевые моменты
- Покажи типичную ошибку новичка по этой теме и как её исправить
- Дай один практический совет из реального опыта

Говори живо, можно с юмором. Не копируй структуру слово в слово каждый раз.
Форматируй код в блоках ```csharp.
Урок должен читаться за 3-5 минут.
""";

// ─────────────────────────────────────────────────────────────
// Хранение состояния пользователей (в памяти)
// ─────────────────────────────────────────────────────────────

// Потокобезопасный словарь: userId → состояние
var userStates = new ConcurrentDictionary<long, UserState>();

// Дуэли: code → комната, userId → code
var duelRooms  = new ConcurrentDictionary<string, DuelRoom>();
var userDuels  = new ConcurrentDictionary<long, string>(); // userId → duelCode

string GenerateDuelCode()
{
    const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    var rnd = new Random();
    return new string(Enumerable.Range(0, 4).Select(_ => chars[rnd.Next(chars.Length)]).ToArray());
}

// Кнопки для вопроса в дуэли
InlineKeyboardMarkup GetDuelAnswerKeyboard(string code, int qIndex) => new(new[]
{
    new[]
    {
        InlineKeyboardButton.WithCallbackData("A", $"duel_ans_{code}_{qIndex}_A"),
        InlineKeyboardButton.WithCallbackData("B", $"duel_ans_{code}_{qIndex}_B"),
        InlineKeyboardButton.WithCallbackData("C", $"duel_ans_{code}_{qIndex}_C"),
        InlineKeyboardButton.WithCallbackData("D", $"duel_ans_{code}_{qIndex}_D"),
    }
});

// Отправить вопрос дуэли одному игроку
async Task SendDuelQuestion(ITelegramBotClient bot, long chatId, DuelRoom room, int qIdx, CancellationToken ct)
{
    if (qIdx >= room.Questions.Count)
    {
        logger.LogError("SendDuelQuestion: qIdx={Q} >= Questions.Count={C}", qIdx, room.Questions.Count);
        await bot.SendMessage(chatId, "❌ Ошибка при загрузке вопроса.", cancellationToken: ct);
        return;
    }
    var q   = room.Questions[qIdx];
    var sb  = new StringBuilder();
    sb.AppendLine($"\u2694\uFE0F *Дуэль [{room.Code}]* — вопрос {qIdx + 1}/{DuelRoom.TotalQuestions}");
    sb.AppendLine();
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

    try
    {
        await bot.SendMessage(chatId, sb.ToString(),
            parseMode: ParseMode.Markdown,
            replyMarkup: GetDuelAnswerKeyboard(room.Code, qIdx),
            cancellationToken: ct);
    }
    catch
    {
        await bot.SendMessage(chatId, sb.ToString(),
            replyMarkup: GetDuelAnswerKeyboard(room.Code, qIdx),
            cancellationToken: ct);
    }
}

UserState GetState(long userId, string? username = null)
{
    var isNew = !userStates.ContainsKey(userId);
    var state = userStates.GetOrAdd(userId, _ => new UserState());
    state.LastActivity = DateTime.UtcNow;
    if (!string.IsNullOrWhiteSpace(username))
        state.Username = username;
    // Загружаем из БД только один раз при первом обращении
    if (isNew)
        userDb.LoadUser(userId, state);
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
// Система рейтинга
// ─────────────────────────────────────────────────────────────

// Очки за действия
const int PointsQuizCorrect   = 10;  // правильный ответ в квизе
const int PointsChallengeSolved = 15; // решённое задание
const int PointsLessonRead    = 5;   // прочитанный урок
const int PointsStreakBonus   = 20;  // бонус за ежедневный стрик

void AddPoints(UserState state, int points, string reason = "")
{
    state.TotalPoints += points;
    if (!string.IsNullOrEmpty(reason))
        logger.LogInformation("Points +{P} ({R}) → total {T} for {U}",
            points, reason, state.TotalPoints, state.Username);
    // Сохраняем в БД — найдём userId через userStates
    var userId = userStates.FirstOrDefault(kv => kv.Value == state).Key;
    if (userId != 0) userDb.SaveUser(userId, state);
}

void UpdateStreak(UserState state)
{
    var today = DateTime.UtcNow.Date;
    if (state.LastStreakDate == today) return; // уже засчитан сегодня

    if (state.LastStreakDate == today.AddDays(-1))
    {
        state.CurrentStreak++;
        // Бонус за каждые 7 дней подряд
        if (state.CurrentStreak % 7 == 0)
            AddPoints(state, PointsStreakBonus, "стрик 7 дней");
    }
    else
    {
        state.CurrentStreak = 1; // сброс стрика
    }
    state.LastStreakDate = today;
}

// Уровень по очкам
static (string Title, string Emoji) GetLevel(int points) => points switch
{
    < 50   => ("Новичок",    "\U0001F331"),
    < 150  => ("Студент",    "\U0001F4D6"),
    < 300  => ("Практикант", "\U0001F527"),
    < 500  => ("Junior",     "\U0001F4BB"),
    < 1000 => ("Middle",     "\u2B50"),
    _      => ("Senior",     "\U0001F3C6")
};

// Топ-10 пользователей
string GetLeaderboard()
{
    // Берём из БД — там актуальные данные
    var top = userDb.GetTopUsers(10);

    if (top.Count == 0)
        return "\U0001F3C6 *Таблица лидеров пуста*\n\nБудь первым — пройди квиз или урок!";

    var sb = new StringBuilder();
    sb.AppendLine("\U0001F3C6 *Таблица лидеров*\n");

    var medals = new[] { "\U0001F947", "\U0001F948", "\U0001F949" };
    for (int i = 0; i < top.Count; i++)
    {
        var (name, pts) = top[i];
        var prefix = i < 3 ? medals[i] : $"{i + 1}\\.";
        var (title, emoji) = GetLevel(pts);
        sb.AppendLine($"{prefix} *{EscapeMd(name)}* {emoji}");
        sb.AppendLine($"    {pts} очков \\| {title}");
    }
    return sb.ToString();
}

// Профиль пользователя
string GetProfile(long userId, UserState state)
{
    var (title, emoji) = GetLevel(state.TotalPoints);
    var streakText = state.CurrentStreak > 1 ? $"\U0001F525 Стрик: {state.CurrentStreak} дн\\." : "";
    return $"{emoji} *{EscapeMd(state.Username)}*\n\n" +
           $"Уровень: *{title}*\n" +
           $"\u2B50 Очков: *{state.TotalPoints}*\n\n" +
           $"\U0001F3AF Правильных в квизе: {state.QuizWins}\n" +
           $"\U0001F527 Заданий решено: {state.ChallengesSolved}\n" +
           $"\U0001F4DA Уроков прочитано: {state.LessonsRead}\n" +
           (streakText.Length > 0 ? $"\n{streakText}" : "");
}

// Экранирование для MarkdownV2
string EscapeMd(string text) => text
    .Replace(".", "\\.").Replace("-", "\\-").Replace("(", "\\(")
    .Replace(")", "\\)").Replace("!", "\\!").Replace("+", "\\+")
    .Replace("=", "\\=").Replace("|", "\\|").Replace("{", "\\{")
    .Replace("}", "\\}").Replace("#", "\\#");

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
        InlineKeyboardButton.WithCallbackData("\U0001F501 Другой пример", "another_example"),
        InlineKeyboardButton.WithCallbackData("\U0001F4D6 Проще", "explain_simpler"),
    },
    new[]
    {
        InlineKeyboardButton.WithCallbackData("\U0001F44D Понятно", "feedback_good"),
        InlineKeyboardButton.WithCallbackData("\U0001F3AF Квиз", "start_quiz"),
    }
});

InlineKeyboardMarkup GetQuizKeyboard() => new(new[]
{
    new[]
    {
        InlineKeyboardButton.WithCallbackData("\u27A1\uFE0F Следующий вопрос", "quiz_next"),
        InlineKeyboardButton.WithCallbackData("\U0001F6AA Выйти из квиза", "quiz_exit"),
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
async Task<QuizQuestion?> GenerateQuizQuestionAsync(List<string> usedTopics, List<string>? usedQuestions = null)
{
    if (string.IsNullOrWhiteSpace(apiKey)) return null;

    var usedHint = usedTopics.Count > 0
        ? $"Уже использованные темы (НЕ повторяй их): {string.Join(", ", usedTopics)}."
        : "Это первый вопрос в сессии.";

    // Добавляем подсказку об уже виденных вопросах
    var questionsHint = usedQuestions?.Count > 0
        ? $"\n\nЭти вопросы уже были заданы (НЕ повторяй их):\n- {string.Join("\n- ", usedQuestions.TakeLast(15))}"
        : "";

    var body = new
    {
        system_instruction = new { parts = new[] { new { text = QuizSystemPrompt } } },
        contents = new[]
        {
            new { role = "user", parts = new[] { new { text = $"Сгенерируй новый вопрос по C# для начинающего. {usedHint}{questionsHint}" } } }
        },
        generationConfig = new { temperature = 1.2, maxOutputTokens = 1024, responseMimeType = "application/json" }
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

// Генерация вопроса для ДУЭЛИ — широкий охват тем, повышенная сложность
async Task<QuizQuestion?> GenerateDuelQuestionAsync(List<string> usedTopics, List<string> usedQuestions)
{
    if (string.IsNullOrWhiteSpace(apiKey)) return null;

    var usedHint = usedTopics.Count > 0
        ? $"Уже использованные темы (избегай их): {string.Join(", ", usedTopics)}."
        : "Это первый вопрос дуэли.";

    var questionsHint = usedQuestions.Count > 0
        ? $"\nЭти вопросы уже были (НЕ повторяй):\n- {string.Join("\n- ", usedQuestions.TakeLast(20))}"
        : "";

    var body = new
    {
        system_instruction = new { parts = new[] { new { text = DuelQuizSystemPrompt } } },
        contents = new[]
        {
            new { role = "user", parts = new[] { new { text = $"Сгенерируй уникальный вопрос для дуэли. {usedHint}{questionsHint}" } } }
        },
        generationConfig = new { temperature = 1.0, maxOutputTokens = 1024, responseMimeType = "application/json" }
    };

    var raw = await CallGeminiAsync(body);
    try
    {
        var json = raw.Trim();
        if (json.StartsWith("```")) json = json.Split('\n', 2)[1];
        if (json.EndsWith("```")) json = json[..json.LastIndexOf("```")];
        return JsonSerializer.Deserialize<QuizQuestion>(json.Trim(),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }
    catch (Exception ex)
    {
        logger.LogError("Ошибка парсинга duel quiz: {Msg}", ex.Message);
        return null;
    }
}

// Вспомогательная функция: сгенерировать следующий вопрос и сохранить в state
async Task<QuizQuestion?> GetNextQuizQuestion(UserState st)
{
    var q2 = await GenerateQuizQuestionAsync(st.UsedQuizTopics, st.UsedQuizQuestions);
    if (q2 is null) return null;

    // Запоминаем тему
    if (!string.IsNullOrWhiteSpace(q2.Topic))
        st.UsedQuizTopics.Add(q2.Topic);
    if (st.UsedQuizTopics.Count >= 10)
        st.UsedQuizTopics.Clear();

    // Запоминаем вопрос в памяти и в БД
    var qKey = q2.Question.Length > 80 ? q2.Question[..80] : q2.Question;
    if (!st.UsedQuizQuestions.Contains(qKey))
        st.UsedQuizQuestions.Add(qKey);
    if (st.UsedQuizQuestions.Count > 100)
        st.UsedQuizQuestions.RemoveAt(0);

    // Сохраняем в БД
    var userId = userStates.FirstOrDefault(kv => kv.Value == st).Key;
    if (userId != 0) userDb.SaveQuestion(userId, q2.Question);

    st.QuizTotal++;
    return q2;
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
        InlineKeyboardButton.WithCallbackData("\U0001F4A1 Подсказка", "challenge_hint"),
        InlineKeyboardButton.WithCallbackData("\u2705 Показать ответ", "challenge_answer"),
    },
    new[]
    {
        InlineKeyboardButton.WithCallbackData("\u27A1\uFE0F Новое задание", "challenge_next"),
        InlineKeyboardButton.WithCallbackData("\U0001F6AA Выйти", "challenge_exit"),
    }
});

// Кнопка выхода из интервью
InlineKeyboardMarkup GetInterviewKeyboard() => new(new[]
{
    new[]
    {
        InlineKeyboardButton.WithCallbackData("\U0001F6AA Завершить интервью", "interview_exit"),
    }
});

// Кнопки выбора этапа роадмапа
InlineKeyboardMarkup GetLearnStagesKeyboard() => new(
    LearnStages.Keys.Select(stage =>
        new[] { InlineKeyboardButton.WithCallbackData(stage, $"learn_stage_{stage}") }
    ).Append(new[] { InlineKeyboardButton.WithCallbackData("\U0001F6AA Выйти", "learn_exit") })
    .ToArray()
);

// Кнопки выбора темы внутри этапа
InlineKeyboardMarkup GetLearnTopicsKeyboard(string stage)
{
    var topics = LearnStages[stage];
    var rows = topics.Select(t =>
        new[] { InlineKeyboardButton.WithCallbackData(t, $"learn_topic_{t}") }
    ).Append(new[]
    {
        InlineKeyboardButton.WithCallbackData("\U0001F519 Назад к этапам", "learn_back"),
        InlineKeyboardButton.WithCallbackData("\U0001F6AA Выйти", "learn_exit"),
    }).ToArray();
    return new InlineKeyboardMarkup(rows);
}

// Кнопки после урока
InlineKeyboardMarkup GetAfterLessonKeyboard(string stage) => new(new[]
{
    new[]
    {
        InlineKeyboardButton.WithCallbackData("\U0001F4DA Другие темы", $"learn_stage_{stage}"),
        InlineKeyboardButton.WithCallbackData("\U0001F519 К этапам", "learn_back"),
    },
    new[]
    {
        InlineKeyboardButton.WithCallbackData("\U0001F4DD Задание по теме", "challenge_by_topic"),
        InlineKeyboardButton.WithCallbackData("\U0001F3AF Квиз", "start_quiz"),
    }
});

// ─────────────────────────────────────────────────────────────
// Gemini — генерация мини-урока
// ─────────────────────────────────────────────────────────────
async Task<string> GenerateLessonAsync(string topic)
{
    if (string.IsNullOrWhiteSpace(apiKey)) return "❌ API-ключ Gemini не настроен.";

    var body = new
    {
        system_instruction = new { parts = new[] { new { text = LearnSystemPrompt } } },
        contents = new[]
        {
            new { role = "user", parts = new[] { new { text = $"Дай мини-урок по теме: «{topic}» в C#" } } }
        },
        generationConfig = new { temperature = 0.7, maxOutputTokens = 4096 }
    };
    return await CallGeminiAsync(body);
}
async Task<(string BrokenCode, string Hint, string FixedCode, string Explanation, string Description, string Topic)?> GenerateChallengeAsync(List<string> usedTopics, string? forceTopic = null)
{
    if (string.IsNullOrWhiteSpace(apiKey)) return null;

    string taskPrompt;
    if (!string.IsNullOrWhiteSpace(forceTopic))
    {
        // Задание строго по теме урока
        taskPrompt = $"Сгенерируй задание на исправление C# кода СТРОГО по теме «{forceTopic}». " +
                     $"Ошибка должна быть напрямую связана с этой темой.";
    }
    else
    {
        var usedHint = usedTopics.Count > 0
            ? $"Уже использованные темы (НЕ повторяй): {string.Join(", ", usedTopics)}."
            : "Это первое задание в сессии.";
        taskPrompt = $"Сгенерируй новое задание на исправление кода для начинающего C# разработчика. {usedHint}";
    }

    var body = new
    {
        system_instruction = new { parts = new[] { new { text = ChallengeSystemPrompt } } },
        contents = new[]
        {
            new { role = "user", parts = new[] { new { text = taskPrompt } } }
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

    // Защита от пустого ответа
    if (string.IsNullOrWhiteSpace(answer))
        answer = "⚠️ Получен пустой ответ. Попробуй ещё раз.";

    async Task SendChunk(string chunk, bool withKeyboard)
    {
        try
        {
            await bot.SendMessage(chatId, chunk,
                parseMode: ParseMode.Markdown,
                replyMarkup: withKeyboard ? keyboard : null,
                cancellationToken: ct);
        }
        catch (Exception ex1)
        {
            logger.LogWarning("Markdown отправка не удалась: {Msg}", ex1.Message);
            try
            {
                await bot.SendMessage(chatId, chunk,
                    replyMarkup: withKeyboard ? keyboard : null,
                    cancellationToken: ct);
            }
            catch (Exception ex2)
            {
                logger.LogError("Отправка сообщения не удалась совсем: {Msg}", ex2.Message);
            }
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
        try
        {
            // ── Обработка нажатий на inline-кнопки ──────────
            if (update.CallbackQuery is { } query)
            {
                var userId   = query.From.Id;
                var chatId   = query.Message!.Chat.Id;
                var state    = GetState(userId, query.From.FirstName ?? query.From.Username);
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
                        AddToHistory(state, "user", "Покажи ещё один пример по той же теме, другой и более наглядный.");
                        var anotherEx = await AskGeminiWithHistoryAsync(state,
                            "Покажи ещё один пример по той же теме, другой и более наглядный.");
                        AddToHistory(state, "model", anotherEx);
                        await SendAnswerAsync(bot, chatId, anotherEx, ct, GetAnswerKeyboard());
                        break;

                    case "explain_simpler":
                        await bot.SendChatAction(chatId, ChatAction.Typing, cancellationToken: ct);
                        AddToHistory(state, "user", "Объясни то же самое проще, как будто я новичок.");
                        var simpler = await AskGeminiWithHistoryAsync(state,
                            "Объясни то же самое, но ещё проще — как будто я полный новичок. Используй бытовую аналогию.");
                        AddToHistory(state, "model", simpler);
                        await SendAnswerAsync(bot, chatId, simpler, ct, GetAnswerKeyboard());
                        break;

                    case "start_quiz":
                    case "quiz_next":
                        state.Mode = UserMode.Quiz;
                        await bot.SendChatAction(chatId, ChatAction.Typing, cancellationToken: ct);
                        var question = await GetNextQuizQuestion(state);
                        if (question is null)
                        {
                            await bot.SendMessage(chatId, "❌ Не удалось сгенерировать вопрос. Попробуй ещё раз.", cancellationToken: ct);
                            break;
                        }
                        state.CurrentQuiz = question;
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
                            "Вышел из режима заданий. Задавай вопросы по C# \U0001F4AC",
                            cancellationToken: ct);
                        break;

                    // Задание строго по теме последнего урока
                    case "challenge_by_topic":
                    {
                        state.Mode = UserMode.Challenge;
                        var topic2 = state.CurrentLearnTopic;
                        await bot.SendChatAction(chatId, ChatAction.Typing, cancellationToken: ct);

                        if (string.IsNullOrWhiteSpace(topic2))
                        {
                            // Тема не известна — обычное рандомное задание
                            await bot.SendMessage(chatId,
                                "\U0001F527 *Задание по коду*",
                                parseMode: ParseMode.Markdown, cancellationToken: ct);
                            var ch0 = await GenerateChallengeAsync(state.UsedChallengeTopics);
                            if (ch0 != null)
                            {
                                if (!string.IsNullOrWhiteSpace(ch0.Value.Topic))
                                    state.UsedChallengeTopics.Add(ch0.Value.Topic);
                                state.CurrentChallenge = $"{ch0.Value.BrokenCode}||HINT||{ch0.Value.Hint}||FIXED||{ch0.Value.FixedCode}||EXPLAIN||{ch0.Value.Explanation}";
                                await bot.SendMessage(chatId, FormatChallenge(ch0.Value.Description, ch0.Value.BrokenCode),
                                    parseMode: ParseMode.Markdown, replyMarkup: GetChallengeKeyboard(), cancellationToken: ct);
                            }
                            break;
                        }

                        await bot.SendMessage(chatId,
                            $"\U0001F527 *Задание по теме «{topic2}»*\n\nНайди и исправь ошибку:",
                            parseMode: ParseMode.Markdown, cancellationToken: ct);

                        // Передаём тему — задание будет строго по ней
                        var ch = await GenerateChallengeAsync(state.UsedChallengeTopics, topic2);
                        if (ch != null)
                        {
                            if (!string.IsNullOrWhiteSpace(ch.Value.Topic))
                                state.UsedChallengeTopics.Add(ch.Value.Topic);
                            state.CurrentChallenge = $"{ch.Value.BrokenCode}||HINT||{ch.Value.Hint}||FIXED||{ch.Value.FixedCode}||EXPLAIN||{ch.Value.Explanation}";
                            await bot.SendMessage(chatId, FormatChallenge(ch.Value.Description, ch.Value.BrokenCode),
                                parseMode: ParseMode.Markdown, replyMarkup: GetChallengeKeyboard(), cancellationToken: ct);
                        }
                        break;
                    }

                    // ── Interview кнопки ──────────────────────
                    case "interview_exit":
                    {
                        state.Mode = UserMode.Normal;
                        var qCount = state.InterviewQuestionCount;
                        state.InterviewHistory.Clear();
                        state.InterviewQuestionCount = 0;
                        await bot.SendMessage(chatId,
                            $"Интервью завершено досрочно после {qCount} вопр.\n\nЗадавай вопросы по C# \U0001F4AC",
                            cancellationToken: ct);
                        break;
                    }

                    // ── Learn кнопки ──────────────────────────
                    case "learn_back":
                        state.Mode = UserMode.Learn;
                        await bot.SendMessage(chatId,
                            "\U0001F4DA *Выбери этап для изучения:*",
                            parseMode: ParseMode.Markdown,
                            replyMarkup: GetLearnStagesKeyboard(),
                            cancellationToken: ct);
                        break;

                    case "learn_exit":
                        state.Mode = UserMode.Normal;
                        state.CurrentLearnStage = "";
                        await bot.SendMessage(chatId,
                            "Вышел из режима обучения. Задавай вопросы по C# \U0001F4AC",
                            cancellationToken: ct);
                        break;

                    default:
                        // duel_accept_{code} — принять вызов на дуэль
                        if (data.StartsWith("duel_accept_"))
                        {
                            var code = data.Replace("duel_accept_", "");

                            if (userDuels.TryGetValue(userId, out var existCode2)
                                && duelRooms.ContainsKey(existCode2))
                            {
                                await bot.AnswerCallbackQuery(query.Id,
                                    "Ты уже в дуэли!", showAlert: true, cancellationToken: ct);
                                break;
                            }
                            // Очищаем устаревшую запись
                            userDuels.TryRemove(userId, out var staleCode2);
                            if (!duelRooms.TryGetValue(code, out var acceptRoom))
                            {
                                await bot.AnswerCallbackQuery(query.Id,
                                    "Вызов уже принят или истёк.", showAlert: true, cancellationToken: ct);
                                break;
                            }
                            if (acceptRoom.Status != DuelStatus.WaitingForOpponent)
                            {
                                await bot.AnswerCallbackQuery(query.Id,
                                    "Вызов уже принят кем-то другим.", showAlert: true, cancellationToken: ct);
                                break;
                            }
                            if (acceptRoom.Player1Id == userId)
                            {
                                await bot.AnswerCallbackQuery(query.Id,
                                    "Нельзя принять свой собственный вызов.", showAlert: true, cancellationToken: ct);
                                break;
                            }

                            // Принимаем
                            acceptRoom.Player2Id   = userId;
                            acceptRoom.Player2Name = state.Username.Length > 0 ? state.Username : "Игрок 2";
                            acceptRoom.Status      = DuelStatus.InProgress;
                            userDuels[userId]      = code;

                            await bot.AnswerCallbackQuery(query.Id, cancellationToken: ct);

                            await bot.SendMessage(chatId,
                                $"\u2694\uFE0F *Дуэль принята!* Генерирую вопросы...",
                                parseMode: ParseMode.Markdown, cancellationToken: ct);
                            await bot.SendMessage(acceptRoom.Player1Id,
                                $"\u2694\uFE0F *{acceptRoom.Player2Name}* принял твой вызов! Дуэль начинается!",
                                parseMode: ParseMode.Markdown, cancellationToken: ct);

                            // Генерируем 5 вопросов
                            var tmpTopics2    = new List<string>();
                            var tmpQuestions2 = new List<string>();
                            for (int i = 0; i < DuelRoom.TotalQuestions; i++)
                            {
                                QuizQuestion? dq = null;
                                for (int attempt = 0; attempt < 3 && dq is null; attempt++)
                                    dq = await GenerateDuelQuestionAsync(tmpTopics2, tmpQuestions2);
                                if (dq != null)
                                {
                                    acceptRoom.Questions.Add(dq);
                                    if (!string.IsNullOrWhiteSpace(dq.Topic)) tmpTopics2.Add(dq.Topic);
                                    tmpQuestions2.Add(dq.Question.Length > 80 ? dq.Question[..80] : dq.Question);
                                }
                            }

                            if (acceptRoom.Questions.Count == 0)
                            {
                                await bot.SendMessage(chatId, "❌ Не удалось сгенерировать вопросы.", cancellationToken: ct);
                                duelRooms.TryRemove(code, out var dr1);
                                userDuels.TryRemove(acceptRoom.Player1Id, out var du1);
                                userDuels.TryRemove(userId, out var du2);
                                break;
                            }

                            acceptRoom.Player1Answered = 0;
                            acceptRoom.Player2Answered = 0;
                            await SendDuelQuestion(bot, acceptRoom.Player1Id, acceptRoom, 0, ct);
                            await SendDuelQuestion(bot, chatId, acceptRoom, 0, ct);
                            break;
                        }

                        // duel_ans_{code}_{qIndex}_{choice} — ответ в дуэли
                        if (data.StartsWith("duel_ans_"))
                        {
                            var parts = data.Split('_');
                            // duel_ans_CODE_QIDX_CHOICE → parts: [duel,ans,CODE,QIDX,CHOICE]
                            if (parts.Length == 5
                                && duelRooms.TryGetValue(parts[2], out var room)
                                && room.Status == DuelStatus.InProgress
                                && int.TryParse(parts[3], out var qIdx)
                                && qIdx < room.Questions.Count)
                            {
                                var choice   = parts[4];
                                var isP1     = userId == room.Player1Id;
                                var isP2     = userId == room.Player2Id;
                                if (!isP1 && !isP2) break;

                                var q        = room.Questions[qIdx];
                                var correct  = choice.Equals(q.Correct, StringComparison.OrdinalIgnoreCase);
                                var myName   = isP1 ? room.Player1Name : room.Player2Name;
                                var myScore  = isP1 ? room.Player1Score : room.Player2Score;
                                var myAns    = isP1 ? room.Player1Answered : room.Player2Answered;

                                // Не даём ответить дважды на один вопрос
                                if (myAns != qIdx) { await bot.AnswerCallbackQuery(query.Id, cancellationToken: ct); break; }

                                // Засчитываем ответ
                                if (correct)
                                {
                                    if (isP1) room.Player1Score++;
                                    else      room.Player2Score++;
                                }
                                if (isP1) room.Player1Answered++;
                                else      room.Player2Answered++;

                                // Показываем результат этого вопроса
                                var correctText = q.Correct switch { "A" => q.A, "B" => q.B, "C" => q.C, _ => q.D };
                                var emoji = correct ? "\u2705" : "\u274C";
                                await bot.SendMessage(chatId,
                                    $"{emoji} *{(correct ? "Правильно" : "Неверно")}!*\n" +
                                    $"Правильный ответ: *{q.Correct})* {correctText}\n\n" +
                                    $"{q.Explanation}",
                                    parseMode: ParseMode.Markdown, cancellationToken: ct);

                                // Следующий вопрос или ожидание
                                var nextIdx = qIdx + 1;
                                if (nextIdx < DuelRoom.TotalQuestions)
                                {
                                    await SendDuelQuestion(bot, chatId, room, nextIdx, ct);
                                    if (isP1) room.Player1Answered = nextIdx;
                                    else      room.Player2Answered = nextIdx;
                                }
                                else
                                {
                                    // Игрок ответил на все вопросы
                                    await bot.SendMessage(chatId,
                                        "\u23F3 Ты ответил на все вопросы! Ожидаю результатов соперника...",
                                        cancellationToken: ct);
                                }

                                // Проверяем завершение дуэли
                                var p1done = room.Player1Answered >= DuelRoom.TotalQuestions;
                                var p2done = room.Player2Answered >= DuelRoom.TotalQuestions;

                                if (p1done && p2done && room.Status == DuelStatus.InProgress)
                                {
                                    room.Status = DuelStatus.Finished;
                                    var s1 = room.Player1Score;
                                    var s2 = room.Player2Score;

                                    string result1, result2;
                                    int pts1, pts2;

                                    if (s1 > s2)
                                    {
                                        result1 = $"\U0001F3C6 *Победа!*"; pts1 = 30;
                                        result2 = $"\U0001F614 *Поражение*"; pts2 = 5;
                                    }
                                    else if (s2 > s1)
                                    {
                                        result1 = $"\U0001F614 *Поражение*"; pts1 = 5;
                                        result2 = $"\U0001F3C6 *Победа!*"; pts2 = 30;
                                    }
                                    else
                                    {
                                        result1 = result2 = "\U0001F91D *Ничья!*";
                                        pts1 = pts2 = 10;
                                    }

                                    var summary =
                                        $"\u2694\uFE0F *Дуэль завершена!*\n\n" +
                                        $"\U0001F535 {room.Player1Name}: *{s1}/{DuelRoom.TotalQuestions}*\n" +
                                        $"\U0001F534 {room.Player2Name}: *{s2}/{DuelRoom.TotalQuestions}*";

                                    // Отправляем итог обоим
                                    var st1 = GetState(room.Player1Id);
                                    var st2 = GetState(room.Player2Id);

                                    await bot.SendMessage(room.Player1Id, $"{summary}\n\n{result1} *+{pts1} очков*",
                                        parseMode: ParseMode.Markdown, cancellationToken: ct);
                                    await bot.SendMessage(room.Player2Id, $"{summary}\n\n{result2} *+{pts2} очков*",
                                        parseMode: ParseMode.Markdown, cancellationToken: ct);

                                    AddPoints(st1, pts1, "дуэль");
                                    AddPoints(st2, pts2, "дуэль");
                                    userDb.SaveUser(room.Player1Id, st1);
                                    userDb.SaveUser(room.Player2Id, st2);

                                    // Чистим комнату
                                    userDuels.TryRemove(room.Player1Id, out _);
                                    userDuels.TryRemove(room.Player2Id, out _);
                                    duelRooms.TryRemove(room.Code, out _);
                                }
                            }
                            break;
                        }
                        // learn_stage_XXX — выбор этапа
                        if (data.StartsWith("learn_stage_"))
                        {
                            var stage = data.Replace("learn_stage_", "");
                            if (LearnStages.ContainsKey(stage))
                            {
                                state.CurrentLearnStage = stage;
                                state.Mode = UserMode.Learn;
                                await bot.SendMessage(chatId,
                                    $"\U0001F4DA *{stage}*\n\nВыбери тему для изучения:",
                                    parseMode: ParseMode.Markdown,
                                    replyMarkup: GetLearnTopicsKeyboard(stage),
                                    cancellationToken: ct);
                            }
                        }
                        // learn_topic_XXX — выбор темы → генерируем урок
                        else if (data.StartsWith("learn_topic_"))
                        {
                            var topic = data.Replace("learn_topic_", "");
                            await bot.SendChatAction(chatId, ChatAction.Typing, cancellationToken: ct);
                            await bot.SendMessage(chatId,
                                $"\U0001F4D6 Готовлю урок по теме *«{topic}»*...",
                                parseMode: ParseMode.Markdown, cancellationToken: ct);

                            var lesson = await GenerateLessonAsync(topic);
                            var stage2 = state.CurrentLearnStage;

                            // Сохраняем тему и урок в историю диалога —
                            // после этого студент может задавать вопросы по теме
                            state.History.Clear(); // начинаем свежий контекст с этой темой
                            AddToHistory(state, "user",  $"Объясни тему: «{topic}» в C#");
                            AddToHistory(state, "model", lesson);

                            // Запоминаем тему для кнопки "Задание по теме"
                            state.CurrentLearnTopic = topic;
                            // Очки за урок
                            state.LessonsRead++;
                            AddPoints(state, PointsLessonRead, $"урок: {topic}");
                            UpdateStreak(state);

                            // Переключаем в Normal — вопросы после урока идут в обычный чат
                            state.Mode = UserMode.Normal;

                            await SendAnswerAsync(bot, chatId, lesson, ct,
                                string.IsNullOrEmpty(stage2) ? null : GetAfterLessonKeyboard(stage2));

                            // Подсказка что можно задавать вопросы
                            await bot.SendMessage(chatId,
                                "\U0001F4AC Можешь задать любой вопрос по этой теме — я помню контекст урока.",
                                cancellationToken: ct);
                        }
                        break;

                    case "quiz_answer_A":
                    case "quiz_answer_B":
                    case "quiz_answer_C":
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
                        if (isCorrect)
                        {
                            state.QuizCorrect++;
                            state.QuizWins++;
                            AddPoints(state, PointsQuizCorrect, "квиз");
                            UpdateStreak(state);
                        }

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
            var username   = message.From?.FirstName ?? "Студент";
            var msgState   = GetState(msgUserId, username);
            var caption    = message.Caption ?? "";

            // Записываем пользователя в БД для рассылок
            if (msgUserId != 0)
                userDb.TouchUser(msgUserId, username);

            // /start
            if (message.Text == "/start")
            {
                msgState.History.Clear();
                msgState.Mode = UserMode.Normal;

                // Главное меню — кнопки-плитки внизу экрана
                var mainMenu = new ReplyKeyboardMarkup(new[]
                {
                    new[] { new KeyboardButton("\U0001F3AF Квиз"),          new KeyboardButton("\U0001F527 Задание") },
                    new[] { new KeyboardButton("\U0001F3A4 Собеседование"),  new KeyboardButton("\U0001F4DA Учиться") },
                    new[] { new KeyboardButton("\U0001F3C6 Рейтинг"),        new KeyboardButton("\U0001F5FA Роадмап") },
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
                    "\U0001F5D1 История очищена. Начинаем с чистого листа!",
                    cancellationToken: ct);
                return;
            }

            // /profile — профиль пользователя
            if (message.Text == "/profile")
            {
                var profileText = GetProfile(msgUserId, msgState);
                await bot.SendMessage(msgChatId, profileText,
                    parseMode: ParseMode.MarkdownV2, cancellationToken: ct);
                return;
            }

            // /top — таблица лидеров
            if (message.Text == "/top")
            {
                await bot.SendMessage(msgChatId, GetLeaderboard(),
                    parseMode: ParseMode.MarkdownV2, cancellationToken: ct);
                return;
            }

            // /quiz — запуск квиза
            if (message.Text == "/quiz")
            {
                msgState.Mode         = UserMode.Quiz;
                msgState.QuizCorrect  = 0;
                msgState.QuizTotal    = 0;
                msgState.CurrentQuiz  = null;
                msgState.UsedQuizTopics.Clear(); // темы сбрасываем — для разнообразия тем
                // UsedQuizQuestions НЕ очищаем — помним все виденные вопросы навсегда
                await bot.SendChatAction(msgChatId, ChatAction.Typing, cancellationToken: ct);
                await bot.SendMessage(msgChatId,
                    "\U0001F3AF *Режим квиза активирован!*\n\nОтвечай нажимая кнопки A / B / C / D.",
                    parseMode: ParseMode.Markdown, cancellationToken: ct);

                var q = await GetNextQuizQuestion(msgState);
                if (q is null)
                {
                    await bot.SendMessage(msgChatId, "❌ Не удалось сгенерировать вопрос. Попробуй ещё раз.", cancellationToken: ct);
                    return;
                }
                msgState.CurrentQuiz = q;
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

            // /learn — интерактивное изучение тем из роадмапа
            if (message.Text == "/learn")
            {
                msgState.Mode = UserMode.Learn;
                msgState.CurrentLearnStage = "";
                await bot.SendMessage(msgChatId,
                    "\U0001F4DA *Режим обучения*\n\n" +
                    "Выбери этап — я дам тебе подробный урок по любой теме:",
                    parseMode: ParseMode.Markdown,
                    replyMarkup: GetLearnStagesKeyboard(),
                    cancellationToken: ct);
                return;
            }

            // /duel — создать или присоединиться к дуэли
            if (message.Text != null && (message.Text == "/duel" || message.Text.StartsWith("/duel ")))
            {
                // Если уже в дуэли — но проверяем что она реально существует
                if (userDuels.TryGetValue(msgUserId, out var existingCode)
                    && duelRooms.ContainsKey(existingCode))
                {
                    await bot.SendMessage(msgChatId,
                        "\u26A0\uFE0F Ты уже в дуэли! Дождись её завершения.",
                        cancellationToken: ct);
                    return;
                }
                // Очищаем устаревшую запись если комнаты уже нет
                userDuels.TryRemove(msgUserId, out var staleCode);

                var parts = message.Text.Split(' ');

                if (parts.Length == 1)
                {
                    // Создаём комнату
                    var code = GenerateDuelCode();
                    while (duelRooms.ContainsKey(code)) code = GenerateDuelCode();

                    var room = new DuelRoom
                    {
                        Code        = code,
                        Player1Id   = msgUserId,
                        Player1Name = username
                    };
                    duelRooms[code]      = room;
                    userDuels[msgUserId] = code;

                    // Сообщение создателю
                    await bot.SendMessage(msgChatId,
                        $"\u2694\uFE0F *Вызов брошен!*\n\n" +
                        $"Рассылаю вызов всем активным игрокам...\n" +
                        $"Или поделись кодом вручную: `{code}`\n\n" +
                        $"Жду соперника (5 минут)...",
                        parseMode: ParseMode.Markdown, cancellationToken: ct);

                    // Рассылаем вызов всем активным пользователям из БД (кроме самого игрока)
                    var challengeKeyboard = new InlineKeyboardMarkup(new[]
                    {
                        new[]
                        {
                            InlineKeyboardButton.WithCallbackData(
                                $"\u2694\uFE0F Принять вызов от {username}",
                                $"duel_accept_{code}")
                        }
                    });

                    var notified   = 0;
                    var activeIds  = userDb.GetActiveUserIds(msgUserId);
                    logger.LogInformation("Duel broadcast: found {Count} active users", activeIds.Count);

                    foreach (var uid in activeIds)
                    {
                        // Пропускаем тех кто уже в дуэли
                        if (userDuels.ContainsKey(uid)) continue;
                        try
                        {
                            await bot.SendMessage(uid,
                                $"\u2694\uFE0F *Вызов на дуэль!*\n\n" +
                                $"*{username}* бросает тебе вызов по C#!\n" +
                                $"5 вопросов — кто знает лучше?\n\n" +
                                $"Нажми кнопку чтобы принять:",
                                parseMode: ParseMode.Markdown,
                                replyMarkup: challengeKeyboard,
                                cancellationToken: ct);
                            notified++;
                        }
                        catch { /* пользователь заблокировал бота */ }
                    }

                    if (notified == 0)
                    {
                        await bot.SendMessage(msgChatId,
                            "Активных игроков сейчас нет. Поделись кодом с другом!",
                            cancellationToken: ct);
                    }

                    // Автоотмена через 5 минут
                    _ = Task.Delay(TimeSpan.FromMinutes(5)).ContinueWith(t =>
                    {
                        if (duelRooms.TryGetValue(code, out var r) && r.Status == DuelStatus.WaitingForOpponent)
                        {
                            duelRooms.TryRemove(code, out var removed1);
                            userDuels.TryRemove(msgUserId, out var removed2);
                            bot.SendMessage(msgChatId,
                                $"\u23F0 Вызов истёк — никто не принял за 5 минут.",
                                parseMode: ParseMode.Markdown).Wait();
                        }
                    });
                }
                else
                {
                    // Присоединяемся к комнате
                    var code = parts[1].ToUpper().Trim();
                    if (!duelRooms.TryGetValue(code, out var room))
                    {
                        await bot.SendMessage(msgChatId,
                            "❌ Комната с таким кодом не найдена или уже завершена.",
                            cancellationToken: ct);
                        return;
                    }
                    if (room.Status != DuelStatus.WaitingForOpponent)
                    {
                        await bot.SendMessage(msgChatId, "❌ Дуэль уже началась или завершена.", cancellationToken: ct);
                        return;
                    }
                    if (room.Player1Id == msgUserId)
                    {
                        await bot.SendMessage(msgChatId, "❌ Нельзя сражаться с самим собой.", cancellationToken: ct);
                        return;
                    }

                    room.Player2Id   = msgUserId;
                    room.Player2Name = username;
                    room.Status      = DuelStatus.InProgress;
                    userDuels[msgUserId] = code;

                    // Генерируем 5 вопросов для обоих
                    await bot.SendChatAction(msgChatId, ChatAction.Typing, cancellationToken: ct);
                    await bot.SendMessage(msgChatId,
                        $"\u2694\uFE0F *{username}* принял вызов!\n\nГенерирую вопросы...",
                        parseMode: ParseMode.Markdown, cancellationToken: ct);

                    // Уведомляем первого игрока
                    await bot.SendMessage(room.Player1Id,
                        $"\u2694\uFE0F *{username}* принял твой вызов! Дуэль начинается!",
                        parseMode: ParseMode.Markdown, cancellationToken: ct);

                    // Генерируем 5 уникальных вопросов для дуэли (широкий охват тем)
                    var tmpTopics = new List<string>();
                    var tmpQuestions = new List<string>();
                    for (int i = 0; i < DuelRoom.TotalQuestions; i++)
                    {
                        QuizQuestion? q = null;
                        // До 3 попыток на случай если Gemini вернёт невалидный JSON
                        for (int attempt = 0; attempt < 3 && q is null; attempt++)
                            q = await GenerateDuelQuestionAsync(tmpTopics, tmpQuestions);

                        if (q != null)
                        {
                            room.Questions.Add(q);
                            if (!string.IsNullOrWhiteSpace(q.Topic)) tmpTopics.Add(q.Topic);
                            var qKey = q.Question.Length > 80 ? q.Question[..80] : q.Question;
                            tmpQuestions.Add(qKey);
                        }
                    }

                    if (room.Questions.Count == 0)
                    {
                        await bot.SendMessage(msgChatId, "❌ Не удалось сгенерировать вопросы.", cancellationToken: ct);
                        duelRooms.TryRemove(code, out _);
                        userDuels.TryRemove(room.Player1Id, out _);
                        userDuels.TryRemove(msgUserId, out _);
                        return;
                    }

                    // Отправляем первый вопрос обоим
                    await SendDuelQuestion(bot, room.Player1Id, room, 0, ct);
                    await SendDuelQuestion(bot, msgChatId, room, 0, ct);
                    room.Player1Answered = 0;
                    room.Player2Answered = 0;
                }
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
                    "\U0001F4DA Учиться"        => "/learn",
                    "\U0001F3C6 Рейтинг"        => "/top",
                    "\U0001F5FA Роадмап"        => "/roadmap",
                    "\u2753 Помощь"             => "/help",
                    _                           => null
                };
                if (menuAction != null)
                {
                    // Вызываем логику напрямую — без Task.Run и лишних сообщений
                    switch (menuAction)
                    {
                        case "/quiz":
                            msgState.Mode = UserMode.Quiz;
                            msgState.QuizCorrect = 0; msgState.QuizTotal = 0;
                            msgState.CurrentQuiz = null;
                            msgState.UsedQuizTopics.Clear();
                            // UsedQuizQuestions НЕ очищаем
                            await bot.SendChatAction(msgChatId, ChatAction.Typing, cancellationToken: ct);
                            await bot.SendMessage(msgChatId,
                                "\U0001F3AF *Режим квиза!*\n\nОтвечай нажимая кнопки A / B / C / D.",
                                parseMode: ParseMode.Markdown, cancellationToken: ct);
                            var qm = await GetNextQuizQuestion(msgState);
                            if (qm != null)
                            {
                                msgState.CurrentQuiz = qm;
                                await bot.SendMessage(msgChatId, FormatQuizQuestion(qm),
                                    parseMode: ParseMode.Markdown, replyMarkup: GetQuizAnswerKeyboard(), cancellationToken: ct);
                            }
                            break;
                        case "/challenge":
                            msgState.Mode = UserMode.Challenge;
                            msgState.UsedChallengeTopics.Clear();
                            await bot.SendChatAction(msgChatId, ChatAction.Typing, cancellationToken: ct);
                            var chm = await GenerateChallengeAsync(msgState.UsedChallengeTopics);
                            if (chm != null)
                            {
                                if (!string.IsNullOrWhiteSpace(chm.Value.Topic))
                                    msgState.UsedChallengeTopics.Add(chm.Value.Topic);
                                msgState.CurrentChallenge = $"{chm.Value.BrokenCode}||HINT||{chm.Value.Hint}||FIXED||{chm.Value.FixedCode}||EXPLAIN||{chm.Value.Explanation}";
                                await bot.SendMessage(msgChatId, FormatChallenge(chm.Value.Description, chm.Value.BrokenCode),
                                    parseMode: ParseMode.Markdown, replyMarkup: GetChallengeKeyboard(), cancellationToken: ct);
                            }
                            break;
                        case "/interview":
                            msgState.Mode = UserMode.Interview;
                            msgState.InterviewHistory.Clear(); msgState.InterviewQuestionCount = 0;
                            await bot.SendChatAction(msgChatId, ChatAction.Typing, cancellationToken: ct);
                            await bot.SendMessage(msgChatId,
                                "\U0001F3A4 *Симуляция собеседования*\n\nОтвечай на вопросы — после 5 вопросов подведу итог.",
                                parseMode: ParseMode.Markdown, cancellationToken: ct);
                            var fq = await ConductInterviewAsync(msgState, "Начни интервью. Поприветствуй и задай первый вопрос.");
                            msgState.InterviewHistory.Add(new ChatMessage("model", fq));
                            await SendAnswerAsync(bot, msgChatId, fq, ct, GetInterviewKeyboard());
                            break;
                        case "/roadmap":
                            await bot.SendMessage(msgChatId, RoadmapText,
                                parseMode: ParseMode.Markdown, cancellationToken: ct);
                            break;
                        case "/learn":
                            msgState.Mode = UserMode.Learn;
                            msgState.CurrentLearnStage = "";
                            await bot.SendMessage(msgChatId,
                                "\U0001F4DA *Режим обучения*\n\nВыбери этап:",
                                parseMode: ParseMode.Markdown,
                                replyMarkup: GetLearnStagesKeyboard(),
                                cancellationToken: ct);
                            break;
                        case "/top":
                            await bot.SendMessage(msgChatId, GetLeaderboard(),
                                parseMode: ParseMode.MarkdownV2, cancellationToken: ct);
                            break;
                        case "/profile":
                            await bot.SendMessage(msgChatId,
                                GetProfile(msgUserId, msgState),
                                parseMode: ParseMode.MarkdownV2, cancellationToken: ct);
                            break;
                        case "/help":
                            await bot.SendMessage(msgChatId,
                                "*Команды:*\n\n" +
                                "/quiz — проверка знаний \U0001F3AF\n" +
                                "/challenge — исправь код \U0001F527\n" +
                                "/interview — собеседование \U0001F3A4\n" +
                                "/learn — изучение тем \U0001F4DA\n" +
                                "/top — таблица лидеров \U0001F3C6\n" +
                                "/profile — мой профиль \U0001F4CA\n" +
                                "/roadmap — план изучения \U0001F5FA\n" +
                                "/clear — очистить историю\n" +
                                "/start — главное меню\n\n" +
                                "Или просто напиши вопрос по C# \U0001F4AC",
                                parseMode: ParseMode.Markdown, cancellationToken: ct);
                            break;
                    }
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

                    // Начисляем очки если ответ выглядит правильным
                    // (Gemini скажет "правильно" или "верно" в начале)
                    var lower = checkResult.ToLower();
                    if (lower.Contains("правильно") || lower.Contains("верно") || lower.Contains("молодец") || lower.Contains("отлично"))
                    {
                        msgState.ChallengesSolved++;
                        AddPoints(msgState, PointsChallengeSolved, "challenge решён");
                        UpdateStreak(msgState);
                    }

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
                "Понимаю текст, фото и .cs файлы.\nГолосовые пока не поддерживаю \U0001F3A4",
                cancellationToken: ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Необработанное исключение в updateHandler");
        }
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
            new BotCommand { Command = "start",     Description = "\U0001F3E0 Главное меню" },
            new BotCommand { Command = "learn",     Description = "\U0001F4DA Изучить тему из роадмапа" },
            new BotCommand { Command = "quiz",      Description = "\U0001F3AF Проверить знания" },
            new BotCommand { Command = "challenge", Description = "\U0001F527 Задание: исправь код" },
            new BotCommand { Command = "interview", Description = "\U0001F3A4 Симуляция собеседования" },
            new BotCommand { Command = "duel",      Description = "\u2694\uFE0F Дуэль с другим игроком" },
            new BotCommand { Command = "top",       Description = "\U0001F3C6 Таблица лидеров" },
            new BotCommand { Command = "profile",   Description = "\U0001F4CA Мой профиль и очки" },
            new BotCommand { Command = "roadmap",   Description = "\U0001F5FA План изучения C#" },
            new BotCommand { Command = "clear",     Description = "\U0001F5D1 Очистить историю диалога" },
            new BotCommand { Command = "help",      Description = "\u2753 Справка" },
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
// Дуэль между двумя пользователями
// ─────────────────────────────────────────────────────────────
enum DuelStatus { WaitingForOpponent, InProgress, Finished }

class DuelRoom
{
    public string   Code       { get; set; } = "";  // 4-значный код приглашения
    public long     Player1Id  { get; set; }
    public string   Player1Name { get; set; } = "";
    public long     Player2Id  { get; set; }
    public string   Player2Name { get; set; } = "";
    public DuelStatus Status   { get; set; } = DuelStatus.WaitingForOpponent;

    // Одинаковые вопросы для обоих игроков
    public List<QuizQuestion> Questions { get; } = new();
    public int CurrentQuestionIndex { get; set; } = 0;

    // Ответы и счёт
    public int Player1Score   { get; set; } = 0;
    public int Player2Score   { get; set; } = 0;
    public int Player1Answered { get; set; } = 0; // сколько вопросов ответил
    public int Player2Answered { get; set; } = 0;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public const int TotalQuestions = 5;
}

// ─────────────────────────────────────────────────────────────
// SQLite — постоянное хранилище данных пользователей
// ─────────────────────────────────────────────────────────────
class UserDb
{
    private readonly string _connStr;

    public UserDb(string dbPath)
    {
        _connStr = $"Data Source={dbPath}";
        InitDb();
    }

    private void InitDb()
    {
        using var conn = new SqliteConnection(_connStr);
        conn.Open();

        Execute(conn, """
            CREATE TABLE IF NOT EXISTS users (
                user_id     INTEGER PRIMARY KEY,
                username    TEXT NOT NULL DEFAULT '',
                points      INTEGER NOT NULL DEFAULT 0,
                quiz_wins   INTEGER NOT NULL DEFAULT 0,
                challenges  INTEGER NOT NULL DEFAULT 0,
                lessons     INTEGER NOT NULL DEFAULT 0,
                streak      INTEGER NOT NULL DEFAULT 0,
                streak_date TEXT NOT NULL DEFAULT '',
                last_active TEXT NOT NULL DEFAULT ''
            )
            """);

        Execute(conn, """
            CREATE TABLE IF NOT EXISTS quiz_questions (
                user_id  INTEGER NOT NULL,
                question TEXT NOT NULL,
                PRIMARY KEY (user_id, question)
            )
            """);

        // Добавляем last_active если колонки ещё нет (миграция старой БД)
        try { Execute(conn, "ALTER TABLE users ADD COLUMN last_active TEXT NOT NULL DEFAULT ''"); }
        catch { /* уже существует */ }
    }

    private static void Execute(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    // Записать пользователя как активного (вызывается при каждом сообщении)
    public void TouchUser(long userId, string username)
    {
        using var conn = new SqliteConnection(_connStr);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO users (user_id, username, last_active)
            VALUES ($id, $name, $ts)
            ON CONFLICT(user_id) DO UPDATE SET
                username    = excluded.username,
                last_active = excluded.last_active;
            """;
        cmd.Parameters.AddWithValue("$id",   userId);
        cmd.Parameters.AddWithValue("$name", username);
        cmd.Parameters.AddWithValue("$ts",   DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"));
        cmd.ExecuteNonQuery();
    }

    // Все пользователи активные за последние 7 дней
    public List<long> GetActiveUserIds(long excludeUserId)
    {
        using var conn = new SqliteConnection(_connStr);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT user_id FROM users
            WHERE user_id != $excl
              AND last_active != ''
              AND last_active > datetime('now', '-7 days')
            ORDER BY last_active DESC
            LIMIT 200
            """;
        cmd.Parameters.AddWithValue("$excl", excludeUserId);
        using var r = cmd.ExecuteReader();
        var result = new List<long>();
        while (r.Read()) result.Add(r.GetInt64(0));
        return result;
    }

    // Для отладки — все пользователи в БД
    public int GetTotalUsersCount()
    {
        using var conn = new SqliteConnection(_connStr);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM users";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    // Загрузить данные пользователя в UserState
    public void LoadUser(long userId, UserState state)
    {
        using var conn = new SqliteConnection(_connStr);
        conn.Open();

        // Основные данные
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT username, points, quiz_wins, challenges, lessons, streak, streak_date FROM users WHERE user_id = $id";
        cmd.Parameters.AddWithValue("$id", userId);
        using var reader = cmd.ExecuteReader();
        if (reader.Read())
        {
            state.Username        = reader.GetString(0);
            state.TotalPoints     = reader.GetInt32(1);
            state.QuizWins        = reader.GetInt32(2);
            state.ChallengesSolved = reader.GetInt32(3);
            state.LessonsRead     = reader.GetInt32(4);
            state.CurrentStreak   = reader.GetInt32(5);
            var dateStr = reader.GetString(6);
            state.LastStreakDate  = DateTime.TryParse(dateStr, out var d) ? d : DateTime.MinValue;
        }
        reader.Close();

        // Виденные вопросы
        using var cmd2 = conn.CreateCommand();
        cmd2.CommandText = "SELECT question FROM quiz_questions WHERE user_id = $id";
        cmd2.Parameters.AddWithValue("$id", userId);
        using var r2 = cmd2.ExecuteReader();
        state.UsedQuizQuestions.Clear();
        while (r2.Read())
            state.UsedQuizQuestions.Add(r2.GetString(0));
    }

    // Сохранить данные пользователя
    public void SaveUser(long userId, UserState state)
    {
        using var conn = new SqliteConnection(_connStr);
        conn.Open();
        using var tx = conn.BeginTransaction();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO users (user_id, username, points, quiz_wins, challenges, lessons, streak, streak_date)
            VALUES ($id, $name, $pts, $qw, $ch, $ls, $str, $sdate)
            ON CONFLICT(user_id) DO UPDATE SET
                username    = excluded.username,
                points      = excluded.points,
                quiz_wins   = excluded.quiz_wins,
                challenges  = excluded.challenges,
                lessons     = excluded.lessons,
                streak      = excluded.streak,
                streak_date = excluded.streak_date;
            """;
        cmd.Parameters.AddWithValue("$id",    userId);
        cmd.Parameters.AddWithValue("$name",  state.Username);
        cmd.Parameters.AddWithValue("$pts",   state.TotalPoints);
        cmd.Parameters.AddWithValue("$qw",    state.QuizWins);
        cmd.Parameters.AddWithValue("$ch",    state.ChallengesSolved);
        cmd.Parameters.AddWithValue("$ls",    state.LessonsRead);
        cmd.Parameters.AddWithValue("$str",   state.CurrentStreak);
        cmd.Parameters.AddWithValue("$sdate", state.LastStreakDate.ToString("yyyy-MM-dd"));
        cmd.ExecuteNonQuery();

        tx.Commit();
    }

    // Сохранить один вопрос
    public void SaveQuestion(long userId, string question)
    {
        var key = question.Length > 80 ? question[..80] : question;
        using var conn = new SqliteConnection(_connStr);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO quiz_questions (user_id, question) VALUES ($id, $q)";
        cmd.Parameters.AddWithValue("$id", userId);
        cmd.Parameters.AddWithValue("$q",  key);
        cmd.ExecuteNonQuery();
    }

    // Топ пользователей для лидерборда
    public List<(string Name, int Points)> GetTopUsers(int limit = 10)
    {
        using var conn = new SqliteConnection(_connStr);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT username, points FROM users WHERE points > 0 AND username != '' ORDER BY points DESC LIMIT $lim";
        cmd.Parameters.AddWithValue("$lim", limit);
        using var r = cmd.ExecuteReader();
        var result = new List<(string, int)>();
        while (r.Read())
            result.Add((r.GetString(0), r.GetInt32(1)));
        return result;
    }
}

// ─────────────────────────────────────────────────────────────
// Типы данных (должны быть после top-level statements)
// ─────────────────────────────────────────────────────────────

enum UserMode { Normal, Quiz, Challenge, Interview, Learn }

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
    // Сами вопросы которые уже были (первые 50 символов для сравнения)
    public List<string> UsedQuizQuestions { get; } = new();
    // Challenge — текущее задание
    public string CurrentChallenge { get; set; } = "";
    // Темы уже использованных заданий (для исключения повторений)
    public List<string> UsedChallengeTopics { get; } = new();
    // Interview — история диалога собеседования
    public List<ChatMessage> InterviewHistory { get; } = new();
    public int InterviewQuestionCount { get; set; } = 0;
    // Learn — текущий выбранный этап
    public string CurrentLearnStage { get; set; } = "";
    // Текущая тема урока (для задания по теме)
    public string CurrentLearnTopic { get; set; } = "";
    // ── Рейтинг ──────────────────────────────────────────────
    public string Username    { get; set; } = "";   // имя для таблицы лидеров
    public int TotalPoints    { get; set; } = 0;    // общий рейтинг
    public int QuizWins       { get; set; } = 0;    // всего правильных в квизе
    public int ChallengesSolved { get; set; } = 0;  // решённых заданий
    public int LessonsRead    { get; set; } = 0;    // прочитанных уроков
    public int CurrentStreak  { get; set; } = 0;    // дней подряд
    public DateTime LastStreakDate { get; set; } = DateTime.MinValue;
}

// Профиль пользователя для таблицы лидеров
class UserProfile
{
    public long   UserId   { get; set; }
    public string Username { get; set; } = "";
    public int    Points   { get; set; }
}
