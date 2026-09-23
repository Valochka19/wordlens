using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WordLens;

/// <summary>
/// Проверка без человека: показывает окно с известным текстом, «наводится» на слово и «протягивает» выделение
/// через две строки, а в файл пишет, что распозналось.
/// Запуск: WordLens.exe --selftest путь\к\отчёту.txt
/// </summary>
internal static class SelfTest
{
    public static async Task Run(ScreenOcr ocr, string reportPath)
    {
        var lines = new List<string>();
        try
        {
            TextBlock Piece(string text) => new() { Text = text, FontSize = 26, Foreground = Brushes.Black };

            var jumps = Piece("jumps");
            var first = new StackPanel { Orientation = Orientation.Horizontal };
            first.Children.Add(Piece("The quick brown fox "));
            first.Children.Add(jumps);
            first.Children.Add(Piece(" over the lazy dog"));

            var ran = Piece("ran");
            var second = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            second.Children.Add(Piece("and then it "));
            second.Children.Add(ran);
            second.Children.Add(Piece(" out of breath completely."));

            var panel = new StackPanel { Margin = new Thickness(30) };
            panel.Children.Add(first);
            panel.Children.Add(second);

            var window = new Window
            {
                Content = panel, Background = Brushes.White, SizeToContent = SizeToContent.WidthAndHeight,
                Left = 120, Top = 120, Topmost = true, WindowStyle = WindowStyle.ToolWindow, Title = "WordLens selftest",
            };
            window.Show();
            await Task.Delay(900);

            System.Drawing.Point CenterOf(FrameworkElement e)
            {
                var p = e.PointToScreen(new Point(e.ActualWidth / 2, e.ActualHeight / 2));
                return new System.Drawing.Point((int)p.X, (int)p.Y);
            }
            var from = CenterOf(jumps);
            var to = CenterOf(ran);

            using var snapshot = ocr.Capture(from.X, from.Y);
            window.Close();

            var word = (await ocr.Read(snapshot, ScreenOcr.WordArea(snapshot, from))).WordAt(from);
            lines.Add(word is null ? "word: <none>" : $"word: {word.Word}");
            if (word is not null) lines.Add($"word context: {word.Context}");

            var page = await ocr.Read(snapshot, ScreenOcr.BandArea(snapshot, from, to));
            var down = page.Select(from, to);
            var up = page.Select(to, from);
            lines.Add(down is null ? "selection: <none>" : $"selection ({down.WordCount} words, {down.LineRects.Count} lines): {down.Text}");
            lines.Add(up is null ? "selection backwards: <none>" : $"selection backwards: {up.Text}");
        }
        catch (Exception e)
        {
            lines.Add("error: " + e);
        }
        File.WriteAllLines(reportPath, lines);
    }
}
