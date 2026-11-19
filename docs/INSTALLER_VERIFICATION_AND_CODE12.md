# Installer verification, recovery and Code 12

## Status and confirmed findings

This change is based on dev/codex-desktopsplitter commit
6b39359ffdda7dc465717ee447ee386a80157992, including the stable monitor identity
and global Revert changes already merged into that base. It does not merge other
branches, change the driver INF/protocol, or install a driver during CI.

**The cause of the reported machine's Code 12 has not been established.** No
Windows 11 installation log, device-instance snapshot or setupapi.dev.log from
that machine was supplied. Installation on real hardware was not reproduced.

The number 12 has at least two distinct meanings in this repository:

- PnP problem 12 is CM_PROB_NORMAL_CONFLICT. It is evidence of a PnP problem,
  not proof of which device, package or resource caused it.
- Installer exit 12 is ExitFileInstallFailed (copy/verification of product files).
  A pnputil exit, Win32 error, HRESULT or CONFIGRET is a separate namespace again.

Official references:
https://learn.microsoft.com/en-us/windows-hardware/drivers/install/cm-prob-normal-conflict
https://learn.microsoft.com/en-us/windows-hardware/drivers/install/retrieving-the-status-and-problem-code-for-a-device-instance

Confirmed defects in the previous code:

1. InstallEngine.cpp / WaitForHealthyDeviceNode accepted `!HasStatus` as healthy,
   and did not require DN_STARTED, a bound-package check or GET_STATUS.
2. RunInstallSequence accepted CreateRootDeviceNode failure when NodeCreated was
   already true. A registered node is not an operational driver.
3. CreateShortcuts, WriteUninstallEntry and WriteProductFlags return values were
   ignored by the engine; some implementations also ignored individual writes.
4. Old packages were pruned before runtime verification, choosing a retained INF
   from enumeration order when no new package was found.
5. Device enumeration included non-present root instances; errors could be
   interpreted as absence, and a ghost node could prevent correct creation.
6. StopRunningProcesses force-terminated processes by basename.
7. External-process waits could be infinite or ignored, and pending restart was
   represented as a detail line while the exit code still indicated success.
8. Wizard failure text claimed nothing else had changed; quiet mode implicitly
   trusted certificates. The shell installer declared success after pnputil and
   devcreate without verifying an operational device.

These are code findings; none proves the specific root cause of the user's PnP
conflict. Duplicate instances, failed/stale binding, missing framework components
and resource/startup failures remain diagnostic hypotheses.

## Package and driver assessment

DesktopSplitter is a UMDF/IddCx driver: its owned binary is
DesktopSplitterVdd.dll, not a private SYS file. The INF references Windows'
WUDFRD and IndirectKmd system components. The signed package contains INF, DLL
and CAT. The protocol is still DESKSPLIT_PROTOCOL_VERSION=1.

The existing UmdfExtensions=IddCx0102 spelling is also used by Microsoft's IDD
sample and is not evidence by itself of an erroneous 1.4 minimum requirement:
https://github.com/microsoft/Windows-driver-samples/blob/main/video/IndirectDisplay/IddSampleDriver/IddSampleDriver.inf
No speculative INF, signing-policy or security-setting change was made.

## Mandatory production pipeline

InstallPolicy.h is the actual orchestrator used by the Windows engine and by
mock tests. Its default outcome is Failed. Ready is reachable only after every
mandatory stage, including final verification:

1. Effective administrator rights and durable log availability.
2. Required embedded driver and product resources, followed by extraction.
3. Existing registration, all/present root instances, recovery ownership, active
   split and process checks. Unknown CM state is a failure. Duplicates and ghost
   instances stop instead of triggering automatic deletion.
4. Authenticode verification. Only an untrusted-root/chain case may prompt for
   explicit publisher consent. Other signature failures are fatal. Quiet mode
   does not grant certificate-store consent. Trust is verified again afterward.
5. SetupCopyOEMInf staging, exact published OEM name and byte comparison of the
   staged INF. Existing driver packages are retained for manual recovery.
6. Unique root-node creation/update; every failure is checked even after node
   registration. The exact requested INF is bound to the checked hardware ID.
7. Present unique device, readable Configuration Manager status, no PnP problem,
   DN_STARTED, correct bound INF, driver DLL payload bytes and bounded GET_STATUS
   with exact response size, compatible protocol and valid monitor count.
8. Verified backup of known product files before replacement.
9. Product files copied; every mandatory installed resource is compared byte for
   byte, including the installer copy. A mere filename is insufficient.
10. Requested shortcuts and every mandatory registry write checked.
11. Final product/version/file and runtime verification.

An active split or recorded hidden-monitor recovery ownership stops an upgrade:
use the owning app's Revert and Exit first. The installer does not manipulate
monitor topology or guess a physical target. Graceful process closing is limited
to matching executable paths in the current session. It never force-kills a
process solely because its basename matches. A tray app that does not exit on
WM_CLOSE causes an actionable stop rather than forced termination.

