using System.Drawing;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace WordLens;

public partial class App : Application
{
    private const int LookupHotkeyId = 1, EscapeHotkeyId = 2;
    private const uint VK_ESCAPE = 0x1B;
    // Окошко закрывается само, когда курсор уходит дальше этого расстояния от слова и окошка.
    private const int CloseDistance = 140;
    // Сдвиг курсора при зажатой клавише, после которого считаем, что человек выделяет фразу, а не просто нажал.
    private const int DragThreshold = 14;

    private Mutex? _singleInstance;
    private HwndSource? _messages;
    private Forms.NotifyIcon? _tray;
    private Settings _settings = null!;
    private WordStore _store = null!;
    private Translator _translator = null!;
    private readonly ScreenOcr _ocr = new();
    private PopupWindow _popup = null!;
    private HighlightWindow _highlight = null!;
    private DispatcherTimer _cursorWatch = null!;
    private MainWindow? _mainWindow;
    private Rectangle _wordRect;
    private int _lookupNumber;
    private bool _escapeRegistered;
    private uint _hotkeyKey;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args is ["--selftest", var reportPath])
        {
            await SelfTest.Run(_ocr, reportPath);
            Shutdown();
            return;
        }

        if (e.Args is ["--shots", var shotsFolder])
        {
            await UiShots.Run(shotsFolder);
            Shutdown();
            return;
        }

        _singleInstance = new Mutex(true, "WordLens.SingleInstance", out bool first);
        if (!first) { Shutdown(); return; }

        _settings = Settings.Load();
        _store = new WordStore();
        _translator = new Translator(_settings);
        _popup = new PopupWindow(_store);
        _highlight = new HighlightWindow();

        _messages = new HwndSource(new HwndSourceParameters("WordLens.Messages") { ParentWindow = Native.HWND_MESSAGE, WindowStyle = 0 });
        _messages.AddHook(OnWindowMessage);

        _cursorWatch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _cursorWatch.Tick += (_, _) => CloseIfCursorLeft();

        CreateTray();

        bool hotkeyOk = ApplyHotkey(_settings.Hotkey);
        if (string.IsNullOrWhiteSpace(_settings.ApiKey) || !hotkeyOk)
            OpenMain(MainPage.Settings);
        else
            _tray!.ShowBalloonTip(4000, "WordLens запущен", $"Наведи курсор на английское слово и нажми {_settings.Hotkey}.", Forms.ToolTipIcon.None);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_messages is not null)
        {
            Native.UnregisterHotKey(_messages.Handle, LookupHotkeyId);
            Native.UnregisterHotKey(_messages.Handle, EscapeHotkeyId);
            _messages.Dispose();
        }
        if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); }
        base.OnExit(e);
    }

    // ---------- горячие клавиши ----------

    private IntPtr OnWindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == Native.WM_HOTKEY)
        {
            handled = true;
            if (wParam.ToInt32() == LookupHotkeyId) Lookup();
            else if (wParam.ToInt32() == EscapeHotkeyId) HidePopup();
        }
        return IntPtr.Zero;
    }

    /// <summary>Занимает клавишу перевода в системе. false — не вышло (клавиша занята или строка непонятная).</summary>
    private bool ApplyHotkey(string hotkey)
    {
        if (_messages is null || !Settings.TryParseHotkey(hotkey, out uint modifiers, out uint key)) return false;
        Native.UnregisterHotKey(_messages.Handle, LookupHotkeyId);
        if (!Native.RegisterHotKey(_messages.Handle, LookupHotkeyId, modifiers | Native.MOD_NOREPEAT, key)) return false;
        _hotkeyKey = key; // по этой клавише потом смотрим, зажата ли она ещё
        return true;
    }

    // ---------- главное: перевод слова под курсором ----------

    /// <summary>
    /// Весь жест от нажатия клавиши до перевода. Коротко нажал — переводим слово под курсором.
    /// Зажал и повёл мышкой — слова подсвечиваются по ходу, а когда клавишу отпустили, переводим выделенную фразу.
    /// </summary>
    private async void Lookup()
    {
        int number = ++_lookupNumber; // если нажали ещё раз, пока шёл прошлый перевод, старый ответ отбрасываем

        // Прячем своё окошко перед снимком, иначе распознаем собственный перевод.
        bool wasVisible = _popup.IsVisible;
        HidePopup();
        if (wasVisible) await Task.Delay(50);

        Native.GetCursorPos(out var c);
        var start = new System.Drawing.Point(c.X, c.Y);
        var cursorRect = new Rectangle(start.X, start.Y, 1, 18);

        Snapshot? snapshot = null;
        Task<OcrPage>? reading = null;
        try
        {
            snapshot = await Task.Run(() => _ocr.Capture(start.X, start.Y));
            var screen = snapshot.Screen;

            OcrPage? page = null;
            bool dragging = false;
            var current = start;

            while (Native.IsKeyDown(_hotkeyKey))
            {
                await Task.Delay(25);
                Native.GetCursorPos(out c);
                current = new System.Drawing.Point(
                    Math.Clamp(c.X, screen.Left, screen.Right - 1), Math.Clamp(c.Y, screen.Top, screen.Bottom - 1));

                if (!dragging && Distance(start, current) > DragThreshold) dragging = true;
                if (!dragging) continue;

                if (reading is { IsCompleted: true }) { page = await reading; reading = null; }

                // Текст распознаём полосой вокруг пути курсора; если курсор подошёл к её краю — распознаём полосу пошире.
                if (reading is null && (page is null || !page.Covers(current)))
                    reading = _ocr.Read(snapshot, ScreenOcr.BandArea(snapshot, start, current));

                _highlight.ShowRects(page?.Select(start, current)?.LineRects ?? []);
            }

            if (!dragging)
            {
                page = await _ocr.Read(snapshot, ScreenOcr.WordArea(snapshot, start));
                await TranslateWord(page.WordAt(start), cursorRect, number);
                return;
            }

            if (reading is not null) { page = await reading; reading = null; }
            if (page is null || !page.Covers(current))
                page = await _ocr.Read(snapshot, ScreenOcr.BandArea(snapshot, start, current));

            var selection = page.Select(start, current);
            if (selection is null || selection.WordCount == 1)
            {
                // Протянули совсем чуть-чуть — считаем, что человек хотел одно слово.
                var point = selection is null ? start : Center(selection.Bounds);
                await TranslateWord(page.WordAt(point), cursorRect, number);
                return;
            }

            await TranslatePhrase(selection, number);
        }
        catch (FriendlyException ex)
        {
            if (number != _lookupNumber) return;
            ShowPopupMessage(ex.Message, _popup.IsVisible ? _wordRect : cursorRect, keepWord: _popup.IsVisible);
        }
        catch (Exception)
        {
            if (number != _lookupNumber) return;
            ShowPopupMessage("Что-то пошло не так. Попробуй ещё раз.", _popup.IsVisible ? _wordRect : cursorRect, keepWord: _popup.IsVisible);
        }
        finally
        {
            // Снимок нельзя выбрасывать, пока по нему ещё идёт распознавание.
            if (reading is not null) { try { await reading; } catch (Exception) { } }
            snapshot?.Dispose();
        }
    }

    private async Task TranslateWord(LookupTarget? target, Rectangle cursorRect, int number)
    {
        if (number != _lookupNumber) return;

        if (target is null)
        {
            ShowPopupMessage("Не вижу здесь английского слова", cursorRect);
            await Task.Delay(1300);
            if (number == _lookupNumber) HidePopup();
            return;
        }

        _wordRect = target.WordRect;
        _highlight.ShowAt(target.WordRect);
        _popup.ShowLoading(target.Word, target.WordRect);
        PopupShown();

        var info = await _translator.Explain(target.Word, target.Context);
        if (number == _lookupNumber) _popup.ShowResult(info, target.Context);
    }

    private async Task TranslatePhrase(Selection selection, int number)
    {
        if (number != _lookupNumber) return;

        _wordRect = selection.Bounds;
        _highlight.ShowRects(selection.LineRects);
        _popup.ShowLoading(selection.Text, selection.Bounds, isPhrase: true);
        PopupShown();

        var info = await _translator.ExplainPhrase(selection.Text, selection.Context);
        if (number == _lookupNumber) _popup.ShowPhrase(info, selection.Context);
    }

    private static double Distance(System.Drawing.Point a, System.Drawing.Point b) =>
        Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));

    private static System.Drawing.Point Center(Rectangle r) => new(r.X + r.Width / 2, r.Y + r.Height / 2);

    private void ShowPopupMessage(string message, Rectangle anchor, bool keepWord = false)
    {
        _wordRect = anchor;
        _popup.ShowMessage(message, anchor, keepWord);
        PopupShown();
    }

    private void PopupShown()
    {
        _cursorWatch.Start();
        // Esc закрывает окошко. Пока оно открыто, Esc не доходит до игры, так что меню паузы не выскочит.
        if (!_escapeRegistered && _messages is not null)
            _escapeRegistered = Native.RegisterHotKey(_messages.Handle, EscapeHotkeyId, 0, VK_ESCAPE);
    }

    private void HidePopup()
    {
        _cursorWatch.Stop();
        _popup.Hide();
        _highlight.Hide();
        if (_escapeRegistered && _messages is not null)
        {
            Native.UnregisterHotKey(_messages.Handle, EscapeHotkeyId);
            _escapeRegistered = false;
        }
    }

    private void CloseIfCursorLeft()
    {
        if (!_popup.IsVisible) return;
        Native.GetCursorPos(out var cursor);
        var keepOpenZone = Rectangle.Union(_popup.ScreenRect, _wordRect);
        keepOpenZone.Inflate(CloseDistance, CloseDistance);
        if (!keepOpenZone.Contains(cursor.X, cursor.Y)) HidePopup();
    }

    // ---------- значок возле часов ----------

    private void CreateTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Открыть WordLens", null, (_, _) => OpenMain(MainPage.Home));
        menu.Items.Add("Мой словарь", null, (_, _) => OpenMain(MainPage.Words));
        menu.Items.Add("Настройки", null, (_, _) => OpenMain(MainPage.Settings));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => Shutdown());

        _tray = new Forms.NotifyIcon { Icon = DrawIcon(), Text = "WordLens", ContextMenuStrip = menu, Visible = true };
        _tray.DoubleClick += (_, _) => OpenMain(MainPage.Home);
    }

    private static Icon DrawIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
            using var back = new SolidBrush(Color.FromArgb(0x2D, 0x7F, 0xF9));
            g.FillEllipse(back, 1, 1, 30, 30);
            using var font = new Font("Segoe UI", 15, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel);
            var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("W", font, Brushes.White, new RectangleF(0, 0, 32, 31), format);
        }
        return Icon.FromHandle(bitmap.GetHicon());
    }

    private void OpenMain(MainPage page)
    {
        if (_mainWindow is null)
        {
            _mainWindow = new MainWindow(_settings, _store, ApplyHotkey, ReleaseHotkey);
            _mainWindow.Closed += (_, _) => _mainWindow = null;
        }
        _mainWindow.Open(page);
    }

    /// <summary>Временно отпускает клавишу перевода — пока её выбирают в настройках, иначе она не «нажмётся» в поле.</summary>
    private void ReleaseHotkey()
    {
        if (_messages is not null) Native.UnregisterHotKey(_messages.Handle, LookupHotkeyId);
    }
}
