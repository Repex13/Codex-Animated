# Codex Animated for Windows

An aurora sweeps over an alpine lake during a nine-second startup animation. When Codex opens, the same landscape appears at low opacity over the window, in its taskbar hover thumbnail, and on its Alt+Tab card.

![Mint northern lights over a dark alpine lake](web/assets/artwork.jpg)

## Install and launch

You’ll need 64-bit Windows (x64), the official Codex desktop app installed for your Windows account, and Microsoft Edge WebView2 Evergreen Runtime for the animation. [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/#download)

1. Download the Windows x64 ZIP under **Assets** on the [latest release](https://github.com/Synaap/Codex-Animated/releases/latest).
2. Use **Extract All** and put the folder somewhere you want to keep it. Keep the DLLs and the `web` folder beside `CodexAnimated.exe`.
3. Double-click `CodexAnimated.exe`. For easier access, create a shortcut to that file. Use this launcher whenever you want the animation and artwork. Opening Codex from its usual shortcut skips the animation.

Choose **Skip** or press **Esc** to go straight to Codex. The launcher also skips playback when Windows requests reduced motion. To watch just the animation, run `.\CodexAnimated.exe --preview` from PowerShell in the extracted folder.

If you are upgrading from an earlier release, choose **Exit artwork** from its notification-area icon before running the new launcher.

## Choose a wallpaper

Right-click **Codex Animated artwork** in the Windows notification area and select **Choose wallpaper…**. Pick a JPEG, PNG, or BMP. The image appears immediately in the Codex window and its Windows previews. The next startup animation uses it too.

The launcher saves a local copy, so you can move or delete the original. Choose **Restore default wallpaper** from the same menu to return to the aurora.

## While using Codex

The helper draws the artwork at 15% opacity across the Codex window. Text and controls pick up a slight tint, and clicks pass through to the app. The artwork stays with Codex when you drag, resize, maximize, or switch apps.

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

Choose **Exit artwork** in the notification area, delete the extracted folder, and remove any shortcut you created. The WebView2 profile and your saved wallpaper are in `%LOCALAPPDATA%\Codex Animated`. Delete that folder if you also want to clear those personal settings.

## Credits

The Windows launcher was inspired by panding999’s Codex Startup Animation. This release includes an original aurora splash, an AI-generated landscape, and a custom Windows icon. Codex Animated is an independent project. [Project inspiration](https://github.com/panding999/codex-startup-animation)

The launcher uses Microsoft.Web.WebView2 version `1.0.4314.35`. The package includes Microsoft’s [license](notices/LICENSE.txt) and [third-party notices](notices/NOTICE.txt).
