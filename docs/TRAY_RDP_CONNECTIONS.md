# RDP connections from the system tray

## Workflow

1. Open the DesktopSplitter tray menu → **RDP connections** → **Manage connections…**.
2. Select **New**, enter a name, host/IP, port and optional user name, then **Save**.
   Existing entries can be edited or deleted. Changes take effect in the tray when
   the manager closes. Deleting an entry does not close an existing remote session.
3. Select the saved connection in the tray's RDP submenu.
4. Select monitors with the checkboxes or by clicking the monitor map. Choose
   **Remote primary** from the selected monitors. Blue tiles are selected; a gold
   outline marks the remote primary. Local Windows primary settings do not change.
5. Choose **Connect**. Windows Remote Desktop Connection handles authentication.

The picker is shown on every launch. A successful client start remembers the
selection and remote primary for that connection and exact set of active monitor
identities. Returning to that monitor set suggests its previous layout, even after
GDI/RDP renumbering, enumeration changes or application restart. Each connection
has independent suggestions, and different monitor sets retain separate choices.

A new set starts without a selection. If changed geometry makes a remembered
selection invalid, the picker explains the problem and retains the old record
until a newly chosen layout is launched. Cancelling the picker changes no layout.
Client startup is the confirmation point: the application does not observe remote
authentication success or read the contents of the remote desktop.

## Monitor identity and client IDs

Only active Windows desktop displays are eligible, including DesktopSplitter's
virtual monitors. Specialized physical targets removed from the desktop are not
listed. In windowed split mode the covered physical source remains a Windows
display: choose its virtual segments for visible remote desktops rather than the
covered physical source.

Every active display needs a confirmed stable identity to key the monitor set.
Unconfirmed displays offer **Confirm identity…** to create a binding after user
confirmation. Use the existing main-window Identity details flow to rebind an old
logical ID instead. Conflicting identities never silently select another device.

The saved layout uses stable IDs, never persistent DISPLAYn names or RDP numbers.
At each refresh and again immediately before launch, a short-lived invocation of
`%SystemRoot%\System32\mstsc.exe /l` obtains the client's actual current IDs.
Its owned monitor-list dialog is read with bounded Win32 text messages, without
clipboard access, simulated keystrokes or inspection of other RDP processes.
The application hides/closes only that invocation's dialog. A brief dialog flash
may occur on some Windows versions.

Numeric monitor records are matched one-to-one to the current DisplayConfig
rectangles, including negative coordinates and portrait displays. Windows state
is read before and after the probe. Missing, unsupported/localized, incomplete or
ambiguous output stops the launch; there is no enumeration-order or DISPLAYn-1
fallback. If Windows hands the probe to another process or blocks dialog reading,
the picker reports an error instead of using guessed IDs. This dependency needs
validation on the target Windows 11 installation and UI language.

## RDP constraints

The generated UTF-16 `.rdp` file uses `use multimon:i:1`, fullscreen mode and an
explicit `selectedmonitors` list. The selected remote primary is listed first.
Selected displays must form a connected group sharing edges; overlaps, gaps,
corner-only contact and mirrored mappings are rejected. At most 16 displays can
be listed. Host policy/client limits can impose additional restrictions.

Before `Process.Start`, the launch coordinator re-enumerates and checks the
complete picker snapshot, geometry, current client IDs, selected IDs and primary.
Apply/Revert in progress stops launching. A changed display configuration requires
an explicit refresh. As with any external client, Windows topology can still
change after the final check; the system RDP client then controls session behavior.
Existing remote sessions are not automatically rearranged on Apply/Revert or
hot-plug. Reopen the tray entry to choose a new layout.

The RDP primary applies only to the remote session. No local primary, driver,
registry, display configuration or numbering changes are made by this feature.

Microsoft references:
- https://learn.microsoft.com/en-us/azure/virtual-desktop/rdp-properties#selectedmonitors
- https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/mstsc

## Persistence and credentials

`%APPDATA%\DesktopSplitter\rdp-connections.json` stores schema 1, connection IDs,
names, endpoints, optional user names and per-monitor-set suggestions. Set keys
are hashes of sorted, length-delimited stable IDs; selection order puts the remote
primary first. Display positions and transient client numbers are not persistent
identities.

Updates load the latest catalog under a lock, validate it, write a temporary file
and atomically replace the catalog, retaining the previous version as `.bak`.
Missing catalogs start empty. Corrupt files, unsupported schemas and invalid or
duplicate records are reported and retained, never replaced by an empty catalog.
Changing an endpoint/user name clears that connection's previous suggestions.
Split settings, recovery data and primary preferences are not overwritten.

No passwords are requested or stored by DesktopSplitter. User names and endpoints
are ordinary configuration data. Authentication is delegated to mstsc, with
CredSSP and server authentication enabled. No shell command interpolation is used.
Each start gets a separate generated file under
`%APPDATA%\DesktopSplitter\RdpSessions`; files are retained for mstsc handoff and
reconnect and contain no passwords. They are not automatically deleted in this
milestone and can be removed after their sessions have closed.

## Automated validation

`dotnet run --project tests/Rdp/Rdp.Tests.csproj -c Release` tests client-list
parsing, exact geometry binding, actual/nonsequential RDP IDs, primary ordering,
single/multiple/portrait displays, contiguous selection, invalid layouts,
per-connection/per-set recall, renumbering, unplug/reconnect, restart, invalid
settings, atomic persistence/backup, deletion and launch-preflight failure paths.
Fakes verify that topology changes, Apply/Revert, unreadable settings and client
startup failure cannot record a successful launch. Post-launch save failures are
reported without starting a duplicate session.

CI runs this suite without starting mstsc, connecting to a host, reading
credentials or changing displays. The full Windows build compiles the WPF
windows and tray integration. Existing monitor identity and installer suites
remain enabled.

## Windows 11 acceptance checklist (manual)

- Create/edit/delete two connections; verify tray text, endpoint tooltip and
  persistence after application restart, including while the main window is hidden.
- Compare picker RDP IDs and rectangles with a manual `mstsc /l` on English and
  Polish Windows. Confirm the probe leaves no dialog/process behind. Test timeout
  or unreadable output and verify no remote connection starts.
- Test one physical display, two adjacent physical displays and Samsung split
  into two/three portrait virtual segments. Verify exactly the selected displays
  show the session and the chosen remote primary receives its main taskbar.
- Verify the local primary is unchanged and an unselected monitor stays local.
- Connect to two different hosts and verify their independent suggestions.
- Select different layouts for the three-physical-monitor and split-monitor sets;
  switch sets and restart the application to verify independent recall.
- Renumber displays, change geometry, disconnect/reconnect, blank/sleep/resume and
  perform Apply/Revert with the picker open. Verify stale launch rejection and
  explicit refresh; confirm missing displays are not silently substituted.
- Try gaps, corner-only contact, overlaps/mirroring and unconfirmed identities.
- Test multiple concurrent remote sessions, mstsc handoff, credentials/certificate
  prompts, authentication failure and reconnect. Running sessions must survive
  editing/deleting profiles or closing DesktopSplitter (apart from the existing
  application's normal split teardown behavior).
- Open the picker on mixed-DPI screens, move it between monitors and check scaling,
  keyboard navigation and long names. Actual mstsc routing and WPF visual behavior
  require this hardware check; compilation/unit tests do not establish them.
