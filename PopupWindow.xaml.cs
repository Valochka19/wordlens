using System.Drawing;
using System.Globalization;
using System.Speech.Synthesis;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace WordLens;

/// <summary>Окошко с переводом рядом со словом. Не забирает фокус, чтобы игра не сворачивалась и не теряла управление.</summary>
public partial class PopupWindow : Window
{
    private const string StarEmpty = "", StarFilled = "";

    private readonly WordStore _store;
    private SpeechSynthesizer? _voice;
    private IntPtr _hwnd;
    private Rectangle _anchor;
    private WordInfo? _info;
    private string _context = "";

    internal PopupWindow(WordStore store)
    {
        _store = store;
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            Native.MakeOverlay(_hwnd, clickThrough: false);
        };
        SizeChanged += (_, _) => Dispatcher.BeginInvoke(Reposition, DispatcherPriority.Loaded);
    }

    /// <summary>Прямоугольник окошка на экране в пикселях — по нему решаем, ушёл ли курсор достаточно далеко, чтобы закрыться.</summary>
    internal Rectangle ScreenRect
    {
        get
        {
            Native.GetWindowRect(_hwnd, out var r);
            return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
        }
    }

    internal void ShowLoading(string text, Rectangle anchor, bool isPhrase = false)
    {
        _info = null;
        SetMode(isPhrase);
        Fill(Shorten(text), "", "", "", "", "", "", "", status: "Перевожу…", buttons: false);
        Present(anchor);
    }

    /// <summary>Перевод выделенной фразы: сам перевод, разбор полезных слов из неё и заметка про грамматику.</summary>
    internal void ShowPhrase(PhraseInfo info, string context)
    {
        // Фразу кладём в словарь и озвучиваем так же, как слово, поэтому приводим её к тому же виду.
        _info = new WordInfo(info.Source, info.Source, "", "", info.Translation, "", "", "", info.Note);
        _context = context;
        SetMode(isPhrase: true);
        Fill(Shorten(info.Source), "", "", info.Translation, "", "", "", info.Note, status: "", buttons: true);
        KeyWordsList.ItemsSource = info.KeyWords;
        KeyWordsList.Visibility = info.KeyWords.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        StarButton.Content = _store.Contains(info.Source) ? StarFilled : StarEmpty;
    }

    /// <summary>Фраза длиннее слова: заголовок мельче, окошко шире.</summary>
    private void SetMode(bool isPhrase)
    {
        WordRun.FontSize = isPhrase ? 14 : 20;
        WordRun.FontWeight = isPhrase ? FontWeights.Normal : FontWeights.SemiBold;
        TranslationText.FontSize = isPhrase ? 16 : 19;
        Card.MaxWidth = isPhrase ? 500 : 400;
    }

    private static string Shorten(string text) => text.Length <= 220 ? text : text[..220].TrimEnd() + "…";

    internal void ShowResult(WordInfo info, string context)
    {
        _info = info;
        _context = context;
        SetMode(isPhrase: false);
        Fill(info.Word, info.Transcription, info.PartOfSpeech, info.Translation, info.OtherMeanings,
            info.Phrase, info.PhraseTranslation, info.Note, status: "", buttons: true);
        StarButton.Content = _store.Contains(info.Lemma) ? StarFilled : StarEmpty;
    }

    /// <summary>Сообщение вместо перевода. Если слово уже показано, оно остаётся в заголовке.</summary>
    internal void ShowMessage(string message, Rectangle anchor, bool keepWord = false)
    {
        _info = null;
        Fill(keepWord ? WordRun.Text : "", "", "", "", "", "", "", "", status: message, buttons: false);
        Present(anchor);
    }

    private void Fill(string word, string transcription, string pos, string translation, string other,
        string phrase, string phraseTranslation, string note, string status, bool buttons)
    {
        WordRun.Text = word;
        TranscriptionRun.Text = transcription.Length > 0 ? "  " + transcription : "";
        SetText(PartOfSpeechText, pos);
        SetText(TranslationText, translation);
        SetText(OtherText, other.Length > 0 ? "ещё: " + other : "");
        PhraseBox.Visibility = phrase.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        PhraseRun.Text = phrase;
        PhraseTranslationRun.Text = phraseTranslation.Length > 0 ? " — " + phraseTranslation : "";
        SetText(NoteText, note);
        SetText(StatusText, status);
        KeyWordsList.ItemsSource = null; // разбор фразы показывает только ShowPhrase, сразу после этого метода
        KeyWordsList.Visibility = Visibility.Collapsed;
        Buttons.Visibility = buttons ? Visibility.Visible : Visibility.Collapsed;
    }

    private static void SetText(System.Windows.Controls.TextBlock block, string text)
    {
        block.Text = text;
        block.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Present(Rectangle anchor)
    {
        _anchor = anchor;
        if (!IsVisible)
        {
            Opacity = 0; // показываем только после того, как встанем на место, иначе окно мигнёт в старой точке
            Show();
        }
        Dispatcher.BeginInvoke(Reposition, DispatcherPriority.Loaded);
    }

    /// <summary>Ставит окошко под словом; если снизу не влезает — над словом. Не даёт вылезти за край монитора.</summary>
    private void Reposition()
    {
        if (!IsVisible || _hwnd == IntPtr.Zero) return;

        var size = ScreenRect.Size;
        var work = Native.WorkAreaAt(_anchor.X, _anchor.Y);

        int x = _anchor.Left - 10;
        int y = _anchor.Bottom;
        if (y + size.Height > work.Bottom) y = _anchor.Top - size.Height;
        x = Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - size.Width));
        y = Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - size.Height));

        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        Opacity = 1;
    }

    private void OnStar(object sender, RoutedEventArgs e)
    {
        if (_info is null) return;
        StarButton.Content = _store.Toggle(_info, _context) ? StarFilled : StarEmpty;
    }

    private void OnSpeak(object sender, RoutedEventArgs e)
    {
        if (_info is null) return;
        try
        {
            if (_voice is null)
            {
                _voice = new SpeechSynthesizer();
                _voice.SelectVoiceByHints(VoiceGender.NotSet, VoiceAge.NotSet, 0, new CultureInfo("en-US"));
                _voice.Rate = -1;
            }
            _voice.SpeakAsyncCancelAll();
            _voice.SpeakAsync(_info.Phrase.Length > 0 ? _info.Phrase : _info.Lemma);
        }
        catch (Exception)
        {
            SetText(StatusText, "Не получилось озвучить: в Windows нет английского голоса.");
        }
    }
}
