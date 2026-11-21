# Roadmap

What is planned for Animated Launchers after v2.1.0. There are no dates. Updates ship in roughly this order, each when it is ready and tested. This is a plan, not a promise: the order can change, and items marked "experiment" ship only if a trial shows they work well.

## Next updates

### 1. Safety net (2.1.1)
- A small log file for each helper, and a helper that restarts itself after a crash
- The uninstaller stays on the PC and the project appears in Windows "Installed apps"
- No startup animation while a full-screen game or a presentation is in front
- Test and release scripts in the repository

### 2. Themes to go (2.2)
- Export a theme as one file and import it again, by button or by dropping it on Theme Studio
- Back up and restore the whole theme library
- Duplicate a theme, or drop any image on Theme Studio to make one
- A credit line per theme for the artist or source

### 3. One helper (3.0)
- One helper program and one notification-area icon for all apps, instead of one per app
- Apps described in one table, so adding an app no longer needs a new program
- Existing shortcuts, pins, settings and themes keep working
- Only one startup animation at a time

### 4. Editors (3.1)
- VS Code, Cursor and Kiro
- An apps page in Theme Studio: every known app, whether it was found, and a switch for its wallpaper
- A table of supported apps in the README: tested, reported working, untested

### 5. Sharper looks (3.2)
- Clear zone (experiment): fade the artwork out where the text is, so the picture stays sharp
- Saturation and vignette controls
- Fit modes (fill, fit, center) and crisp scaling for pixel art
- Looks for light-colored apps (experiment)
- Your own presets

### 6. Animation styles (3.3)
- A short style: a title card of about three seconds
- A calm version when Windows asks for reduced motion
- A third speed, and click anywhere to skip
- Play only on the first start after signing in (option)

## After that

- **Chat and notes apps:** Obsidian, and Discord (under review); add your own app with a compatibility check
- **Faster theme making:** crop the avatar from the wallpaper, use the desktop wallpaper, paste an image, add a folder of images, gradient themes without an image
- **Theme Studio comfort:** undo, reset, hold to compare, keyboard shortcuts, search and favorites
- **Status and updates:** per-app status, a report to copy into bug reports, a "check for updates" button that contacts GitHub only when clicked
- **Variety:** theme rotation, favorites, a hotkey to hide the artwork, follow Windows light and dark mode
- **Day and night:** a theme per time of day, night dim, extra dim per app, hide while presenting
- **Every window:** artwork on all windows of an app at once, not only the focused one
- **Animation details:** title fonts, avatar frame shapes, your own boot lines with variables
- **New animation skins**
- **Starter themes** made from generated gradients and public-domain art
- **Reach:** screenshots and a clip in the README, automatic builds, signed programs, winget, translations

## Experiments

Small trials that decide whether an idea is worth building:

- Per-pixel transparency inside the app window (needed for the clear zone)
- Apps built on other technology, for example Spotify, Steam, Telegram and browsers
- Moving wallpapers (GIF or short video), only if they stay light on the processor
- A slight shift of the artwork with the mouse
- The animation covering the app window exactly

## Not planned

- Games, game launchers and anything with anti-cheat
- Changing the supported apps' own files, or running code inside them
- Telemetry or accounts
- Shipping or hosting artwork without the right to share it
- macOS and Linux

## Suggest something

Open an issue and describe what you would like and which app you use it with.
