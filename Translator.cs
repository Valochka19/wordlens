using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace WordLens;

/// <summary>Всё, что показываем про слово в окошке.</summary>
internal sealed record WordInfo(
    string Word, string Lemma, string Transcription, string PartOfSpeech,
    string Translation, string OtherMeanings, string Phrase, string PhraseTranslation, string Note);

/// <summary>Пара «слово или выражение — перевод» в разборе фразы.</summary>
internal sealed record KeyWord(string English, string Russian);

/// <summary>Всё, что показываем про выделенную фразу.</summary>
internal sealed record PhraseInfo(string Source, string Translation, IReadOnlyList<KeyWord> KeyWords, string Note);

/// <summary>Спрашивает у DeepSeek, что слово значит именно в этом предложении.</summary>
internal sealed class Translator(Settings settings)
{
    private const string Instructions = """
        Ты помощник русскоязычного человека, который учит английский. Он указал на слово на экране.
        Тебе дают слово и текст вокруг него (текст распознан с экрана, в нём могут быть ошибки и обрывки — исправляй их по смыслу).
        Ответь строго JSON-объектом с полями:
        "word" — слово, как оно стоит в тексте (с исправленной опечаткой распознавания, если была);
        "lemma" — начальная форма;
        "transcription" — транскрипция IPA начальной формы, в квадратных скобках;
        "pos" — часть речи по-русски, одним-двумя словами;
        "translation" — перевод именно в этом контексте, 1–4 слова;
        "other" — до трёх других частых значений через запятую, строкой; если их нет — пустая строка;
        "phrase" — если слово здесь часть фразового глагола, идиомы или устойчивого выражения, то это выражение целиком, иначе пустая строка;
        "phrase_translation" — перевод этого выражения или пустая строка;
        "note" — одно короткое предложение по-русски: почему здесь такое значение или как это слово употребляют. Без воды.
        """;

    private const string PhraseInstructions = """
        Ты помощник русскоязычного человека, который учит английский. Он выделил на экране фразу или предложение.
        Тебе дают выделенный текст и строки, в которых он стоит (текст распознан с экрана, в нём могут быть ошибки и обрывки — исправляй их по смыслу).
        Ответь строго JSON-объектом с полями:
        "source" — выделенный текст с исправленными ошибками распознавания;
        "translation" — естественный перевод на русский, как сказал бы живой человек;
        "words" — массив из 2–5 объектов {"en": "...", "ru": "..."}: самые полезные для изучения слова, фразовые глаголы и устойчивые выражения из этой фразы (в начальной форме) с переводом в этом контексте. Очевидные слова вроде the, is, you не включай;
        "note" — одно короткое предложение по-русски про грамматику или оборот, если в фразе есть что-то неочевидное для изучающего; иначе пустая строка.
        """;

    // Больше этого за раз не переводим: программа про изучение слов и фраз, а не про перевод страниц.
    private const int MaxPhraseLength = 700;

    private readonly Dictionary<string, PhraseInfo> _phraseCache = new(StringComparer.OrdinalIgnoreCase);

    public async Task<PhraseInfo> ExplainPhrase(string phrase, string context)
    {
        if (phrase.Length > MaxPhraseLength)
            throw new FriendlyException("Это слишком много за раз. Выдели одно-два предложения.");
        if (_phraseCache.TryGetValue(phrase, out var cached)) return cached;

        string content = await Ask(PhraseInstructions, $"Выделено: {phrase}\nСтроки целиком: {context}", maxTokens: 900);
        try
        {
            using var doc = JsonDocument.Parse(content);
            var o = doc.RootElement;
            string translation = Text(o, "translation");
            if (translation.Length == 0) throw new JsonException();

            var words = new List<KeyWord>();
            if (o.TryGetProperty("words", out var array) && array.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in array.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.Object))
                {
                    string en = Text(item, "en"), ru = Text(item, "ru");
                    if (en.Length > 0 && ru.Length > 0) words.Add(new KeyWord(en, ru));
                }
            }

