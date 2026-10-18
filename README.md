# Bluey for Windows

A Windows version of [bluey-by-riley](https://github.com/rbrown101010/bluey-by-riley) (Unlicense).
A blueberry character lives on your iPhone; it looks where your mouse is, and when you hold the screen
and talk, it looks at your PC screen, points at things with its own cursor, and can click, type and scroll.

- **Windows app** (`Windows/Bluey`): tray icon, overlay cursor + speech bubble, screen reading
  (Windows OCR + UI Automation), mouse/keyboard control, talks to Google Gemini.
- **iPhone app** (`iOS`, `Shared`): the original face, with a new push-to-talk voice that sends your
  question to the PC.
- It uses **Gemini's free tier**: no credit card. Get a key at https://aistudio.google.com/apikey

## How to talk to him

Double-tap the face to wake him. **Hold the screen, speak, let go.** His answer pops up as a bubble on your PC.

## Build it (no Mac or Visual Studio needed)

1. Make a **public** GitHub repo and upload everything in this folder (public repos get free build minutes).
2. Open the **Actions** tab → **Build Bluey** → **Run workflow**.
3. When it finishes (about 5–10 minutes), download two artifacts from the run page:
   `Bluey-Windows-exe` (Bluey.exe) and `Bluey-iPhone-ipa` (Bluey.ipa).

If a build fails, open the failed step, copy the red error lines, and send them to whoever is helping you.
This code was written without being compiled, so the first run may need a fix or two.

## Set up

**PC:** run `Bluey.exe`. Windows will say "protected your PC" (the app isn't paid-signed): click
*More info → Run anyway*. Allow it through the firewall on **private** networks when asked.
Right-click the blue tray icon → **Set Gemini key…** and paste your key.

**iPhone:** install `Bluey.ipa` with [Sideloadly](https://sideloadly.io) (free Apple ID; the app must be
re-installed every 7 days). Turn on Developer Mode if iOS asks, then trust the profile in
Settings → General → VPN & Device Management. Allow Local Network and Microphone.
Phone and PC must be on the same Wi-Fi.

## Hotkeys (PC)

| Keys | What |
|---|---|
| Ctrl+Alt+Space | Wake / sleep the phone |
| Ctrl+Alt+P | Point at the mouse |
| Ctrl+Alt+S | Stop actions (refuses actions for 6 seconds) |
| Ctrl+Alt+H | Show / hide the overlay |

## Notes

- **Privacy:** on Gemini's free tier, Google may use prompts and screenshots to improve its products.
  Don't use Bluey with banking, private messages or work documents on screen. Tray menu →
  *Send screenshots to Gemini* can be turned off (he then only gets the text he can read).
- **Safety:** he never types into password fields, refuses lock/security shortcuts, and is told to ask
  before sending, deleting or buying. Turn off *Let him use the computer* in the tray menu to make him look and point only.
- **Free limits** change. If you see "used my free requests", wait a minute. If Google renames the model,
  use tray → *Choose Gemini model…* (default `gemini-flash-latest`).
- Settings and the encrypted key live in `%AppData%\Bluey`; errors go to `%AppData%\Bluey\log.txt`.
- Not included vs. the original: web research cards and all-day meeting transcription (they need paid APIs).
- If the phone never connects: check both are on the same Wi-Fi (not a "guest" network that isolates devices)
  and that the firewall prompt was allowed on Private networks.