PnP Code 12 and other problems are fatal even if another operation requests a
restart. Unknown status is never healthy. CM_PROB_NEED_RESTART (decimal 14) and
explicit native reboot flags can produce a distinct pending result. Protocol
mismatch remains a failure. A driver that replays a persisted active split is
not certified as a completed installation.

## Results and logs

- Exit 0: all mandatory checks passed; the wizard can offer to launch the app.
- Exit 3010: files/registration stages completed, but a restart is required.
  Readiness is NOT certified, and the wizard does not launch the app. Reboot and
  run the installed DesktopSplitterSetup.exe /verify-installed before Apply.
- Other nonzero: installation failed/incomplete. Exit 12 is a product-file
  failure, 14 a registration failure, 15 a persistent-log failure. Earlier exit
  meanings remain documented in /?.

A failing mandatory stage cannot be converted into Ready. Console and GUI use
this same engine. The finish title, subtitle and body reflect failure or pending
restart, and View log opens the retained full log.

Logs are written under `%ProgramData%\DesktopSplitter\Logs`, with UTC timestamps,
installer/Windows versions, previous installation, stage results and namespaces,
root instance IDs, CONFIGRET, DN flags, PnP code, optional underlying NTSTATUS
(DEVPKEY_Device_ProblemStatus), bound/staged INF, external-child PID/exit/wait
results, restart and rollback status, and the final result. setupapi.dev.log is
referenced but not modified. No secrets or process command lines are collected.

Failure to create/write the durable log prevents a success result. Without
administrator rights, setup stops before installation and may be unable to create
a ProgramData log; the explicit privilege failure is then shown directly.

## Failure and recovery

Old driver packages are not pruned. Known product files are backed up under
`%ProgramData%\DesktopSplitter\Recovery\files-...`. On a copy, verification or
later failure, the engine attempts to restore exactly those old bytes and remove
only newly introduced known product files. Restore failures retain their codes
and the backup path. User settings and unrelated files are not removed.

This is **file rollback, not transactional PnP rollback**. The engine does not
blindly remove/rebind a device or delete packages to reconstruct an assumed old
state. If driver installation/binding or product registration partly succeeded,
the result explicitly says that a partial installation may remain. Restored files
do not imply that the old driver binding or all old registry values were restored.
Consented certificate-store changes are also not silently undone. Retain logs,
backups and old packages for a controlled repair.

The shell install/uninstall scripts delegate to the checked engine instead of
bypassing it through pnputil/devcreate. Arbitrary -InfPath installation is rejected
with a request to build an installer embedding that package. -VerifyOnly runs
post-reboot verification. -SkipTestSigningCheck is obsolete and never changes
security policy. -InstallDir uses console mode; an untrusted publisher then
requires a separate interactive install and explicit consent. Uninstall likewise
stops on active recovery/split state rather than forcing display teardown.

## Controlled Windows 11 update procedure

1. Keep the previous working installer and back up user settings separately.
2. In the existing app, Revert; confirm all physical screens are restored; choose
   Exit in its tray menu. Do not merely close a window configured to minimize.
3. Run the new complete installer as administrator, using the existing install
   directory. Review any explicit publisher-consent request; do not change
   Secure Boot, test-signing or security policy as an installer workaround.
4. If exit 3010 is reported, restart. Then, before Apply, run from an elevated
   prompt: `"C:\Program Files\DesktopSplitter\DesktopSplitterSetup.exe" /verify-installed`.
   This checks the installed product without staging/installing a driver.
5. If any stage fails, stop. Retain the ProgramData log and recovery directory.
   Do not delete root devices or driver-store packages based on a filename alone.
6. Collect read-only evidence with installer/collect-diagnostics.ps1. Inspect the
   bundle before sharing it together with the matching installation log.

## Tests and limits

Portable tests execute the production stage policy with mock native operations.
They cover clean installation, update, running app/compositor, active split,
administrator failure, INF and node failures, Code 12/other PnP problems, absent
or malformed replies, protocol/binding mismatch, timeout, cancellation, file
rollback failure, restart, unknown status and all-stage failure propagation.

CI compiles and links the Windows installer without executing it, parses the
PowerShell scripts, runs mock tests, and builds the full embedded package.
Existing /verify and /selftest are package/UI self-checks, not installation tests.
No driver is installed and no PnP or display configuration is changed by tests.

Limits requiring real Windows 11 acceptance:

- Native SetupAPI operations may block inside Windows; they are not safely
  force-cancelled. pnputil has a bounded wait, but timed-out PnP server work may
  still be incomplete. Do not assume a timeout reversed changes.
- GET_STATUS has bounded overlapped waits. If a broken driver does not acknowledge
  cancellation, its buffers/handles remain alive until setup exits to avoid
  freeing memory still referenced by an in-flight request.
- The catalog package and on-disk DLL/protocol are verified, but the existing
  protocol does not expose an in-memory product build ID beyond version 1.
- Recovery settings are inspected for the account running setup. When elevating
  as another account, verify the original user's recovery state manually.
