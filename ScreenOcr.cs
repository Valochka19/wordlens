using System.IO;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace WordLens;

/// <summary>Слово под курсором: само слово, где оно на экране (в пикселях) и текст вокруг него.</summary>
internal sealed record LookupTarget(string Word, Rectangle WordRect, string Context);

/// <summary>Кусок текста, выделенный протяжкой: текст, рамки по строкам (для подсветки) и общий прямоугольник.</summary>
internal sealed record Selection(string Text, IReadOnlyList<Rectangle> LineRects, Rectangle Bounds, string Context, int WordCount);

internal sealed record ScreenWord(string Text, Rectangle Rect);

internal sealed record ScreenLine(IReadOnlyList<ScreenWord> Words, Rectangle Rect)
{
    public string Text { get; } = string.Join(" ", Words.Select(w => w.Text));
}

/// <summary>Снимок монитора в момент нажатия клавиши. Всё распознавание идёт по нему, а не по живому экрану,
/// поэтому наша собственная подсветка и окошко в текст не попадают.</summary>
internal sealed class Snapshot(Bitmap image, Rectangle screen) : IDisposable
{
    public Bitmap Image { get; } = image;
    public Rectangle Screen { get; } = screen;
    public void Dispose() => Image.Dispose();
}

/// <summary>Распознанный текст участка экрана. Все координаты — в пикселях экрана.</summary>
internal sealed class OcrPage(Rectangle area, Rectangle screen, IReadOnlyList<ScreenLine> lines)
{
    // Насколько далеко курсор может быть от слова, чтобы оно ещё считалось «под курсором».
    private const int MaxMissDistance = 24;
    // При выделении начальная точка может быть чуть дальше от текста — рука не так точна, когда тянешь.
    private const int MaxSelectionStartMiss = 70;

    /// <summary>Распознан ли текст возле этой точки, или курсор уже ушёл к краю участка и его надо расширять.</summary>
    public bool Covers(Point p)
    {
        const int margin = 40;
        bool top = p.Y >= area.Top + margin || area.Top <= screen.Top;
        bool bottom = p.Y <= area.Bottom - margin || area.Bottom >= screen.Bottom;
        return top && bottom;
    }

