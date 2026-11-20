# Codex Animated for Windows

Codex Animated scans your wallpaper, assembles its outlines, and reveals the full image during a 12-second startup animation. When Codex opens, the picture stays visible in the app window, its taskbar hover thumbnail, and its Alt+Tab card.

![Mint northern lights over a dark alpine lake](web/assets/artwork.jpg)

## Install and launch

You’ll need 64-bit Windows (x64), the official Codex desktop app installed for your Windows account, and Microsoft Edge WebView2 Evergreen Runtime for the animation. [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/#download)

1. Download **CodexAnimated.exe** under **Assets** on the release page. [Latest release](https://github.com/Synaap/Codex-Animated/releases/latest)
2. Save it somewhere you want to keep it and double-click it. The standalone EXE includes the animation, artwork, and WebView2 SDK files. You can also choose the ZIP, extract it, and run `CodexAnimated.exe` from the extracted folder.
3. Use the same launcher or create a shortcut to it for future animated starts. If you already have the portrait animation installed, the launcher uses it automatically. On a second PC, you can put your own complete portrait-theme `web` folder beside the EXE to play that same sequence.

Choose **Skip** or press **Esc** to go straight to Codex. The launcher also skips playback when Windows requests reduced motion. To watch the animation by itself, pass `--preview` to the executable.

When upgrading from the official v1.0 or v1.1 release, the new launcher takes over from its artwork helper. If you run a different custom build, choose **Exit artwork** in its notification-area menu before launching this version.

## Choose a wallpaper

Right-click **Codex Animated artwork** in the Windows notification area and select **Choose wallpaper…**. Pick a JPEG, PNG, or BMP. The image appears immediately in the Codex window and its Windows previews. The next startup animation uses it too.

The launcher saves a local copy, so you can move or delete the original. Choose **Restore default wallpaper** to return to your theme’s bundled image. A fresh installation starts with the aurora.

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

The script uses the 64-bit .NET Framework C# compiler and the WebView2 SDK DLLs included in the repository. It writes `CodexAnimated.exe` to the same folder as `web`.

## Uninstall

Choose **Exit artwork** in the notification area, delete the executable or extracted folder, and remove any shortcut you created. The runtime cache, WebView2 profile and your saved wallpaper are in `%LOCALAPPDATA%\Codex Animated`. Delete that folder if you also want to clear those files.

## Credits

The Windows launcher was inspired by panding999’s Codex Startup Animation. This release includes an original aurora splash, an AI-generated landscape, and a custom Windows icon. Codex Animated is an independent project. [Project inspiration](https://github.com/panding999/codex-startup-animation)

The launcher uses Microsoft.Web.WebView2 version `1.0.4314.35`. The package includes Microsoft’s [license](notices/LICENSE.txt) and [third-party notices](notices/NOTICE.txt).
