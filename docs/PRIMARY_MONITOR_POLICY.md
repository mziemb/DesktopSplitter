# Primary monitor preferences

This implementation starts at dev/codex-desktopsplitter 3fb677e. It does not reuse
or merge the abandoned primary-monitor-preservation branch.

## Roles and persistence

`%APPDATA%\DesktopSplitter\primary-preferences.json` stores confirmed logical
monitor IDs, independently of UI settings. Writes use a temporary file and atomic
replacement; the first replaced version is retained as `.bak`. Corruption or an
unknown schema stops automatic changes instead of replacing recovery data.

- `PreSplitId`: the primary before the first Apply of this split session. Saved
  before driver/topology changes, and carried through repeated Apply.
- `DesiredId`: the primary wanted during the split. Initially segment 1 if the
  primary physical monitor is split; otherwise the original physical primary.
- `ManualOverride`: a subsequent explicit/observed primary preference. Does not
  replace PreSplitId. Valid preferences survive reapply; a removed virtual segment
  falls back to the session default. A source being hidden cannot remain primary.
- `RevertPending`: the original primary could not yet be restored. An absent
  monitor remains a pending ID, never a replacement DISPLAYn or arbitrary monitor.

A deliberate Revert restores PreSplitId before and after virtual device teardown.
If it is absent, virtual monitors can still be removed, and restoration is retried
when the same identity returns. Owned hidden-display recovery is retained while
its source is offline and retried after reconnect; failed/unconfirmed recovery is
not cleared. Once restoration is confirmed, ordinary physical-primary preference
tracking continues. A normal Exit tears the split down but retains the split's
manual preference for the existing startup-reapply behavior. A deliberate Revert
ends that split session. A declined Apply restores the previous split preference
as well as its geometry.

The app must remain running (including in the tray) for live reconciliation.
After a full Exit, pending restoration resumes the next time the app runs.

## Events, manual choice and safety

The reconciler runs off the UI thread, wakes on display/lifecycle notifications
and also polls. It uses the same display-operation gate as Apply/Revert. Stop does
not leave a second worker competing with a delayed first worker. Switches resolve
stable IDs to fresh Windows addresses and verify the resulting primary. The virtual
group is checked against existing screen rectangles: when the default position
right of the split source overlaps a physical screen (as with a portrait screen
left of the primary), only the virtual group moves beyond the existing desktop.
Physical relative placement is preserved; primary rebasing is still one batch.

Inputs include WM_DISPLAYCHANGE, WM_DEVICECHANGE, console display power state,
Windows suspend/resume, session transitions and compositor secure-desktop events.
Unavailable/ambiguous identities and incomplete enumeration defer changes without
rewriting intent. Monitor sets and dimensions must settle for three seconds after
lifecycle changes. Own writes are suppressed as learning signals.

Windows does not report whether a primary change was a user's action or an OS
fallback. To avoid learning a fallback, automatic manual-choice adoption requires
a recent interaction with foreground Windows Settings (including its hosted
ApplicationFrameHost window) plus a changed primary on
a settled topology. A Settings event during sleep/reconnect settling is ambiguous
and is not automatically learned. For another display utility, rapid changes
during wake, or an unobserved event, use **Remember current Windows primary**.
This records the current confirmed monitor without changing the Revert target.
Pending explicit Revert restoration must finish before a new idle preference is
recorded. This limitation is intentional; there is no claim of infallible inference
of user intent from Windows' generic topology notification.

Secure desktop can temporarily use an available physical display. That temporary
fallback does not change DesiredId; resume restores the saved physical or virtual
choice, including a manually selected segment other than segment 1. Virtual
primary restoration waits for the compositor. Existing active legacy settings
migrate confirmed IDs and the currently observed primary; historical user intent
cannot be reconstructed from old DISPLAYn fields.

## Windows 11 acceptance

Use one app instance and keep status output and primary-preferences.json for each
case. Confirm monitor identities first. Tests below require real hardware and
are not performed by CI.

1. Primary P, split a secondary Samsung S into two/three segments: P stays primary.
2. Split P itself: segment 1 becomes primary; Revert returns P.
3. Set another physical Q as primary in Windows Settings during the split. Allow
   the desktop to settle; sleep/wake and screen off/on must retain Q. Revert: P.
4. Repeat with virtual segment 2. Lock/UAC must not replace its saved preference;
   after resume it returns; Revert still restores P.
5. Disconnect the desired primary; let Windows use a fallback. Reconnect with
   changed DISPLAY numbering: restore the saved logical monitor.
6. Disconnect original P, then Revert. Virtual monitors disappear, P remains a
   pending target, and reconnect restores P. Repeat with an owned hidden source;
   manual specialization recovery may still be needed if Windows refuses it.
7. Reapply a different layout after a manual primary change. Original P remains
   the Revert target; preferences for existing segments remain intact.
8. Decline the Apply countdown: previous geometry and primary preference return.
9. Exit/restart the app and test the existing startup-reapply behavior. Explicit
   Revert must not be treated as automatic exit teardown.
10. Repeat with transient/empty enumeration, clone mode or ambiguous identity:
    no arbitrary primary or identity reassignment; intent stays on disk.
11. Change primary using another utility, press Remember current Windows primary,
    then power-cycle displays. That explicit choice survives; Revert restores P.
12. After Revert, disconnect/reconnect the physical primary again. It is restored;
    a later deliberate Windows Settings choice on a settled desktop is respected.

Automated tests exercise the actual policy and file store with synthetic monitor
observations and temporary files. They do not mutate display configuration. The
full Windows build validates integration, not physical sleep/hot-plug behavior.
