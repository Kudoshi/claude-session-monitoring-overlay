# Instructions for AI agents

Read this before changing anything in this repository. [README.md](README.md) explains what the app does for users; this file explains how to work on it safely.

## What this is

Claude Session Monitoring Overlay: a Windows-only WPF app (.NET 9) that shows Claude desktop and terminal sessions as Needs input, Running, or Done. It is a small always-on-top, click-through window with a tray icon and a Settings window.

It has no server and sends nothing over the network. It reads local files and writes its own files.

## Map of the code

| File | Responsibility |
|---|---|
| `Program.cs` | Entry point. Handles `--hook`, `--install-hook`, `--uninstall-hook`, `--settings`. Enforces one instance. Runs startup migration and hook reconcile. |
| `SessionMonitor.cs` | Merges desktop session files and hook status into Input / Running / Done. Produces transition events for sounds and flashes. |
| `Hooks.cs` | `HookWriter` (the `--hook` mode, runs inside Claude Code, must stay fast and silent), `HookInstaller` (edits `~/.claude/settings.json`), `HookSwitches` (the two user-facing switches). |
| `OverlayWindow.xaml(.cs)` | Click-through window, Ctrl mode, keyboard hook, row rendering, animations, tray icon, opening sessions. |
| `SettingsWindow.xaml(.cs)` | All user settings, the hook cards, and the Start with Windows checkbox. |
| `Settings.cs` | `AppSettings` (persisted JSON), `StartupEntry` (Run key), `Theme` (brushes for every layer). |
| `Sounds.cs` | Synthesized built-in sounds, custom file playback, per-channel debounce. |
| `Native.cs` | Win32 P/Invoke. |
| `install.ps1` / `uninstall.ps1` | Per-user install and removal, including upgrade from 1.0.x. |
| `tools/make-icon.ps1` | Regenerates `app.ico`. |

## Invariants: do not break these

- **Click-through by default.** The window must keep `WS_EX_TRANSPARENT`, `WS_EX_NOACTIVATE` and `WS_EX_TOOLWINDOW` outside Ctrl mode. It must never take focus.
- **Background opacity never goes to 0.** A fully transparent window can't be clicked, so Ctrl mode would be unreachable. `Theme.Apply` clamps it to 0.01.
- **Hook entries are shared.** Both hook switches use one set of entries in `~/.claude/settings.json`. Entries stay while either switch is on. `HookSwitches.IsOn` reads the saved switch, not the file.
- **Hook entries must point at the running exe.** `HookSwitches.Reconcile` runs at startup and re-points them. Any change to the install location must keep this working.
- **Hook mode must stay fast.** `--hook` runs on every tool call. Keep the WPF assemblies out of that path. `Program.Main` returns before `RunGui` loads them.
- **`--uninstall-hook` turns both switches off.** Otherwise they would be re-added on the next launch.
- **The data folder keeps its name.** `%LOCALAPPDATA%\ClaudeOverlay` holds settings, sounds, `state\` and the log. Renaming it would lose user data unless a migration is written.
- **Startup entry name.** The Run value is `ClaudeSessionMonitoringOverlay`. `StartupEntry.MigrateLegacy` moves the old `ClaudeOverlay` value. Keep that migration until it is no longer needed.
- **Never block Claude.** `HookWriter.Run` catches everything and returns 0. Don't add anything that can throw or wait on input.
- **Desktop session files are undocumented.** `%APPDATA%\Claude\claude-code-sessions\…\local_*.json` belongs to the Claude desktop app. Read them only. Read defensively and keep the "keep last good" behaviour in `LoadDesks`.

## Working on the code

- Target: `net9.0-windows`, WPF plus WinForms for the tray icon. Windows only.
- Build: `dotnet build` for a quick check. For a shippable build: `dotnet publish ClaudeSessionMonitoringOverlay.csproj -c Release -r win-x64 -o dist`. This makes a single self-contained exe of about 160 MB.
- Layers: colours and opacity live in `Theme.Apply` as named brushes (`Bg`, `Fg`, `RowBg`, `ExBg`, and so on). Use `SetResourceReference` with those names instead of hard-coded colours, so the Text, Card and Background sliders keep working.
- Text inside XAML and `.cs` uses plain sentence case. Keep user-facing wording short and consistent with the existing text.
- PowerShell scripts are run by Windows PowerShell 5.1. Write them as UTF-8 with a BOM, and avoid syntax that needs PowerShell 7.
- Do not commit `dist/`, `bin/`, `obj/`, or any file under `%APPDATA%` or `%LOCALAPPDATA%`. `.gitignore` covers the build output.

## Testing

Unit tests don't exist yet. Verify by running the app.

- **Hook mode:** pipe a JSON event into the exe, for example `echo {"session_id":"test-1","hook_event_name":"UserPromptSubmit"} | ClaudeSessionMonitoringOverlay.exe --hook`, then read `%LOCALAPPDATA%\ClaudeOverlay\state\test-1.json`. Delete the test file afterwards.
- **Overlay:** launch the exe and check `%LOCALAPPDATA%\ClaudeOverlay\overlay.log` for exceptions. The log is only written on errors.
- **Install and upgrade:** test `install.ps1` from an extracted zip. Confirm that `settings.json` hook commands match the installed path and that the startup value exists.

## Things to ask the user before doing

- **Do not edit `~/.claude/settings.json` by hand, and do not run `uninstall.ps1` or `--uninstall-hook` on the user's own machine,** without asking. They use the hooks themselves, and removing them stops live status.
- Don't change the data folder name, the exe name, or the startup value name without confirming. Those are visible to existing users.
- Don't publish a release, make the repository public or private, or change its licence without an explicit instruction.

## Known issues

- **Terminal session titles** come from the first line of the prompt. Sessions that start with a `<system-reminder>` block show that tag as the title. The fix is to skip lines that start with `<` in `HookWriter.Snippet`.
- The overlay can't open terminal sessions in the desktop app. Clicking a done terminal session only clears it.
- The exe isn't code-signed, so Windows SmartScreen warns on first run.

## Releases

- Version lives in `ClaudeSessionMonitoringOverlay.csproj` (`<Version>`).
- A release is a zip containing `ClaudeSessionMonitoringOverlay.exe`, `install.ps1`, `uninstall.ps1`, `README.md` and `LICENSE`, attached to a GitHub release tagged `vX.Y.Z`.
- The README's download link points at `releases/latest`, so mark the new release as latest.

## Commits

- Keep commit messages short in the subject line, with a body that explains why.
- Add the Co-Authored-By trailer that the current session specifies.