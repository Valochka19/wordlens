using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WordLens;

/// <summary>
/// Снимки всех страниц главного окна в PNG — чтобы проверять внешний вид, не кликая руками.
/// Запуск: WordLens.exe --shots папка. Настройки и словарь пользователя не трогает: всё демонстрационное.
/// </summary>
internal static class UiShots
{
    public static async Task Run(string folder)
    {
        Directory.CreateDirectory(folder);

        var settings = new Settings { ApiKey = "sk-demo", Hotkey = "Ctrl+Q" };
        var store = new WordStore(
        [
            new() { Word = "scavenge", Transcription = "[ˈskævɪndʒ]", Translation = "собирать, добывать", Context = "You can scavenge components from barrels along the road.", Added = DateTime.Now },
            new() { Word = "run out of", Transcription = "[rʌn aʊt əv]", Translation = "закончиться", Context = "We're about to run out of fuel, head back to base.", Added = DateTime.Now.AddDays(-1) },
            new() { Word = "blueprint", Transcription = "[ˈbluːprɪnt]", Translation = "чертёж", Context = "Learn the blueprint at a workbench before crafting.", Added = DateTime.Now.AddDays(-2) },
            new() { Word = "decay", Transcription = "[dɪˈkeɪ]", Translation = "разрушаться", Context = "Your base will decay without upkeep in the tool cupboard.", Added = DateTime.Now.AddDays(-9) },
        ]);

        var window = new MainWindow(settings, store, _ => true, () => { });
        foreach (var page in new[] { MainPage.Home, MainPage.Words, MainPage.Settings })
        {
            window.Open(page);
            await Task.Delay(700); // ждём, пока закончится анимация появления страницы
            Save((FrameworkElement)window.Content, Path.Combine(folder, page + ".png"));
        }
        window.Close();
    }

    private static void Save(FrameworkElement root, string path)
    {
        var image = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        image.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var file = File.Create(path);
        encoder.Save(file);
    }
}
