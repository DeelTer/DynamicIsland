# Island

A Dynamic Island–style widget for the top edge of a Windows screen.

It shows a compact pill at the top of the monitor. Hovering over it opens a panel with:

- **Media controls** for any player that reports to Windows (Spotify, browsers and others): artwork, progress bar and buttons.
- **Tools**: screenshots of an area or the whole screen, a color picker, offline dictation (whisper.cpp), a timer and a currency/crypto converter.
- **Shelf**: a temporary place to keep files and drag them out again.
- **Dock**: pinned apps and folders.
- **Clipboard history**: search, an image grid, pins and drag-out. Unpinned entries expire after 7 days.
- **Quick note.**

You can drag the island along the top edge and onto another monitor. It moves away from fullscreen apps and games.

## Stack

- **Core:** C# / .NET 9, WinForms and Win32 (`IslandApp.cs`, `Features.cs`, `Native.cs`). It handles the transparent click-through window, the motion physics, media via Windows media sessions (GSMTC), the clipboard, screen capture and hotkeys.
- **UI:** plain HTML, CSS and JS rendered in Microsoft WebView2 (`web/`). Icons are [Phosphor](https://phosphoricons.com) (MIT).
- **Dictation:** [whisper.cpp](https://github.com/ggerganov/whisper.cpp) (MIT) in `assets/speech/`.
- **Currency rates:** the public Coinbase API.

## Build

Requirements:

- Windows 10 19041 or later
- .NET 9 SDK
- WebView2 Runtime (included in Windows 11)

1. Download the speech model once (about 141 MB; it is not kept in git):

   ```powershell
   ./scripts/get-model.ps1
   ```

2. Run the app:

   ```powershell
   dotnet run
   ```

3. Or build a standalone single-file exe:

   ```powershell
   dotnet publish Island.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeAllContentForSelfExtract=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o publish
   ```

User data (settings, clipboard history, shelf files) is stored in `%LOCALAPPDATA%\IslandDesktop`.

## Hotkeys

| Keys | Action |
|---|---|
| Ctrl+Alt+I | Open / close the island |
| Ctrl+Alt+V | Clipboard history |
| Ctrl+Alt+D | Dictation into the focused text field |
| Wheel over the pill | System volume |
| Middle click on the pill | Play / pause |
| Right click on the pill | Paste the last clipboard entry |

