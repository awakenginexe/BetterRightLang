# BetterRightLang

BetterRightLang corrects text typed on the wrong US English or Thai Kedmanee keyboard layout. It runs as a Windows tray app. The settings window uses the installed Microsoft Edge WebView2 Runtime and can be closed while corrections continue.

## Use

Download and run the Windows x64 **Setup.exe**. Setup adds BetterRightLang to the Start menu and installs the .NET 10 Desktop Runtime and Evergreen WebView2 Runtime if needed. For portable use, install those runtimes, extract the full ZIP, and open **BetterRightLang.exe**. [GitHub Releases](https://github.com/awakenginexe/BetterRightLang/releases)

Quit any running copy of RightLang or BetterRightLang before opening a different copy. Enable the US English and Thai Kedmanee keyboard layouts in Windows.

| Mode | Tray icon | Behavior |
| --- | --- | --- |
| Automatic | Green | Compares the typed word with both offline dictionaries and switches the active keyboard layout when it applies a correction. |
| Manual | Yellow | Leaves typing alone until you use a conversion shortcut. |
| Paused | Grey | Suspends correction and releases the shortcuts. Resume from the tray or settings. |

Windows shows a tray notification when you change modes or when BetterRightLang applies a correction. Correction notifications show the direction, such as **English → Thai**. The words themselves appear in the session's **History** page. Windows notification settings control banner visibility.

| Shortcut | Action |
| --- | --- |
| Shift + Backspace | Convert the current word. |
| Shift + Caps Lock | Convert the selected text, preserving whitespace. |
| Ctrl + Caps Lock | Toggle Automatic and Manual while keeping your current Caps Lock state. |

Holding either Caps Lock shortcut triggers it once. Pressing Caps Lock on its own works normally.

Try typing **l;ylfurujg0wf** on the English layout. The same keys on the Thai layout spell **สวัสดีพี่เจได**. The settings practice field lets you try the selected mode directly. Double-click the tray icon or run the executable again to open settings. **Start with Windows** launches directly in the tray.

Auto keeps a word when its current spelling can still grow into a known word. It looks for evidence in the other layout starting at the third key and can recognize several Thai words typed together.

Open **History** in the settings window to see the last 100 corrected words from this session, newest first. Each entry shows the original spelling, replacement and whether a shortcut or Automatic mode made the change. Choose **Whitelist original** to keep that spelling in Automatic mode. You can also add or remove words directly on this page. Saved words are protected as you type, including English case variants. Manual shortcuts can still convert them.

BetterRightLang checks the active field and word before editing. If more keys arrive during a browser correction, it holds them in memory until the caret is back in place and replays them in order. Standard Windows edit controls support current-word correction in long documents. In other editors, a current-word correction requires verifiable field boundaries and at most 4,096 UTF-16 code units of surrounding text. Manual selection supports up to 4,096 UTF-16 code units. Protected fields are skipped. The detector uses local dictionaries. Correction history lives in memory while the tray app runs. Closing settings keeps the session going. **Quit BetterRightLang** in the tray clears it. The whitelist is stored in **%LOCALAPPDATA%\BetterRightLang\settings.json**.

## Build

Use the .NET 10 SDK on 64-bit Windows. The framework-dependent build requires the .NET 10 Desktop Runtime and the Evergreen WebView2 Runtime.

~~~powershell
dotnet run --project Desktop
dotnet run --project tests/Core -c Release
dotnet run --project tests/Input -c Release
dotnet run --project tests/Desktop -c Release
dotnet publish Desktop -c Release -r win-x64 --self-contained false -o dist/BetterRightLang
~~~

Keep the published files together when moving the app.

To check the published app in Brave's Google search box, run the command below with the app in Automatic or Manual mode. It opens a disposable Brave profile, briefly takes keyboard focus and checks the field and its active keyboard layout. It types the example without submitting a search.

~~~powershell
dotnet run --project tests/Live -c Release -- --run --target brave-google --app dist/BetterRightLang/BetterRightLang.exe
~~~

Use `--target brave-local` with `--case overlap`, `overlap-early`, `overlap-burst` or `overlap-auto` to hold a disposable Brave editor before or during a correction and check that typing through it preserves the word, surrounding text and caret.

## Release

In a release build, open **Settings → Check for updates**. When a new version is available, choose **Update and restart**. The app keeps your saved settings and whitelist. The updater reads GitHub Releases without credentials and needs the repository to be public.

Push a tag in `vMAJOR.MINOR.PATCH` format. GitHub Actions runs the Core, Input and Desktop tests, sets the app version from the tag, and publishes the Windows x64 installer, portable ZIP, and Velopack update feed and package.

## Sources and licenses

The new application code is [MIT-licensed](LICENSE). English words come from SCOWL and Thai words from LibThai. [Dictionary sources, modifications and licenses](Data/README.md) accompany the application. The distribution also includes the [Velopack license](Desktop/Notices/Velopack-LICENSE.txt) and Microsoft's WebView2 notices.

RightLang 1.1.4312 supplied the behavioral reference for the two modes, keyboard shortcuts and compound-word handling. BetterRightLang has its own detector and word lists. [Original RightLang guide](https://www.rightlang.com/index.html)
