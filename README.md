# WordLens

Point the cursor at any English word on screen, press a hotkey, and a small popup explains that word **in this exact sentence**: contextual translation, IPA, part of speech, the phrasal verb or idiom it belongs to, and a one-line note. Made for people learning English, not for translating whole pages.

A Lookupper-style overlay for Windows. UI and explanations are in Russian, since the target user is a Russian speaker learning English.

![Home](docs/screenshots/Home.png)

## Features

- **Single word.** Hover, tap the hotkey (default `F8`). You get the in-context meaning, lemma, IPA, part of speech, other common meanings, and the phrasal verb or idiom if the word is part of one.
- **Phrase.** Hold the key and drag: words highlight as you go. Release: natural translation of the phrase, 2–5 key words worth learning, and a short grammar note.
- **My words.** Star a word to save it with its context. List with dates, stats on the home page.
- **Pronunciation** via the built-in Windows voice.
- Works on top of games (windowed / borderless), browsers, video players, any window.

## How it works

| Part | Built with | Why |
|---|---|---|
| Screen capture and text recognition | `Windows.Media.Ocr` (ships with Windows 10/11) | Returns coordinates for every word, nothing to download |
| Hotkey | `RegisterHotKey` | No keyboard hooks, no injection into other processes, anti-cheat safe |
| Popups | WPF windows with `WS_EX_NOACTIVATE` | Never steal focus from the game or app |
| Explanation | DeepSeek `deepseek-chat`, strict JSON response | Understands context and fixes OCR mistakes by meaning |
| Settings and word list | `%AppData%\WordLens\*.json` | The API key stays on the user's machine |

The screenshot is taken once at the moment the key is pressed and OCR runs on that image. The app never reads other processes' memory and never intercepts input.

## Running

Requires Windows 10 (build 19041) or 11, .NET 9, and the English OCR language pack (Settings → Time & Language → Language → English → Text recognition).

```powershell
dotnet build -c Release
.\bin\Release\net9.0-windows10.0.19041.0\WordLens.exe
```

Paste a DeepSeek API key (platform.deepseek.com) in Settings and change the hotkey if you like.

Utility modes:

```powershell
WordLens.exe --selftest report.txt     # OCR and drag-select self-test, no API calls
WordLens.exe --shots folder            # renders every page of the main window to PNG with demo data
```

## Screenshots

| My words | Settings |
|---|---|
| ![](docs/screenshots/Words.png) | ![](docs/screenshots/Settings.png) |

## Limitations

- Windows only. Games must run windowed or borderless: exclusive fullscreen yields an empty capture.
- Recognition quality depends on the font. Regular UI and web text is reliable; stylised game fonts are hit and miss.
- You need your own DeepSeek key; requests are paid (a fraction of a cent per word).

## Roadmap

Anki export, in-app spaced repetition, start with Windows, single-file build.

---

*По-русски: наводишь курсор на английское слово, жмёшь клавишу, видишь перевод именно в этом предложении. Для тех, кто учит английский.*