    public LookupTarget? WordAt(Point p)
    {
        if (Nearest(p, MaxMissDistance) is not var (li, wi)) return null;

        var word = lines[li].Words[wi];
        string text = Clean(word.Text);
        if (!text.Any(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z')) return null;

        // Контекст — строка со словом плюс соседние: по нему модель понимает, какое значение слова имеется в виду.
        var parts = new List<string>();
        if (li > 0 && SameParagraph(lines[li - 1].Rect, lines[li].Rect)) parts.Add(lines[li - 1].Text);
        parts.Add(lines[li].Text);
        if (li + 1 < lines.Count && SameParagraph(lines[li].Rect, lines[li + 1].Rect)) parts.Add(lines[li + 1].Text);

        return new LookupTarget(text, word.Rect, string.Join(" ", parts));
    }

    /// <summary>Выделение как в обычном тексте: от слова возле первой точки до слова возле второй, через все строки между ними.</summary>
    public Selection? Select(Point from, Point to)
    {
        if (Nearest(from, MaxSelectionStartMiss) is not var (l1, w1)) return null;
        if (Nearest(to, int.MaxValue) is not var (l2, w2)) return null;
        if (l1 > l2 || (l1 == l2 && w1 > w2)) (l1, w1, l2, w2) = (l2, w2, l1, w1);

        // Между первой и последней строкой могут затесаться чужие: соседняя колонка, боковая панель, другое окно.
        // Берём только те, что лежат в той же полосе по горизонтали и между ними по вертикали.
        var first = lines[l1].Rect;
        var last = lines[l2].Rect;
        int left = Math.Min(first.Left, last.Left), right = Math.Max(first.Right, last.Right);

        var rects = new List<Rectangle>();
        var text = new List<string>();
        var context = new List<string>();
        int count = 0;

        for (int li = l1; li <= l2; li++)
        {
            var line = lines[li];
            if (li != l1 && li != l2)
            {
                int middle = line.Rect.Top + line.Rect.Height / 2;
                bool inside = middle > first.Top && middle < last.Bottom && line.Rect.Right > left && line.Rect.Left < right;
                if (!inside) continue;
            }

            int start = li == l1 ? w1 : 0;
            int end = li == l2 ? w2 : line.Words.Count - 1;
            var words = line.Words.Skip(start).Take(end - start + 1).ToList();
            if (words.Count == 0) continue;

            rects.Add(words.Select(w => w.Rect).Aggregate(Rectangle.Union));
            text.AddRange(words.Select(w => w.Text));
            context.Add(line.Text);
            count += words.Count;
        }

        if (count == 0) return null;
        return new Selection(string.Join(" ", text), rects, rects.Aggregate(Rectangle.Union), string.Join(" ", context), count);
    }

    private (int Line, int Word)? Nearest(Point p, int maxDistance)
    {
        (int, int)? best = null;
        double bestDistance = double.MaxValue;
        for (int li = 0; li < lines.Count; li++)
        {
            for (int wi = 0; wi < lines[li].Words.Count; wi++)
            {
                var r = lines[li].Words[wi].Rect;
                double dx = Math.Max(Math.Max(r.Left - p.X, 0), p.X - r.Right);
                double dy = Math.Max(Math.Max(r.Top - p.Y, 0), p.Y - r.Bottom);
                // Промах по вертикали «дороже»: иначе при выделении курсор между строк цепляет слово не с той строки.
                double distance = Math.Sqrt(dx * dx + dy * dy * 4);
                if (distance < bestDistance) { best = (li, wi); bestDistance = distance; }
            }
        }
        return bestDistance <= maxDistance ? best : null;
    }

    /// <summary>Строки из одного абзаца: идут вплотную друг под другом, шрифт одного размера, по горизонтали перекрываются.
    /// Отсекает заголовки окон, кнопки и текст из соседних окон, которые случайно попали в снимок.</summary>
    private static bool SameParagraph(Rectangle upper, Rectangle lower)
    {
        double height = Math.Min(upper.Height, lower.Height);
        bool similarFont = Math.Max(upper.Height, lower.Height) < height * 1.5;
        bool adjacent = lower.Top - upper.Bottom < height * 1.2 && lower.Top > upper.Top;
        bool overlap = Math.Min(upper.Right, lower.Right) - Math.Max(upper.Left, lower.Left) > 0;
        return similarFont && adjacent && overlap;
    }

    /// <summary>Убирает прилипшие кавычки, скобки и точки: «"hello,» → «hello».</summary>
    private static string Clean(string s)
    {
        int start = 0, end = s.Length;
        while (start < end && !char.IsLetter(s[start])) start++;
        while (end > start && !char.IsLetter(s[end - 1])) end--;
        return s[start..end];
    }
}

/// <summary>Снимает экран и распознаёт на нём английский текст.</summary>
internal sealed class ScreenOcr
{
    // Для одного слова распознаём не весь экран, а полосу вокруг курсора: так быстрее, а строки целиком всё равно попадают.
    private const int WordAreaWidth = 1100, WordAreaHeight = 380;
    // Для выделения — полоса во всю ширину монитора с запасом сверху и снизу от пути курсора.
    private const int BandPadding = 260;
    // Мелкий текст распознаётся заметно лучше, если картинку увеличить.
    private const int Scale = 2;

    private OcrEngine? _engine;

    /// <summary>Снимок монитора, на котором сейчас курсор.</summary>
    public Snapshot Capture(int x, int y)
    {
        var m = Native.MonitorAt(x, y);
        var screen = Rectangle.FromLTRB(m.Left, m.Top, m.Right, m.Bottom);
        var image = new Bitmap(screen.Width, screen.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(image))
            g.CopyFromScreen(screen.Location, Point.Empty, screen.Size);
        return new Snapshot(image, screen);
    }

    public static Rectangle WordArea(Snapshot snapshot, Point p)
    {
        var area = new Rectangle(p.X - WordAreaWidth / 2, p.Y - WordAreaHeight / 2, WordAreaWidth, WordAreaHeight);
        area.Intersect(snapshot.Screen);
        return area;
    }

    public static Rectangle BandArea(Snapshot snapshot, Point a, Point b)
    {
        var s = snapshot.Screen;
        int top = Math.Max(s.Top, Math.Min(a.Y, b.Y) - BandPadding);
        int bottom = Math.Min(s.Bottom, Math.Max(a.Y, b.Y) + BandPadding);
        return Rectangle.FromLTRB(s.Left, top, s.Right, bottom);
    }

    /// <summary>Распознаёт текст на участке снимка. Одновременно по одному снимку можно запускать только одно распознавание.</summary>
    public async Task<OcrPage> Read(Snapshot snapshot, Rectangle area)
    {
        var engine = _engine ??= CreateEngine();

        using var stream = await Task.Run(() => Enlarge(snapshot, area));
        var decoder = await BitmapDecoder.CreateAsync(stream.AsRandomAccessStream());
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        var result = await engine.RecognizeAsync(bitmap);

        var lines = new List<ScreenLine>();
        foreach (var line in result.Lines)
        {
            var words = line.Words.Select(w => new ScreenWord(w.Text, ToScreen(w.BoundingRect, area))).ToList();
            if (words.Count > 0) lines.Add(new ScreenLine(words, words.Select(w => w.Rect).Aggregate(Rectangle.Union)));
        }
        return new OcrPage(area, snapshot.Screen, lines);
    }

    /// <summary>Всё сразу: снять экран и найти слово в точке. Нужно для самопроверки.</summary>
    public async Task<LookupTarget?> FindWordAt(int x, int y)
    {
        using var snapshot = Capture(x, y);
        var point = new Point(x, y);
        var page = await Read(snapshot, WordArea(snapshot, point));
        return page.WordAt(point);
    }

    private static Rectangle ToScreen(Windows.Foundation.Rect r, Rectangle area) => new(
        area.X + (int)(r.X / Scale), area.Y + (int)(r.Y / Scale),
        (int)Math.Ceiling(r.Width / Scale), (int)Math.Ceiling(r.Height / Scale));

    private static OcrEngine CreateEngine()
    {
        var english = OcrEngine.AvailableRecognizerLanguages
            .Where(l => l.LanguageTag.StartsWith("en", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(l => l.LanguageTag.Equals("en-US", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();

        var engine = english is null ? null : OcrEngine.TryCreateFromLanguage(english);
        return engine ?? throw new FriendlyException(
            "Чтобы читать английский текст с экрана, добавь English в Windows: Параметры → Время и язык → Язык и регион → Добавить язык.");
    }

    private static MemoryStream Enlarge(Snapshot snapshot, Rectangle area)
    {
        var source = new Rectangle(area.X - snapshot.Screen.X, area.Y - snapshot.Screen.Y, area.Width, area.Height);
        using var scaled = new Bitmap(area.Width * Scale, area.Height * Scale, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(scaled))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(snapshot.Image, new Rectangle(0, 0, scaled.Width, scaled.Height), source, GraphicsUnit.Pixel);
        }

        var stream = new MemoryStream();
        scaled.Save(stream, ImageFormat.Bmp);
        stream.Position = 0;
        return stream;
    }
}

/// <summary>Ошибка с текстом, который можно как есть показать человеку.</summary>
internal sealed class FriendlyException(string message) : Exception(message);
