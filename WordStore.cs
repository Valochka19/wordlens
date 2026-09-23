using System.IO;
using System.Text;
using System.Text.Json;

namespace WordLens;

internal sealed class SavedWord
{
    public string Word { get; set; } = "";
    public string Transcription { get; set; } = "";
    public string Translation { get; set; } = "";
    public string Context { get; set; } = "";
    public string Note { get; set; } = "";
    public DateTime Added { get; set; }

    public string AddedText => Added.ToString("dd.MM.yyyy");
}

/// <summary>Личный словарь: слова, которые пользователь отметил звёздочкой. Лежит в %AppData%\WordLens\words.json.</summary>
internal sealed class WordStore
{
    private static string FilePath => Path.Combine(Settings.Folder, "words.json");
    private readonly List<SavedWord> _words;

    public event Action? Changed;

    public WordStore()
    {
        try
        {
            _words = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<List<SavedWord>>(File.ReadAllText(FilePath)) ?? []
                : [];
        }
        catch (Exception e) when (e is JsonException or IOException) { _words = []; }
    }

    /// <summary>Словарь «понарошку» для снимков интерфейса: живёт только в памяти, файл пользователя не трогает.</summary>
    public WordStore(IEnumerable<SavedWord> demoWords)
    {
        _words = demoWords.ToList();
        _demo = true;
    }

    private readonly bool _demo;

    public IReadOnlyList<SavedWord> Words => _words;

    public bool Contains(string lemma) => _words.Any(w => w.Word.Equals(lemma, StringComparison.OrdinalIgnoreCase));

    /// <summary>Добавляет слово, а если оно уже есть — убирает. Возвращает true, если слово теперь в словаре.</summary>
    public bool Toggle(WordInfo info, string context)
    {
        bool removed = _words.RemoveAll(w => w.Word.Equals(info.Lemma, StringComparison.OrdinalIgnoreCase)) > 0;
        if (!removed)
        {
            _words.Insert(0, new SavedWord
            {
                Word = info.Lemma,
                Transcription = info.Transcription,
                Translation = info.Translation,
                Context = context,
                Note = info.Note,
                Added = DateTime.Now,
            });
        }
        Save();
        return !removed;
    }

    public void Remove(SavedWord word)
    {
        if (_words.Remove(word)) Save();
    }

    /// <summary>Файл для импорта в Anki: лицевая сторона — слово, оборот — перевод и предложение, где оно встретилось.</summary>
    public void ExportForAnki(string path)
    {
        static string Cell(string s) => s.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');
        var sb = new StringBuilder();
        foreach (var w in _words)
            sb.Append(Cell($"{w.Word} {w.Transcription}".Trim())).Append('\t')
              .Append(Cell(w.Translation)).Append("<br><i>").Append(Cell(w.Context)).Append("</i>").Append('\n');
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    private void Save()
    {
        if (_demo) { Changed?.Invoke(); return; }
        Directory.CreateDirectory(Settings.Folder);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(_words, new JsonSerializerOptions { WriteIndented = true }));
        Changed?.Invoke();
    }
}
