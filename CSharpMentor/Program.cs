using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

var builder = WebApplication.CreateBuilder(args);

// ─────────────────────────────────────────────────────────────
// HTTP-клиент для Gemini API
// ─────────────────────────────────────────────────────────────
builder.Services.AddHttpClient("gemini", client =>
{
    client.BaseAddress = new Uri("https://generativelanguage.googleapis.com/");
    client.DefaultRequestHeaders.Accept.Add(
        new MediaTypeWithQualityHeaderValue("application/json"));
    // Gemini иногда думает долго — даём 60 секунд
    client.Timeout = TimeSpan.FromSeconds(60);
});

var app = builder.Build();

// ─────────────────────────────────────────────────────────────
// Конфигурация
// ─────────────────────────────────────────────────────────────
var config      = app.Services.GetRequiredService<IConfiguration>();

// Railway передаёт переменные окружения напрямую — проверяем оба варианта
var apiKey   = Environment.GetEnvironmentVariable("GeminiApiKey")
            ?? Environment.GetEnvironmentVariable("GEMINI_API_KEY")
            ?? config["GeminiApiKey"]
            ?? "";

var botToken = Environment.GetEnvironmentVariable("TelegramBotToken")
            ?? Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN")
            ?? config["TelegramBotToken"]
            ?? "";
var httpFactory = app.Services.GetRequiredService<IHttpClientFactory>();
var logger      = app.Services.GetRequiredService<ILogger<Program>>();

// ─────────────────────────────────────────────────────────────
// Системный промпт — «личность» ментора
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
6. Используй жизненные аналогии, чтобы объяснить абстрактные концепции.
7. Структурируй ответ:
   - Краткое объяснение «что это такое»
   - Пример кода с комментариями
   - Разбор примера
   - (опционально) Жизненная аналогия или совет
8. При объяснении ошибок: покажи неправильный код → объясни причину → покажи исправленный код.
9. Форматируй код в блоках ``` с указанием языка (```csharp).
""";

// ─────────────────────────────────────────────────────────────
// Вспомогательная функция: запрос к Gemini API
// ─────────────────────────────────────────────────────────────
async Task<string> AskGeminiAsync(string question)
{
    if (string.IsNullOrWhiteSpace(apiKey))
        return "❌ API-ключ Gemini не настроен на сервере.";

    var body = new
    {
        system_instruction = new
        {
            parts = new[] { new { text = SystemPrompt } }
        },
        contents = new[]
        {
            new { role = "user", parts = new[] { new { text = question } } }
        },
        generationConfig = new { temperature = 0.7, maxOutputTokens = 8192 }
    };

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
            .GetString() ?? "Пустой ответ от модели.";
    }
    catch (Exception ex)
    {
        return $"❌ Ошибка сети: {ex.Message}";
    }
}

// ─────────────────────────────────────────────────────────────
// Telegram Bot — Long Polling (работает без публичного сервера)
// ─────────────────────────────────────────────────────────────
if (!string.IsNullOrWhiteSpace(botToken))
{
    var botClient = new TelegramBotClient(botToken);

    var cts = new CancellationTokenSource();

    botClient.StartReceiving(
        updateHandler: async (bot, update, ct) =>
        {
            // Обрабатываем только текстовые сообщения
            if (update.Message is not { Text: { } text } message) return;

            var chatId   = message.Chat.Id;
            var username = message.From?.FirstName ?? "Студент";

            logger.LogInformation("TG [{User}]: {Text}", username, text);

            // Команда /start
            if (text.Trim() == "/start")
            {
                await bot.SendMessage(
                    chatId,
                    $"👋 Привет, {username}!\n\n" +
                    "Я — *C# Mentor*, твой ИИ-помощник по языку C#.\n\n" +
                    "Задавай любые вопросы о C# и .NET — объясню простым языком с примерами кода.\n\n" +
                    "Например:\n" +
                    "• Что такое async/await?\n" +
                    "• Как работает LINQ?\n" +
                    "• Почему NullReferenceException?\n\n" +
                    "Просто напиши свой вопрос! 🚀",
                    parseMode: ParseMode.Markdown,
                    cancellationToken: ct);
                return;
            }

            // Команда /help
            if (text.Trim() == "/help")
            {
                await bot.SendMessage(
                    chatId,
                    "*C# Mentor — команды:*\n\n" +
                    "/start — приветствие\n" +
                    "/help — эта справка\n\n" +
                    "Для вопроса просто напиши его текстом.",
                    parseMode: ParseMode.Markdown,
                    cancellationToken: ct);
                return;
            }

            // Показываем «печатает...»
            await bot.SendChatAction(chatId, ChatAction.Typing, cancellationToken: ct);

            // Получаем ответ от Gemini
            var answer = await AskGeminiAsync(text);

            // Разбиваем длинный ответ на части если > 4096 символов
            const int maxLen = 4096;
            if (answer.Length <= maxLen)
            {
                await bot.SendMessage(chatId, answer,
                    parseMode: ParseMode.Markdown,
                    cancellationToken: ct);
            }
            else
            {
                for (int i = 0; i < answer.Length; i += maxLen)
                {
                    var chunk = answer.Substring(i, Math.Min(maxLen, answer.Length - i));
                    await bot.SendMessage(chatId, chunk,
                        parseMode: ParseMode.Markdown,
                        cancellationToken: ct);
                    await Task.Delay(300, ct);
                }
            }
        },
        errorHandler: (bot, ex, src, ct) =>
        {
            logger.LogError(ex, "Ошибка Telegram polling");
            return Task.CompletedTask;
        },
        receiverOptions: new ReceiverOptions
        {
            AllowedUpdates = [UpdateType.Message]
        },
        cancellationToken: cts.Token
    );

    // Проверяем что токен рабочий
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
// Веб-часть (статика + REST API)
// ─────────────────────────────────────────────────────────────
app.UseDefaultFiles();
app.UseStaticFiles();

// POST /api/ask — для веб-интерфейса
app.MapPost("/api/ask", async (AskRequest request) =>
{
    if (string.IsNullOrWhiteSpace(apiKey))
        return Results.Json(new { error = "API-ключ не найден." }, statusCode: 500);

    if (string.IsNullOrWhiteSpace(request.Question))
        return Results.BadRequest(new { error = "Вопрос не может быть пустым." });

    var answer = await AskGeminiAsync(request.Question);
    return answer.StartsWith("❌")
        ? Results.Json(new { error = answer }, statusCode: 500)
        : Results.Json(new { answer });
});

app.Run();

record AskRequest(string Question);
