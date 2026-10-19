using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpClient("gemini", client =>
{
    client.BaseAddress = new Uri("https://generativelanguage.googleapis.com/");
    client.DefaultRequestHeaders.Accept.Add(
        new MediaTypeWithQualityHeaderValue("application/json"));
    client.Timeout = TimeSpan.FromSeconds(90);
});

// HTTP-клиент для скачивания файлов из Telegram (без BaseAddress)
builder.Services.AddHttpClient("files");

var app = builder.Build();

// ─────────────────────────────────────────────────────────────
// Конфигурация
// ─────────────────────────────────────────────────────────────
var config = app.Services.GetRequiredService<IConfiguration>();

var apiKey = Environment.GetEnvironmentVariable("GeminiApiKey")
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
// Системный промпт
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
9. Если прислали скриншот или фото с кодом/ошибкой — внимательно разбери что на нём изображено.
10. Если прислали .cs файл — проанализируй код, найди проблемы и предложи улучшения.
11. Форматируй код в блоках ``` с указанием языка (```csharp).
""";

// ─────────────────────────────────────────────────────────────
// Скачать файл из Telegram и вернуть bytes + mimeType
// ─────────────────────────────────────────────────────────────
async Task<(byte[] Data, string MimeType)?> DownloadTelegramFileAsync(
    ITelegramBotClient bot, string fileId, string defaultMime)
{
    try
    {
        var file = await bot.GetFile(fileId);
        if (file.FilePath is null) return null;

        var url = $"https://api.telegram.org/file/bot{botToken}/{file.FilePath}";
        var http = httpFactory.CreateClient("files");
        var data = await http.GetByteArrayAsync(url);

        // Определяем MIME по расширению файла
        var ext = Path.GetExtension(file.FilePath).ToLowerInvariant();
        var mime = ext switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png"            => "image/png",
            ".gif"            => "image/gif",
            ".webp"           => "image/webp",
            ".cs"             => "text/plain",
            ".txt"            => "text/plain",
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
// Запрос к Gemini — только текст
// ─────────────────────────────────────────────────────────────
async Task<string> AskGeminiAsync(string question)
{
    if (string.IsNullOrWhiteSpace(apiKey))
        return "❌ API-ключ Gemini не настроен на сервере.";

    var body = new
    {
        system_instruction = new { parts = new[] { new { text = SystemPrompt } } },
        contents = new[]
        {
            new { role = "user", parts = new[] { new { text = question } } }
        },
        generationConfig = new { temperature = 0.7, maxOutputTokens = 8192 }
    };

    return await CallGeminiAsync(body);
}

// ─────────────────────────────────────────────────────────────
// Запрос к Gemini — текст + изображение (base64)
// ─────────────────────────────────────────────────────────────
async Task<string> AskGeminiWithImageAsync(string question, byte[] imageData, string mimeType)
{
    if (string.IsNullOrWhiteSpace(apiKey))
        return "❌ API-ключ Gemini не настроен на сервере.";

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
                        ? "Пожалуйста, разбери код или ошибку на этом скриншоте. Объясни что здесь происходит и как это исправить."
                        : question }
                }
            }
        },
        generationConfig = new { temperature = 0.7, maxOutputTokens = 8192 }
    };

    return await CallGeminiAsync(body);
}

// ─────────────────────────────────────────────────────────────
// Запрос к Gemini — текст + текстовый файл (.cs, .txt)
// ─────────────────────────────────────────────────────────────
async Task<string> AskGeminiWithFileAsync(string question, byte[] fileData, string fileName)
{
    if (string.IsNullOrWhiteSpace(apiKey))
        return "❌ API-ключ Gemini не настроен на сервере.";

    var fileText = Encoding.UTF8.GetString(fileData);
    var prompt = string.IsNullOrWhiteSpace(question)
        ? $"Проанализируй этот C# код из файла `{fileName}`. Найди проблемы, объясни что он делает и предложи улучшения:\n\n```csharp\n{fileText}\n```"
        : $"{question}\n\nВот код из файла `{fileName}`:\n\n```csharp\n{fileText}\n```";

    return await AskGeminiAsync(prompt);
}

// ─────────────────────────────────────────────────────────────
// Общий метод вызова Gemini API
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
            .GetString() ?? "Пустой ответ от модели.";
    }
    catch (Exception ex)
    {
        return $"❌ Ошибка сети: {ex.Message}";
    }
}

// ─────────────────────────────────────────────────────────────
// Отправить ответ в Telegram (с разбивкой на части если > 4096)
// ─────────────────────────────────────────────────────────────
async Task SendAnswerAsync(ITelegramBotClient bot, long chatId, string answer, CancellationToken ct)
{
    const int maxLen = 4000;

    // Пробуем отправить с Markdown, если ошибка — отправляем plain text
    async Task SendChunk(string chunk)
    {
        try
        {
            await bot.SendMessage(chatId, chunk, parseMode: ParseMode.Markdown, cancellationToken: ct);
        }
        catch
        {
            await bot.SendMessage(chatId, chunk, cancellationToken: ct);
        }
    }

    if (answer.Length <= maxLen)
    {
        await SendChunk(answer);
        return;
    }

    for (int i = 0; i < answer.Length; i += maxLen)
    {
        await SendChunk(answer.Substring(i, Math.Min(maxLen, answer.Length - i)));
        await Task.Delay(300, ct);
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
            if (update.Message is not { } message) return;

            var chatId   = message.Chat.Id;
            var username = message.From?.FirstName ?? "Студент";
            var caption  = message.Caption ?? "";  // подпись под фото/файлом

            // ── /start ───────────────────────────────────────
            if (message.Text == "/start")
            {
                await bot.SendMessage(chatId,
                    $"👋 Привет, *{username}*\\!\n\n" +
                    "Я — *C\\# Mentor*, твой ИИ\\-помощник по языку C\\#\\.\n\n" +
                    "Что я умею:\n" +
                    "💬 Отвечать на вопросы по C\\# и \\.NET\n" +
                    "📷 Разбирать скриншоты с кодом или ошибками\n" +
                    "📄 Анализировать \\.cs файлы\n\n" +
                    "Просто напиши вопрос, пришли скриншот или файл\\! 🚀",
                    parseMode: ParseMode.MarkdownV2, cancellationToken: ct);
                return;
            }

            // ── /help ────────────────────────────────────────
            if (message.Text == "/help")
            {
                await bot.SendMessage(chatId,
                    "*C\\# Mentor — что умею:*\n\n" +
                    "💬 Задай вопрос текстом\n" +
                    "📷 Пришли скриншот с ошибкой или кодом\n" +
                    "📄 Пришли \\`.cs\\` или \\`.txt\\` файл с кодом\n" +
                    "📷\\+💬 Фото с подписью — подпись станет вопросом\n\n" +
                    "/start — приветствие",
                    parseMode: ParseMode.MarkdownV2, cancellationToken: ct);
                return;
            }

            await bot.SendChatAction(chatId, ChatAction.Typing, cancellationToken: ct);

            // ── Фото ─────────────────────────────────────────
            if (message.Photo is { } photos && photos.Length > 0)
            {
                logger.LogInformation("TG [{User}]: фото (caption: '{Cap}')", username, caption);

                // Берём фото в наилучшем качестве (последний элемент массива)
                var bestPhoto = photos[^1];
                var file = await DownloadTelegramFileAsync(bot, bestPhoto.FileId, "image/jpeg");

                if (file is null)
                {
                    await bot.SendMessage(chatId, "❌ Не удалось скачать фото.", cancellationToken: ct);
                    return;
                }

                var answer = await AskGeminiWithImageAsync(caption, file.Value.Data, file.Value.MimeType);
                await SendAnswerAsync(bot, chatId, answer, ct);
                return;
            }

            // ── Документ (.cs, .txt, .png, .jpg как файл) ───
            if (message.Document is { } doc)
            {
                var fileName  = doc.FileName ?? "file";
                var ext       = Path.GetExtension(fileName).ToLowerInvariant();
                var isCode    = ext is ".cs" or ".txt" or ".json" or ".xml" or ".csproj";
                var isImage   = ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp";

                logger.LogInformation("TG [{User}]: файл '{File}'", username, fileName);

                var file = await DownloadTelegramFileAsync(bot, doc.FileId,
                    isImage ? "image/jpeg" : "text/plain");

                if (file is null)
                {
                    await bot.SendMessage(chatId, "❌ Не удалось скачать файл.", cancellationToken: ct);
                    return;
                }

                string answer;
                if (isImage)
                    answer = await AskGeminiWithImageAsync(caption, file.Value.Data, file.Value.MimeType);
                else if (isCode)
                    answer = await AskGeminiWithFileAsync(caption, file.Value.Data, fileName);
                else
                    answer = $"⚠️ Формат `{ext}` не поддерживается.\n\nПришли `.cs`, `.txt` файл или скриншот.";

                await SendAnswerAsync(bot, chatId, answer, ct);
                return;
            }

            // ── Обычный текстовый вопрос ─────────────────────
            if (message.Text is { } text)
            {
                logger.LogInformation("TG [{User}]: {Text}", username, text);
                var answer = await AskGeminiAsync(text);
                await SendAnswerAsync(bot, chatId, answer, ct);
                return;
            }

            // ── Голосовое / стикер / прочее ──────────────────
            await bot.SendMessage(chatId,
                "Я понимаю текст, фото и .cs файлы.\nГолосовые сообщения пока не поддерживаю 🎤",
                cancellationToken: ct);
        },
        errorHandler: (bot, ex, src, ct) =>
        {
            logger.LogError(ex, "Ошибка Telegram polling");
            return Task.CompletedTask;
        },
        receiverOptions: new ReceiverOptions { AllowedUpdates = [UpdateType.Message] },
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

    var answer = await AskGeminiAsync(request.Question);
    return answer.StartsWith("❌")
        ? Results.Json(new { error = answer }, statusCode: 500)
        : Results.Json(new { answer });
});

app.Run();

record AskRequest(string Question);
