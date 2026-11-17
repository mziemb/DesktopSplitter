# Stable monitor identification (identity schema 1)

This change targets `dev/codex-desktopsplitter`. It does not change the IddCx
protocol, EDID generation, signing settings or the driver binary interface.

## Model and trust boundaries

`MonitorIdentity` is the JSON contract shared with dscomp:

- `stableMonitorId`: catalog-owned logical ID, for example `MON-001`.
- `monitorDevicePath`: Windows device interface evidence, not a global ID.
- `manufacturer`, `productCode`, `serialNumber`: corroborating EDID evidence.
  Zero and `0xffffffff` serials are unusable for port-independent matching.
- `adapterPath`: adapter instance evidence, required for virtual segments.
- `virtualAdapterId`: catalog-assigned GUID for a DesktopSplitter adapter instance.
- `segmentIndex`: existing driver EDID index; -1 means physical.

The current GDI name, adapter LUID and target ID are **addresses**, resolved from
fresh connected DisplayConfig targets. They never create or recover a stable ID.
IDs are installation-local; copying a catalog to another computer does not prove
that its devices are the same.

A physical binding is created by explicit user confirmation. On later discovery:

1. A unique matching path must have compatible manufacturer/product and no
   contradictory usable serial.
2. Without that path, only a unique manufacturer/product/usable-serial match in
   both the saved catalog and connected observations may recover the binding.
3. Conflicting evidence, missing devices, duplicate bindings, unsupported schemas
   or insufficient evidence fail closed. Enumeration order and friendly names
   never break ties.

Identical monitors without usable unique serials remain distinguishable only by
confirmed path/connection evidence. An undetectable swap of identical hardware on
unchanged ports cannot be solved by software: these IDs describe confirmed
bindings, not guaranteed globally unique panel identities. After a path change,
use **Identity details / Confirm monitor** to rebind an offline logical ID or
create a new ID. Rebinding an ID that is still connected elsewhere is refused.
An explicitly replaced binding is retained as retired catalog evidence.

The catalog is `%APPDATA%\DesktopSplitter\monitor-identities.json`. Missing
catalogs require new physical confirmation; corrupt catalogs are not silently
replaced. Catalog and settings writes use temporary files and atomic replacement
within the same directory. The current implementation serializes writes within
one application process; concurrent control application instances are unsupported.

## Virtual devices

A virtual logical ID is `DSV-<adapter GUID>-<segment number>`; its caption is
`DS-VIRTUAL-01`, etc. The adapter GUID is catalog-owned and anchored to Windows'
adapter path, and the segment index comes from the existing DSP EDID serial range.
Recreating a segment on the same adapter retains its ID; a different adapter
instance receives a different GUID. Multiple control interfaces currently stop
Apply with an explicit error until adapter-selection UI exists.

Neither a new GDI name nor positional DISPLAY order identifies a virtual device.
Incomplete EDID/adapter evidence stops Apply rather than guessing the mapping.
Adapter node replacement/path changes are not assumed to represent the old
adapter, even if the same segment serials reappear.

## Application, Apply/Revert and hidden targets

The physical monitor selector shows the logical ID, friendly name and current
Windows address. The details dialog exposes path, EDID, current LUID/target and
active state. It also shows connected inactive monitors for recovery confirmation.
Apply requires an active confirmed target. All active physical screens must be
confirmed because layout staging writes every active screen's placement.
Clone sources with multiple targets are rejected before staging.

Apply and Revert preflight the saved roles before stopping dscomp or changing
screens. Placement and primary changes resolve logical IDs again at the operation
boundary. A detected topology change while staging stops further application.
Windows does not offer a lock spanning enumeration and a later display API;
there remains an unavoidable time-of-check/time-of-use window during hot-plug.
Full transactional Apply/rollback is a separate audited repair, not implemented
by this identity change.

Connected inactive targets are enumerated with `QDC_ALL_PATHS` and deduplicated
by current adapter/target, preferring active paths. Inactive source names are
never treated as usable GDI addresses. Specialization operations resolve current
target addresses using the stable ID. Recovery ownership is persisted **before**
hiding a monitor, and is cleared only after a fresh active-target observation and
a successful specialization probe proving that hiding is disabled. Missing or
ambiguous devices retain recovery ownership and stop the operation.

The suspension watcher now stores logical IDs; each primary switch resolves them
inside the existing operation gate. This change does not redesign the watcher's
thread lifecycle or the compositor's crash supervision.

