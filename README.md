# Animated Launchers for Windows

This project builds one animated launcher per supported app — `DevinAnimated.exe`, `AntigravityAnimated.exe`, and `CodexAnimated.exe` — plus `ThemeStudio.exe`, a small configurator for switching wallpaper, avatar, style, and opacity and for swapping between saved themes. Each launcher opens with a 12-second animation, starts its app, and keeps the artwork visible inside the app window, its taskbar hover thumbnail, and its Alt+Tab card.

![A silhouetted girl and birds by the water, with a city skyline under a pink and violet sunset](web/assets/artwork.jpg)

## Install and launch

You'll need 64-bit Windows (x64), at least one of the supported apps (Devin, Antigravity IDE, or Codex), and Microsoft Edge WebView2 Evergreen Runtime for the animation. [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/#download)

1. Build from source (see below) or copy the built executables somewhere you want to keep them. Keeping them together in one folder lets Theme Studio restart each helper for you.
2. Use the matching launcher — or a shortcut to it — for future animated starts. Opening the app from its usual shortcut skips the startup animation.

Choose **Skip** or press **Esc** to go straight to the app. The launcher also skips playback when Windows requests reduced motion. To watch the animation by itself, pass `--preview` to the executable.

When upgrading, you can keep your saved wallpaper. If a previous or custom helper is still active, choose **Exit artwork** in its notification-area menu before running the new launcher.

Optional: to have a helper attach whenever its app opens — even without an animated launch — add `<App>Animated.exe --artwork` to `HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Run`. Each helper waits in the notification area and attaches when its app's window appears.

## Choose a wallpaper

Right-click the app's **artwork** icon in the Windows notification area and select **Choose wallpaper…**. Pick a JPEG, PNG, or BMP. The helper updates the window and its Windows previews immediately. On the next animated start, the scanner frames your picture, traces its outlines, and reveals that same image in color.

The launcher saves a local copy, so you can move or delete the original. Choose **Restore default wallpaper** to bring back the bundled sunset in both the app and the animation.

## Choose an avatar

Select **Choose avatar…** to set the portrait inside the splash's circular radar independently of the wallpaper. It applies on the next animated start. Choose **Restore default avatar** to go back to using the wallpaper image.

## Artwork style and opacity

The **Artwork style** submenu processes the overlay without touching your saved image:

- **Natural** — the wallpaper as-is.
- **Soften for readability** — blurs and darkens the artwork so text stays easy to read on top of it.
- **Dim for readability** — a stronger blur and shade: the artwork becomes a soft dark texture.
- **Nen aura tint** — maps the artwork to a violet duotone.

The **Artwork opacity** submenu picks 10%, 15%, 20%, or 25%. Both settings persist and apply immediately to the running window.

**Skip splash when the app is open** is a toggle: while checked, launching through the shortcut goes straight to the app whenever a window already exists, so the animation only plays on a cold start.

## Theme Studio

`ThemeStudio.exe` edits the same settings files the helpers read, for whichever target app you pick at the top: wallpaper and splash avatar pickers with previews, the style and opacity controls, the skip-splash toggle, and a theme library under `%LOCALAPPDATA%\<App> Animated\Themes`. A theme bundles a wallpaper, optional avatar, style, opacity, and the skip flag. **Apply** copies the theme into place and restarts the app's artwork helper so it lands immediately; **Save current** stores the live setup as a new theme.

## While using the app

The helper draws the artwork at the selected opacity across the app window. Text and controls pick up a slight tint, and clicks pass through to the app. The artwork stays with the window when you drag, resize, maximize, or switch apps.

The helper stays in the notification area when the app closes. It reattaches when the app reopens or replaces its main window, and follows the active window if several are open.

Right-click the app's **artwork** icon in the Windows notification area (check the hidden icons if needed). Choose **Hide artwork** or **Show artwork** to toggle it. **Exit artwork** closes the helper and leaves the app open.

While the artwork is visible, maximize and restore are instant for that window. Hiding the artwork or exiting the helper releases the transition setting. If Windows cannot attach the image inside the app, the helper uses a separate click-through window. In that fallback mode, Windows previews can omit the artwork.

## How it works

The launcher loads the animation from local files in its own WebView2 session, then starts the app: `Devin.exe` or `Antigravity IDE.exe` from the local install (resolved from the standard install folders and the registered protocol), or Codex through its Microsoft Store app identity. A separate helper attaches the artwork through Windows window-management APIs; it verifies the window belongs to the real app — the executable path for Devin and Antigravity, the package family `OpenAI.Codex_2p2nqsd0c76g0` for Codex — before attaching. It runs as your Windows user, leaves the app installations intact, and opens no debugging port.

## Build from source

Clone this repository or download the source archive. Open PowerShell in the repository root on x64 Windows and run the build script.

```powershell
.\source\Build.ps1
```

The script uses the 64-bit .NET Framework C# compiler and the WebView2 SDK DLLs included in the repository. It writes four executables — `DevinAnimated.exe`, `AntigravityAnimated.exe`, `CodexAnimated.exe`, and `ThemeStudio.exe` — to the same folder as `web`. Each launcher is compiled from the same sources with a per-app `APP_*` define selected in `source/AppTarget.cs`. To change the animation itself, edit the files in `web` and rebuild.

## Uninstall

Choose **Exit artwork** in the notification area, delete the executables or extracted folder, and remove any shortcuts and `Run` entries you created. Each app keeps its runtime cache, WebView2 profile, saved wallpaper, and theme library in `%LOCALAPPDATA%\<App> Animated`. Delete those folders if you also want to clear those files.

## Credits

The portrait startup animation is adapted from panding999’s Codex Startup Animation. The Windows adaptation uses the sunset illustration supplied for this release and traces the selected wallpaper at startup. [Upstream animation at the source revision](https://github.com/panding999/codex-startup-animation/tree/c743c2b5eceaa7be9dc511d23a1a77f7c4ce6248)

The launcher uses Microsoft.Web.WebView2 version `1.0.4314.35`. The package includes Microsoft’s [license](notices/LICENSE.txt) and [third-party notices](notices/NOTICE.txt).
