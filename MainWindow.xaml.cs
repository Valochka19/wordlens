using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Win32;

namespace WordLens;

internal enum MainPage { Home, Words, Settings }

/// <summary>Главное окно: «Главная», «Мой словарь», «Настройки».</summary>
public partial class MainWindow : Window
{
    private const string AutostartKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private readonly Settings _settings;
    private readonly WordStore _store;
    private readonly Func<string, bool> _applyHotkey;
    private readonly Action _releaseHotkey;

    /// <param name="applyHotkey">Пробует занять клавишу перевода в системе; false — она уже занята другой программой.</param>
    /// <param name="releaseHotkey">Временно отпускает клавишу, чтобы её можно было «нажать» в поле выбора.</param>
    internal MainWindow(Settings settings, WordStore store, Func<string, bool> applyHotkey, Action releaseHotkey)
    {
        _settings = settings;
        _store = store;
        _applyHotkey = applyHotkey;
        _releaseHotkey = releaseHotkey;
        InitializeComponent();

        SourceInitialized += (_, _) => Native.RoundCorners(new WindowInteropHelper(this).Handle);
        _store.Changed += RefreshWords;
        Closed += (_, _) =>
        {
            _store.Changed -= RefreshWords;
            _applyHotkey(_settings.Hotkey);
        };

        ApiKeyBox.Text = settings.ApiKey;
        HotkeyBox.Text = settings.Hotkey;
        AutostartSwitch.IsChecked = IsAutostartOn();
        RefreshStatus();
        RefreshWords();
    }

    internal void Open(MainPage page)
    {
        (page switch { MainPage.Words => NavWords, MainPage.Settings => NavSettings, _ => NavHome }).IsChecked = true;
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    // ---------- переключение страниц ----------

    private void OnNavChanged(object sender, RoutedEventArgs e)
    {
        if (SettingsPage is null) return; // срабатывает ещё во время загрузки окна, когда страниц нет

        FrameworkElement page = sender == NavWords ? WordsPage : sender == NavSettings ? SettingsPage : HomePage;
        foreach (FrameworkElement p in new FrameworkElement[] { HomePage, WordsPage, SettingsPage })
            p.Visibility = p == page ? Visibility.Visible : Visibility.Collapsed;

        // страница мягко выезжает снизу
        var shift = new TranslateTransform(0, 14);
        page.RenderTransform = shift;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        page.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
        shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    // ---------- главная ----------

    private void RefreshStatus()
    {
        bool ready = !string.IsNullOrWhiteSpace(_settings.ApiKey);
        var color = (Brush)FindResource(ready ? "Good" : "Warn");
        StatusDot.Fill = HeroDot.Fill = color;
        StatusLabel.Text = ready ? "Работает" : "Нужен ключ";
        HeroStatus.Text = ready ? "Готов к работе" : "Добавь ключ в настройках";
        HotkeyCaps.ItemsSource = _settings.Hotkey.Split('+');
    }

    // ---------- словарь ----------

    private void RefreshWords()
    {
        var all = _store.Words;
        StatTotal.Text = all.Count.ToString();
        StatWeek.Text = all.Count(w => w.Added >= DateTime.Now.AddDays(-7)).ToString();
        StatToday.Text = all.Count(w => w.Added.Date == DateTime.Today).ToString();

        RecentWords.ItemsSource = all.Take(10).ToList();
        RecentEmpty.Visibility = all.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        string query = SearchBox.Text.Trim();
        var shown = query.Length == 0
            ? all.ToList()
            : all.Where(w => w.Word.Contains(query, StringComparison.OrdinalIgnoreCase)
                          || w.Translation.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();

        WordCards.ItemsSource = shown;
        WordsCount.Text = all.Count == 0 ? "Слова, которые ты отметил звёздочкой" : $"Слов: {all.Count}";
        WordsEmpty.Text = all.Count == 0
            ? "Здесь пока пусто. Переведи слово и нажми звёздочку в окошке — оно появится тут."
            : shown.Count == 0 ? "Ничего не нашлось." : "";
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (SearchHint is null) return;
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        RefreshWords();
    }

    private void OnRemoveWord(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is SavedWord word) _store.Remove(word);
    }

    private void OnExport(object sender, RoutedEventArgs e)
    {
        if (_store.Words.Count == 0) return;
        var dialog = new SaveFileDialog { FileName = "WordLens для Anki.txt", Filter = "Текстовый файл|*.txt" };
        if (dialog.ShowDialog(this) != true) return;
        _store.ExportForAnki(dialog.FileName);
        WordsCount.Text = "Готово. В Anki: Файл → Импортировать и выбери этот файл.";
    }

    // ---------- настройки ----------

    private void OnHotkeyFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _releaseHotkey();
        HotkeyHint.Text = "Жду нажатия… Нажми клавишу или сочетание.";
    }

    private void OnHotkeyBlur(object sender, KeyboardFocusChangedEventArgs e)
    {
        _applyHotkey(_settings.Hotkey);
        HotkeyHint.Text = "Кликни в поле справа и нажми клавишу или сочетание. Если в игре она уже занята, выбери другую.";
    }

    private void OnHotkeyKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin or Key.Escape or Key.Tab)
            return;

        var parts = new List<string>();
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        parts.Add(key.ToString());
        HotkeyBox.Text = string.Join("+", parts);
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        Keyboard.ClearFocus();

        if (!_applyHotkey(HotkeyBox.Text))
        {
            _applyHotkey(_settings.Hotkey);
            ShowSaveStatus("Эта клавиша уже занята другой программой. Выбери другую.", "Bad");
            return;
        }

        _settings.ApiKey = ApiKeyBox.Text.Trim();
        _settings.Hotkey = HotkeyBox.Text;
        _settings.Save();
        SetAutostart(AutostartSwitch.IsChecked == true);

        RefreshStatus();
        ShowSaveStatus("Сохранено", "Good");
    }

    private void ShowSaveStatus(string text, string colorKey)
    {
        SaveStatus.Text = text;
        SaveStatus.Foreground = (Brush)FindResource(colorKey);
        SaveStatus.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
    }

    private static bool IsAutostartOn()
    {
        using var key = Registry.CurrentUser.OpenSubKey(AutostartKey);
        return key?.GetValue("WordLens") is not null;
    }

    private static void SetAutostart(bool on)
    {
        using var key = Registry.CurrentUser.OpenSubKey(AutostartKey, writable: true);
        if (key is null) return;
        if (on) key.SetValue("WordLens", $"\"{Environment.ProcessPath}\"");
        else key.DeleteValue("WordLens", throwOnMissingValue: false);
    }
}
