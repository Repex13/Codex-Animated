# Codex Animated for Windows

Codex Animated opens with a 12-second animation. A circular scanner frames the sunset portrait, the image breaks into fragments, and the pieces draw the skyline before the full-color scene appears. When Codex opens, the sunset stays visible in the app window, its taskbar hover thumbnail, and its Alt+Tab card.

![A silhouetted girl and birds by the water, with a city skyline under a pink and violet sunset](web/assets/artwork.jpg)

## Install and launch

You’ll need 64-bit Windows (x64), the official Codex desktop app installed for your Windows account, and Microsoft Edge WebView2 Evergreen Runtime for the animation. [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/#download)

1. Download **CodexAnimated.exe** under **Assets** on the release page. [Latest release](https://github.com/Synaap/Codex-Animated/releases/latest)
2. Save it somewhere you want to keep it and double-click it. The standalone EXE includes the animation, artwork, and WebView2 SDK files. You can also choose the ZIP, extract it, and run `CodexAnimated.exe` from the extracted folder.
3. Use this launcher or a shortcut to it for future animated starts. It includes the sunset portrait sequence for first-time users and uses its embedded theme, even beside files from an older release. Opening Codex from its usual shortcut skips the startup animation.

Choose **Skip** or press **Esc** to go straight to Codex. The launcher also skips playback when Windows requests reduced motion. To watch the animation by itself, pass `--preview` to the executable.

When upgrading, you can keep your saved wallpaper. If a previous or custom helper is still active, choose **Exit artwork** in its notification-area menu before running the new launcher.

## Choose a wallpaper

Right-click **Codex Animated artwork** in the Windows notification area and select **Choose wallpaper…**. Pick a JPEG, PNG, or BMP. The helper updates the Codex window and its Windows previews immediately. On the next animated start, the scanner frames your picture, traces its outlines, and reveals that same image in color.

The launcher saves a local copy, so you can move or delete the original. Choose **Restore default wallpaper** to bring back the bundled sunset in both the app and the animation.

## While using Codex

The helper draws the artwork at 15% opacity across the Codex window. Text and controls pick up a slight tint, and clicks pass through to the app. The artwork stays with Codex when you drag, resize, maximize, or switch apps.

The helper stays in the notification area when Codex closes. It reattaches when Codex reopens or replaces its main window, and follows the active Codex window if several are open.

Right-click **Codex Animated artwork** in the Windows notification area (check the hidden icons if needed). Choose **Hide artwork** or **Show artwork** to toggle it. **Exit artwork** closes the helper and leaves Codex open.

While the artwork is visible, maximize and restore are instant for that Codex window. Hiding the artwork or exiting the helper releases the transition setting. If Windows cannot attach the image inside Codex, the helper uses a separate click-through window. In that fallback mode, Windows previews can omit the artwork.

## How it works

The launcher loads the animation from local files in its own WebView2 session, then asks Windows to open Codex using its registered app identity. A separate helper attaches the artwork through Windows window-management APIs. It runs as your Windows user, leaves the Codex installation intact, and opens no debugging port.

## Build from source

Clone this repository or download the source archive from the release. Open PowerShell in the repository root on x64 Windows and run the build script.

```powershell
.\source\Build.ps1
```

The script uses the 64-bit .NET Framework C# compiler and the WebView2 SDK DLLs included in the repository. It writes `CodexAnimated.exe` to the same folder as `web`. To change the animation itself, edit the files in `web` and rebuild the executable to embed your changes.

## Uninstall

Choose **Exit artwork** in the notification area, delete the executable or extracted folder, and remove any shortcut you created. The runtime cache, WebView2 profile and your saved wallpaper are in `%LOCALAPPDATA%\Codex Animated`. Delete that folder if you also want to clear those files.

## Credits

The portrait startup animation is adapted from panding999’s Codex Startup Animation. The Windows adaptation uses the sunset illustration supplied for this release and traces the selected wallpaper at startup. [Upstream animation at the source revision](https://github.com/panding999/codex-startup-animation/tree/c743c2b5eceaa7be9dc511d23a1a77f7c4ce6248)

The launcher uses Microsoft.Web.WebView2 version `1.0.4314.35`. The package includes Microsoft’s [license](notices/LICENSE.txt) and [third-party notices](notices/NOTICE.txt).
