# Global Revert shortcut

The default combination is **Ctrl+Alt+Shift+R**. It works in the user session,
including while DesktopSplitter is hidden in the tray, as long as the application
is running and its dispatcher processes messages. It does not replace an
independent watchdog for a hung process or a shortcut on the secure desktop.

## Why is Win+Esc not the default?

Windows uses Win+Esc to close Magnifier:
https://support.microsoft.com/en-us/accessibility/windows/magnifier/magnifier-keyboard-shortcuts-and-touch-gestures

Microsoft reserves Windows-key combinations for the operating system:
https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerhotkey

Win+Esc can be entered in settings, but it is not guaranteed to be available or
reliable for an application. DesktopSplitter uses RegisterHotKey, without a hook
that intercepts the combination or disables system shortcuts. Even Ctrl+Alt+Shift+R
may already be registered by another application; the registration result is checked.
The existing Compositor shortcut Ctrl+Alt+Shift+S still means suspend/resume.

## Settings

In the main window, under **GLOBAL REVERT SHORTCUT**:

1. Select Enable global Revert shortcut.
2. Enter a combination such as Ctrl+Alt+Shift+R, Ctrl+Alt+Q or Ctrl+Shift+F11.
3. Select Save shortcut and check the message showing the shortcut actually active.

Supported modifiers are Ctrl, Alt, Shift and Win; supported keys are A–Z, 0–9,
Esc and F1–F24. F12 and Ctrl+Shift+Esc are explicitly rejected as reserved.
At least one modifier is required. Clearing the checkbox and saving unregisters
the shortcut. Settings are stored in the existing
`%APPDATA%\DesktopSplitter\settings.json` file as `globalRevertHotkeyEnabled`
and `globalRevertHotkey`; Apply preserves these fields. Older files receive the
default without losing existing settings or the migration backup.

When changing the shortcut, the application registers the new combination before
releasing the previous one. A conflict with another application or a save failure
preserves the previous working shortcut and does not save an inactive combination
as working. Status shows the actual active combination even after an error.
Registration failure at startup is reported; settings are not automatically
replaced with a different shortcut.

## Revert behavior

WM_HOTKEY is received by a separate invisible message window, regardless of main
window visibility. MOD_NOREPEAT limits repetition while keys are held; concurrent
Revert requests are also coalesced.

During Apply, a Revert request sets one pending request and cancels the Apply
token. Revert runs when Apply leaves its operation path, before the UI refresh.
Cancellation is cooperative: an in-progress Windows call may need to finish first.
The request does not start a second concurrent reconfiguration. Existing stable
monitor identity checks still apply; uncertain matching preserves recovery data
and reports an error.

## Tests

The `tests/MonitorIdentity` suite also covers parsing and normalization, invalid
combinations, registration conflicts, rollback after save failure, switching and
rejecting stale messages, disabling and cleanup, settings restoration and the
Revert queue during Apply. Tests use backend fakes and do not change display
configuration or the registry.

Manually verify on Windows 11:

- The default shortcut while another application has focus and DesktopSplitter is in the tray.
- Saving another combination, restarting the application and releasing the shortcut on exit.
- A conflict with a second instance/registration: the previous shortcut stays active.
- Repeated presses do not start concurrent Revert operations.
- Requests during Apply and the countdown, including restoring a specialized monitor.
- Shortcut routing in the actual RDP configuration: the client may direct shortcuts to the local or remote session.