- An unidentifiable/duplicate device state requires manual review. The tool does
  not guess which instance should be deleted.
- The actual reported Code 12 remains unconfirmed until machine evidence is
  available, including whether the observed 12 was PnP, installer or child exit.

## Follow-up: ReadOnly files and removal pending restart

The supplied Windows log confirmed successful PnP/GET_STATUS verification followed
by Win32 5 during file copying and failure to restore DesktopSplitterSetup.exe.
The user confirmed that this file had ReadOnly set and that reinstalling after a
restart succeeds. The uninstall log and screenshot confirmed exit 3010 with an
incorrect installation-specific instruction. These observations do not establish
which process, if any, held a file open during the initial failure.

- Known product files have only ReadOnly cleared before replacement/deletion;
  other attributes and ACLs are preserved. Attributes copied from the setup source
  or backup do not leave the installed setup ReadOnly again. Directories and
  reparse points are rejected as product files.
- Copy failures name the target file, and setup replacement also logs its source.
- Rollback compares old and current bytes first. Unchanged files, including a
  running setup, are not needlessly overwritten. Changed locked files still fail
  explicitly; no process is forcibly killed to complete rollback.
- Uninstall logs the exact running setup scheduled for deletion after restart.
  Empty install-directory cleanup is also scheduled; Windows never recursively
  deletes unrelated files. The final result distinguishes removal complete from
  removal pending restart. GUI and console instruct restart before reinstalling,
  without telling users to verify an uninstalled product. Finish strings use ASCII
  punctuation to avoid the mojibake observed in the screenshot.
- Before staging/binding a driver, installation reads (never edits) Windows'
  PendingFileRenameOperations and checks exact known product paths for scheduled
  deletion. If matched, exit 16 blocks installation and asks for restart. This
  avoids reinstalling a file that Windows would delete at the next boot. Unrelated
  system reboot requests do not block this product. Unknown/unreadable pending
  operations stop installation conservatively.

Regression validation includes portable production-policy/message/pending-pair
checks and Windows temporary-file tests for ReadOnly replacement/removal, backup
attributes, unchanged locked files, changed locked failures, and preserving user
files. These tests do not invoke installation, PnP, delayed deletion or registry
writes. Acceptance on Windows 11: uninstall from the installed setup, confirm 3010
and the removal-specific message, attempt reinstall before restart (exit 16 before
PnP), restart, then reinstall and run final verification. Keep logs for each step.

## Follow-up: tray shutdown and setup self-lock

The November 19 log stops after five seconds with ERROR_BUSY (170). The previous
WM_CLOSE request was handled by MainWindow.OnClosing as minimize-to-tray, so it
could not implement installer shutdown. This was an application lifetime issue,
not evidence of a PnP fault.

The app now exposes an installer-specific registered window message, including
when launched hidden at startup. It refuses the request during Apply/Revert;
otherwise it uses normal App.ExitApplication teardown instead of hiding. Setup
validates every candidate's executable path, session and creation time before
requesting shutdown, closes the application before the compositor, and waits for
process handles. Legacy tray apps use Restart Manager with exact PID/creation
identities, without RmForceShutdown or basename termination. PID, path, IPC reply,
Restart Manager result and wait result are retained in the log. Unresponsive or
busy components still stop installation safely.

Shutdown now precedes display-recovery checks. After the owning app exits, setup
rechecks hidden-display ownership and PnP/active virtual-monitor state. If recovery
did not finish, setup stops and retains recovery information and helpers. Known
product files are probed for writable access without truncation before any driver
staging/binding, so a remaining file lock has a named diagnostic at preflight.

A GUI setup/uninstaller launched from its registered/default installed location
copies itself into a unique admin/SYSTEM-only ProgramData/SetupSessions directory.
The child inherits only a parent process handle, waits for that process to exit,
and then performs the requested operation. Thus the installed EXE is not kept
mapped by a waiting bootstrap process. The original launcher returns explicit
exit 17 (handoff, not installation success); the child GUI owns the final result.
Temporary worker cleanup is scheduled for a later reboot, without declaring that
this cache cleanup itself requires an immediate restart. Quiet mode from the
installed EXE returns 17 with instructions to run an external copy instead; it
does not claim a synchronous success before another process finishes. Package
scripts should use the downloaded/build installer outside the product directory.
A machine-wide setup mutex prevents concurrent new-version install/uninstall
operations (exit 18 if another setup is active); other installers are never killed
in the middle of driver operations.

Windows regression fixtures cover installer IPC, a legacy window ignoring WM_CLOSE,
busy refusal, an executable with the same basename outside the product directory,
and detection of a locked setup before PnP changes. Fixtures use temporary files
and controlled child processes, never actual installation or display APIs.
Real Windows 11 acceptance still includes app-to-tray, active split restoration,
installed-path GUI uninstall/handoff, a second setup window, and external quiet
installation. A hung older app may still refuse graceful Restart Manager shutdown;
setup then fails with evidence instead of forcibly interrupting display recovery.