## Compositor schema and compatibility

New config fields are `identitySchemaVersion: 1`, `physicalIdentity`,
`physicalWidth`, `physicalHeight`, and `segments[].identity`. Legacy GDI fields
remain diagnostic/current address hints, not authoritative identity evidence.
New dscomp rejects legacy/unverified configs; confirm identities and Apply again.
Ship the updated app and compositor together. Existing driver protocol and
EDID segment numbering remain unchanged. Old app/compositor binaries do not gain
the new safety guarantees merely by reading updated settings.

The native resolver corroborates DisplayConfig metadata with read-only EDID
registry data and enumerates connected hidden targets. It resolves the render
adapter by the target's current LUID, never by an arbitrary adapter-zero fallback.
Capture resolves each segment again when recreating Desktop Duplication. The
window rescuer resolves both its source and target. Specialized acquisition uses
the verified device path directly, including an already hidden target. Initial
hidden-target dimensions come from the config; portrait scanout/orientation
handling remains a separate audited issue.

## Migration

Legacy GDI aliases/friendly names alone cannot prove identity. Automatic migration
therefore does not bind them to today's DISPLAY number. Existing layout values
remain, but automatic Apply is blocked until the physical identity is explicitly
confirmed. Confirmation upgrades settings to schema 1 and disables both automatic
startup Apply flags; the user must opt in again after checking the configuration.

Before the first write to legacy settings, the exact original bytes are copied to
`settings.json.pre-identity.bak`. Later writes do not replace that backup. Invalid
settings are not overwritten by this write path. Already confirmed settings are
not replaced by a migration attempt for another selection.

An explicit checkbox can assign legacy hidden-monitor recovery ownership to the
selected physical device. Nothing infers this relationship from the old GDI alias.
If that checkbox is not confirmed, legacy recovery evidence is retained and
operations that would tear down the split are blocked. Windows Settings remains
the manual recovery route when evidence is unavailable.

## Automated validation

No tests install a driver, edit signing policy or call display mutation APIs.

- `dotnet run --project tests/MonitorIdentity/MonitorIdentity.Tests.csproj -c Release`
  runs production matching, resolution, settings backup and migration code with
  synthetic observations and temporary files.
- `g++ -std=c++17 -Wall -Wextra -Werror tests/MonitorIdentity/IdentityProbe.cpp -o identity-probe`
  then `./identity-probe` validates the production native matcher.
- GitHub Actions runs both on Ubuntu; the existing Windows build compiles the
  complete app, compositor and driver package. The PR trigger targets the dev base.

Scenarios include GDI renumbering, identical models, absent serials, disconnected
and reconnected targets, reordered enumeration, virtual adapter/segment reuse,
catalog restart, legacy migration/backup, hidden targets, conflicting serials,
duplicate records, missing evidence and a mutation callback blocked by failed
resolution. Synthetic tests do not establish hardware behavior.

## Windows 11 acceptance tests (manual, controlled machine)

1. Confirm the two 1920x1080 displays and portrait Samsung 1080x1920. Record IDs,
   device paths and EDIDs; confirm that initial legacy startup cannot auto-Apply.
2. Apply two segments; Revert; repeat with three supported segments. Check each
   logical segment and current target address, not Windows' numbering labels.
3. Turn Samsung off/on and unplug/replug it. Its ID must remain if evidence is
   sufficient; otherwise Apply must stop and request confirmation.
4. Repeat through screen blanking, sleep/wake and restart. Capture identity details
   before and after a DISPLAY3 -> DISPLAY2 renumbering.
5. With two identical no-serial monitors, change ports. Verify that unresolved
   bindings require explicit confirmation. Do not claim to detect an identical
   panel swap that leaves indistinguishable evidence.
6. Verify a hidden target remains visible in identity diagnostics. Stop dscomp and
   test recovery; missing/ambiguous targets must retain recovery ownership.
7. Alter saved evidence in a test copy (wrong serial, duplicate ID, unsupported
   schema) and verify preflight blocks before changing the desktop.
8. Check cursor and window rescue after renumbering, plus RDP connect/disconnect.
9. Verify the original settings backup is byte-identical and survives repeated saves.

Full Windows hardware acceptance, portrait specialized rendering, driver behavior,
and RDP topology still require this manual matrix. Do not merge on portable tests
alone when hardware acceptance is a release requirement.