            var info = new PhraseInfo(Text(o, "source") is { Length: > 0 } s ? s : phrase, translation, words, Text(o, "note"));
            _phraseCache[phrase] = info;
            return info;
        }
        catch (JsonException)
        {
            throw new FriendlyException("Переводчик ответил что-то непонятное. Попробуй ещё раз.");
        }
    }

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(25) };
    private readonly Dictionary<string, WordInfo> _cache = new(StringComparer.OrdinalIgnoreCase);

    public async Task<WordInfo> Explain(string word, string context)
    {
        string cacheKey = word + "\n" + context;
        if (_cache.TryGetValue(cacheKey, out var cached)) return cached;

        string content = await Ask(Instructions, $"Слово: {word}\nТекст вокруг: {context}", maxTokens: 400);
        try
        {
            using var doc = JsonDocument.Parse(content);
            var o = doc.RootElement;
            string translation = Text(o, "translation");
            if (translation.Length == 0) throw new JsonException();

            var info = new WordInfo(
                Word: Text(o, "word") is { Length: > 0 } w ? w : word,
                Lemma: Text(o, "lemma") is { Length: > 0 } l ? l : word.ToLowerInvariant(),
                Transcription: Text(o, "transcription"),
                PartOfSpeech: Text(o, "pos"),
                Translation: translation,
                OtherMeanings: Text(o, "other"),
                Phrase: Text(o, "phrase"),
                PhraseTranslation: Text(o, "phrase_translation"),
                Note: Text(o, "note"));
            _cache[cacheKey] = info;
            return info;
        }
        catch (JsonException)
        {
            throw new FriendlyException("Переводчик ответил что-то непонятное. Попробуй ещё раз.");
        }
    }

    /// <summary>Один запрос к DeepSeek. Возвращает JSON-текст ответа модели; все сбои превращает в понятные человеку сообщения.</summary>
    private async Task<string> Ask(string instructions, string question, int maxTokens)
    {
        if (string.IsNullOrWhiteSpace(settings.ApiKey))
            throw new FriendlyException("Сначала добавь ключ DeepSeek: значок WordLens возле часов → Настройки.");

        var body = new
        {
            model = settings.Model,
            temperature = 0.2,
            max_tokens = maxTokens,
            response_format = new { type = "json_object" },
            messages = new object[]
            {
                new { role = "system", content = instructions },
                new { role = "user", content = question },
            },
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.deepseek.com/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey.Trim());
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        try
        {
            using var response = await Http.SendAsync(request);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                throw new FriendlyException("Ключ DeepSeek не подошёл. Проверь его в настройках.");
            if (response.StatusCode == HttpStatusCode.PaymentRequired)
                throw new FriendlyException("На счёте DeepSeek закончились деньги.");
            if (!response.IsSuccessStatusCode)
                throw new FriendlyException("Переводчик сейчас не отвечает. Попробуй ещё раз чуть позже.");

            using var envelope = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            string content = envelope.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";

            // На случай, если модель всё-таки обернула ответ в ```json ... ```
            int open = content.IndexOf('{'), close = content.LastIndexOf('}');
            return open >= 0 && close > open ? content[open..(close + 1)] : content;
        }
        catch (HttpRequestException)
        {
            throw new FriendlyException("Нет связи с переводчиком. Проверь интернет.");
        }
        catch (TaskCanceledException)
        {
            throw new FriendlyException("Переводчик слишком долго думает. Попробуй ещё раз.");
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
        {
            throw new FriendlyException("Переводчик ответил что-то непонятное. Попробуй ещё раз.");
        }
    }

    private static string Text(JsonElement o, string name)
    {
        if (!o.TryGetProperty(name, out var v)) return "";
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString()!.Trim(),
            JsonValueKind.Array => string.Join(", ", v.EnumerateArray().Select(x => x.ToString().Trim()).Where(x => x.Length > 0)),
            _ => "",
        };
    }
}
