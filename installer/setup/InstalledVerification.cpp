#include "InstalledVerification.h"
#include "Payload.h"
#include "resource.h"
#include "../../compositor/src/Json.h"
#include <winioctl.h>
#include "DeskSplitProtocol.h"
#include <shlobj.h>
#include <strsafe.h>
#include <algorithm>
#include <cstring>
#include <vector>

namespace DeskSplit {
namespace {
std::wstring Join(const std::wstring& directory, const std::wstring& name) { return directory + L"\\" + name; }
StageResult Error(DWORD code, const std::wstring& text) { return StageResult::Fail(code, text); }
const wchar_t* const files[] = {L"DesktopSplitter.exe", L"dscomp.exe", L"devcreate.exe", L"edidoverride.exe", L"compositor-README.md", L"DesktopSplitterSetup.exe"};
DWORD CompareBytes(const wchar_t* path, const void* expected, DWORD length) {
    HANDLE file = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) return GetLastError();
    LARGE_INTEGER size = {}; DWORD result = ERROR_SUCCESS;
    if (!GetFileSizeEx(file, &size)) result = GetLastError();
    else if (size.QuadPart != length) result = ERROR_CRC;
    std::vector<BYTE> chunk(65536);
    DWORD offset = 0;
    while (!result && offset < length) {
        DWORD count = 0, wanted = (std::min<DWORD>)(65536, length - offset);
        if (!ReadFile(file, chunk.data(), wanted, &count, nullptr)) { result = GetLastError(); break; }
        if (count != wanted || memcmp(chunk.data(), static_cast<const BYTE*>(expected) + offset, count)) { result = ERROR_CRC; break; }
        offset += count;
    }
    CloseHandle(file); return result;
}
DWORD CompareFile(const std::wstring& actual, const std::wstring& expected) {
    HANDLE file = CreateFileW(expected.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, 0, nullptr);
    if (file == INVALID_HANDLE_VALUE) return GetLastError();
    LARGE_INTEGER size = {};
    if (!GetFileSizeEx(file, &size) || size.QuadPart < 0 || size.QuadPart > 512 * 1024 * 1024) { CloseHandle(file); return ERROR_INVALID_DATA; }
    std::vector<BYTE> bytes(static_cast<size_t>(size.QuadPart)); DWORD read = 0;
    BOOL ok = ReadFile(file, bytes.data(), static_cast<DWORD>(bytes.size()), &read, nullptr);
    DWORD error = ok ? ERROR_SUCCESS : GetLastError(); CloseHandle(file);
    if (error || read != bytes.size()) return error ? error : ERROR_READ_FAULT;
    return CompareBytes(actual.c_str(), bytes.data(), read);
}
}

DWORD BoundInfName(const DeviceNodeInfo& node, std::wstring& name) {
    HKEY key = nullptr;
    CONFIGRET cr = CM_Open_DevNode_Key(node.DevInst, KEY_QUERY_VALUE, 0, RegDisposition_OpenExisting, &key, CM_REGISTRY_SOFTWARE);
    if (cr != CR_SUCCESS) return CM_MapCrToWin32Err(cr, ERROR_GEN_FAILURE);
    wchar_t value[MAX_PATH] = {}; DWORD size = sizeof(value), type = 0;
    LONG rc = RegQueryValueExW(key, L"InfPath", nullptr, &type, reinterpret_cast<BYTE*>(value), &size);
    RegCloseKey(key);
    if (rc != ERROR_SUCCESS) return static_cast<DWORD>(rc);
    if (type != REG_SZ || value[0] == 0 || size > sizeof(value)) return ERROR_INVALID_DATA;
    value[ARRAYSIZE(value) - 1] = 0; name = value; return ERROR_SUCCESS;
}
StageResult ProbeDriver(const DeviceNodeInfo& node, uint32_t& activeMonitors) {
    activeMonitors = 0;
    ULONG size = 0;
    auto* guid = const_cast<GUID*>(&GUID_DEVINTERFACE_DESKSPLIT);
    auto* instance = const_cast<wchar_t*>(node.InstanceId);
    CONFIGRET cr = CM_Get_Device_Interface_List_SizeW(&size, guid, instance, CM_GET_DEVICE_INTERFACE_LIST_PRESENT);
    if (cr != CR_SUCCESS) return StageResult::Fail(cr, L"Cannot enumerate this device's control interface", L"ConfigurationManager");
    if (size < 2) return Error(ERROR_NOT_READY, L"Driver control interface is missing");
    std::vector<wchar_t> paths(size + 2, 0);
    cr = CM_Get_Device_Interface_ListW(guid, instance, paths.data(), size, CM_GET_DEVICE_INTERFACE_LIST_PRESENT);
    if (cr != CR_SUCCESS) return StageResult::Fail(cr, L"Control interface enumeration changed/failed", L"ConfigurationManager");
    if (!paths[0] || paths[wcslen(paths.data()) + 1]) return Error(ERROR_DUP_NAME, L"Expected exactly one control interface for this device");
    struct Probe {
        OVERLAPPED ov = {};
        DESKSPLIT_STATUS status = {};
        HANDLE device = INVALID_HANDLE_VALUE;
    };
    auto* probe = new Probe;
    probe->ov.hEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    if (!probe->ov.hEvent) { DWORD e = GetLastError(); delete probe; return Error(e, L"CreateEvent for GET_STATUS failed"); }
    probe->device = CreateFileW(paths.data(), GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
        nullptr, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, nullptr);
    if (probe->device == INVALID_HANDLE_VALUE) {
        DWORD e = GetLastError(); CloseHandle(probe->ov.hEvent); delete probe; return Error(e, L"Cannot open device-specific driver interface");
    }
    DWORD returned = 0;
    BOOL ok = DeviceIoControl(probe->device, IOCTL_DESKSPLIT_GET_STATUS, nullptr, 0, &probe->status,
        sizeof(probe->status), &returned, &probe->ov);
    DWORD error = ok ? ERROR_SUCCESS : GetLastError();
    if (!ok && error == ERROR_IO_PENDING) {
        DWORD wait = WaitForSingleObject(probe->ov.hEvent, 3000);
        if (wait != WAIT_OBJECT_0) {
            error = wait == WAIT_TIMEOUT ? ERROR_TIMEOUT : GetLastError();
            CancelIoEx(probe->device, &probe->ov);
            if (WaitForSingleObject(probe->ov.hEvent, 1000) != WAIT_OBJECT_0) {
                // A broken driver may not acknowledge cancellation. Keep the overlapped buffer,
                // handles and event alive until process exit, rather than free in-flight memory.
                return Error(error, L"GET_STATUS timeout; cancellation not acknowledged; probe retained until setup exits");
            }
        } else {
            ok = GetOverlappedResult(probe->device, &probe->ov, &returned, FALSE);
            error = ok ? ERROR_SUCCESS : GetLastError();
        }
    }
    auto status = probe->status;
    CloseHandle(probe->device); CloseHandle(probe->ov.hEvent); delete probe;
    if (error) return Error(error, L"GET_STATUS failed or timed out");
    if (returned != sizeof(status) || status.Version != DESKSPLIT_PROTOCOL_VERSION || status.ActiveMonitorCount > DESKSPLIT_MAX_MONITORS)
        return StageResult::Fail(ERROR_INVALID_DATA, L"GET_STATUS size, protocol version or monitor count is incompatible", L"Protocol");
    activeMonitors = status.ActiveMonitorCount;
    return StageResult::Pass();
}
StageResult VerifyInstalledDriver(const wchar_t* expectedInf, bool reboot, IStepSink* sink) {
    StageResult last = Error(ERROR_NOT_READY, L"Driver verification has not completed");
    ULONGLONG deadline = GetTickCount64() + 15000;
    do {
        std::vector<DeviceNodeInfo> nodes;
        DWORD error = ListDeviceNodes(DESKSPLIT_HARDWARE_ID, false, nodes);
        if (error) return Error(error, L"Device-instance enumeration failed");
        if (nodes.size() != 1) return Error(ERROR_DUP_NAME, L"Duplicate or missing root device instances during final verification");
        error = ListDeviceNodes(DESKSPLIT_HARDWARE_ID, true, nodes);
        if (error) return Error(error, L"Present-device enumeration failed");
        if (nodes.size() != 1) return Error(nodes.empty() ? ERROR_NOT_FOUND : ERROR_DUP_NAME, L"Expected one present DesktopSplitter device");
        const auto& node = nodes[0];
        std::wstring bound; error = BoundInfName(node, bound);
        wchar_t text[1024] = {};
        StringCchPrintfW(text, ARRAYSIZE(text), L"Device=%s CM_status_read=%u status=0x%08lX PnP_problem=%lu bound_INF=%s expected_INF=%s bound_query_Win32=0x%08lX",
            node.InstanceId, node.HasStatus ? 1u : 0u, node.Status, node.Problem, bound.c_str(), expectedInf, error);
        sink->OnDetail(text);
        StringCchPrintfW(text, ARRAYSIZE(text), L"Device problem NTSTATUS available=%u value=0x%08lX", node.HasProblemStatus ? 1u : 0u, node.ProblemStatus);
        sink->OnDetail(text);
        DeviceEvidence evidence;
        evidence.present = true; evidence.statusRead = node.HasStatus;
        if (!node.HasStatus) return StageResult::Fail(node.StatusResult, L"CM_Get_DevNode_Status failed", L"ConfigurationManager");
        evidence.problem = node.Problem; evidence.started = (node.Status & DN_STARTED) != 0;
        evidence.correctInf = !error && _wcsicmp(bound.c_str(), expectedInf) == 0;
        evidence.reboot = reboot;
        uint32_t active = 0;
        StageResult probe = node.HasStatus && !node.Problem && evidence.started ? ProbeDriver(node, active)
            : Error(ERROR_NOT_READY, L"Probe withheld until PnP starts the device");
        evidence.replied = probe.ok || probe.domain == L"Protocol";
        evidence.validReply = probe.ok; evidence.compatibleProtocol = probe.ok; evidence.activeMonitors = active;
        last = EvaluateDevice(evidence);
        if (last.ok) {
            wchar_t windows[MAX_PATH] = {};
            if (!GetWindowsDirectoryW(windows, ARRAYSIZE(windows))) return Error(GetLastError(), L"Windows directory lookup failed");
            HMODULE module = GetModuleHandleW(nullptr);
            HRSRC resource = FindResourceW(module, MAKEINTRESOURCEW(IDR_DRIVER_DLL), RT_RCDATA);
            HGLOBAL loaded = resource ? LoadResource(module, resource) : nullptr;
            const void* bytes = loaded ? LockResource(loaded) : nullptr;
            if (!bytes) return Error(ERROR_RESOURCE_DATA_NOT_FOUND, L"Required UMDF DLL payload missing");
            DWORD binary = CompareBytes(Join(windows, L"System32\\drivers\\UMDF\\DesktopSplitterVdd.dll").c_str(), bytes, SizeofResource(module, resource));
            sink->OnDetail((L"Installed UMDF DLL payload comparison Win32=" + std::to_wstring(binary)).c_str());
            if (binary && !last.restart) return Error(binary, L"Installed UMDF driver DLL does not match this package");
            if (last.restart) sink->OnDetail(L"RESTART REQUIRED: runtime readiness is not certified; run /verify-installed after reboot.");
            else sink->OnDetail(L"PnP started, exact bound INF and GET_STATUS protocol verified.");
            return last;
        }
        if (!probe.ok) sink->OnDetail((L"Probe evidence: " + probe.domain + L" code=" + std::to_wstring(probe.code) + L" " + probe.detail).c_str());
        if (node.Problem && node.Problem != CM_PROB_NEED_RESTART) return last;
        if (!node.HasStatus || probe.domain == L"Protocol") return last;
        Sleep(250);
    } while (GetTickCount64() < deadline);
    return last;
}
StageResult VerifyProductFiles(const wchar_t* directory) {
    const int ids[] = {IDR_APP_EXE, IDR_DSCOMP_EXE, IDR_DEVCREATE_EXE, IDR_EDIDOVERRIDE_EXE, IDR_COMPOSITOR_README};
    HMODULE module = GetModuleHandleW(nullptr);
    for (size_t i = 0; i < ARRAYSIZE(ids); ++i) {
        HRSRC resource = FindResourceW(module, MAKEINTRESOURCEW(ids[i]), RT_RCDATA);
        HGLOBAL loaded = resource ? LoadResource(module, resource) : nullptr;
        const void* bytes = loaded ? LockResource(loaded) : nullptr;
        DWORD size = resource ? SizeofResource(module, resource) : 0;
        if (!bytes || !size) return Error(ERROR_RESOURCE_DATA_NOT_FOUND, files[i]);
        DWORD error = CompareBytes(Join(directory, files[i]).c_str(), bytes, size);
        if (error) return Error(error, L"Installed file differs from embedded payload: " + Join(directory, files[i]));
    }
    wchar_t self[MAX_PATH] = {}; if (!GetModuleFileNameW(nullptr, self, ARRAYSIZE(self))) return Error(GetLastError(), L"Cannot identify setup executable");
    DWORD error = CompareFile(Join(directory, files[5]), self);
    return error ? Error(error, L"Installed setup executable differs from this installer") : StageResult::Pass();
}
namespace {
StageResult CheckRecoveryOwnership() {
    wchar_t appdata[MAX_PATH] = {};
    if (FAILED(SHGetFolderPathW(nullptr, CSIDL_APPDATA, nullptr, SHGFP_TYPE_CURRENT, appdata)))
        return Error(ERROR_PATH_NOT_FOUND, L"Cannot inspect the current user's display recovery state");
    std::wstring settingsPath = Join(appdata, L"DesktopSplitter\\settings.json");
    HANDLE settings = CreateFileW(settingsPath.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, 0, nullptr);
    if (settings == INVALID_HANDLE_VALUE) {
        DWORD error = GetLastError();
        if (error != ERROR_FILE_NOT_FOUND && error != ERROR_PATH_NOT_FOUND) return Error(error, L"Cannot read display recovery settings");
    } else {
        LARGE_INTEGER length = {}; DWORD read = 0;
        if (!GetFileSizeEx(settings, &length) || length.QuadPart <= 0 || length.QuadPart > 1024 * 1024) {
            CloseHandle(settings); return Error(ERROR_INVALID_DATA, L"Invalid display recovery settings; inspect before upgrading");
        }
        std::string text(static_cast<size_t>(length.QuadPart), '\0');
        BOOL ok = ReadFile(settings, text.data(), static_cast<DWORD>(text.size()), &read, nullptr); CloseHandle(settings);
        ds::json::Value root; std::string parseError;
        if (!ok || read != text.size() || !ds::json::Parse(text, root, parseError) || !root.isObject())
            return Error(ERROR_INVALID_DATA, L"Display recovery settings are unreadable; no automatic overwrite");
        for (const char* key : {"specializedDevice", "SpecializedStableMonitorId"}) {
            const auto* value = root.find(key);
            if (value && !value->isString()) return Error(ERROR_INVALID_DATA, L"Recovery ownership has an invalid type; state is unknown");
        }
        if (!root.stringMember("specializedDevice").empty() || !root.stringMember("SpecializedStableMonitorId").empty())
            return Error(ERROR_BUSY, L"Hidden-display recovery ownership remains. Restore that display using the installed app before upgrading; settings retained.");
    }
    return StageResult::Pass();
}
bool ConfirmIdleForForcedClose(IStepSink* sink) {
    auto recovery = CheckRecoveryOwnership();
    if (!recovery.ok) { sink->OnError((L"Force-close refused: " + recovery.detail).c_str()); return false; }
    std::vector<DeviceNodeInfo> all, present;
    DWORD error = ListDeviceNodes(DESKSPLIT_HARDWARE_ID, false, all);
    if (!error) error = ListDeviceNodes(DESKSPLIT_HARDWARE_ID, true, present);
    if (error || all.size() != present.size() || present.size() > 1) {
        sink->OnError(L"Force-close refused: device presence/identity is unknown or ambiguous."); return false;
    }
    if (present.empty()) { sink->OnDetail(L"Force-close safety check: no DesktopSplitter device, no recovery ownership."); return true; }
    const auto& node = present[0];
    if (!node.HasStatus || node.Problem || !(node.Status & DN_STARTED)) {
        sink->OnError(L"Force-close refused: driver state is unknown or not healthy."); return false;
    }
    uint32_t active = 0;
    auto probe = ProbeDriver(node, active);
    if (!probe.ok || active) {
        sink->OnError((L"Force-close refused: GET_STATUS failed or active split exists; active=" + std::to_wstring(active)).c_str()); return false;
    }
    sink->OnDetail(L"Force-close safety check: verified GET_STATUS, zero active virtual monitors, no recovery ownership.");
    return true;
}
}
StageResult CheckUpgradeState(const wchar_t* directory, IStepSink* sink) {
    int stopped = 0;
    DWORD shutdownError = QuiesceProductProcesses(directory, &stopped, sink, [sink] { return ConfirmIdleForForcedClose(sink); });
    if (shutdownError) return Error(shutdownError, L"A verified DesktopSplitter component did not exit after installer IPC / Restart Manager shutdown. See PID, path, idle/recovery checks and shutdown results above. Forced closing is refused unless the display state and process identity are safe.");
    sink->OnDetail((L"Stopped verified components: " + std::to_wstring(stopped)).c_str());
    auto recovery = CheckRecoveryOwnership();
    if (!recovery.ok) return recovery;
    std::vector<DeviceNodeInfo> all, present;
    DWORD error = ListDeviceNodes(DESKSPLIT_HARDWARE_ID, false, all);
    if (error) return Error(error, L"Cannot enumerate previous root device instances");
    error = ListDeviceNodes(DESKSPLIT_HARDWARE_ID, true, present);
    if (error) return Error(error, L"Cannot enumerate present root device instances");
    for (const auto& node : all) sink->OnDetail((L"Existing device: " + std::wstring(node.InstanceId) + L" PnP=" + std::to_wstring(node.Problem)).c_str());
    if (all.size() > 1 || all.size() != present.size()) return Error(ERROR_DUP_NAME, L"Duplicate or non-present DesktopSplitter instances; no automatic device cleanup. Review exact instance IDs in the log.");
    if (!present.empty()) {
        uint32_t active = 0; StageResult probe = ProbeDriver(present[0], active);
        if (probe.ok && active) return Error(ERROR_BUSY, L"Active split detected. Revert using the existing app, restore hidden display, then exit before upgrading.");
        if (!present[0].HasStatus) return StageResult::Fail(present[0].StatusResult, L"Existing device CM status is unknown", L"ConfigurationManager");
        if (!probe.ok && !present[0].Problem) return probe;
        if (!probe.ok) sink->OnDetail((L"Existing device is not healthy; repair may proceed after process checks: " + probe.detail).c_str());
    }
    error = CheckProductFilesWritable(directory, sink);
    return error ? Error(error, L"Product files remain locked or not writable after component shutdown; see exact path above") : StageResult::Pass();
}
StageResult BackUpProductFiles(const wchar_t* directory, std::wstring& backup) {
    wchar_t common[MAX_PATH] = {}; if (FAILED(SHGetFolderPathW(nullptr, CSIDL_COMMON_APPDATA, nullptr, SHGFP_TYPE_CURRENT, common))) return Error(ERROR_PATH_NOT_FOUND, L"ProgramData unavailable");
    backup = Join(common, L"DesktopSplitter\\Recovery\\files-" + std::to_wstring(GetCurrentProcessId()) + L"-" + std::to_wstring(GetTickCount64()));
    DWORD error = EnsureDirectory(backup.c_str()); if (error) return Error(error, L"Cannot create recovery directory");
    for (const auto* name : files) {
        auto from = Join(directory, name), to = Join(backup, name);
        DWORD attr = GetFileAttributesW(from.c_str());
        if (attr == INVALID_FILE_ATTRIBUTES) {
            error = GetLastError(); if (error == ERROR_FILE_NOT_FOUND || error == ERROR_PATH_NOT_FOUND) continue;
            return Error(error, L"Cannot inspect existing product file: " + from);
        }
        if (!CopyFileW(from.c_str(), to.c_str(), TRUE)) return Error(GetLastError(), L"Cannot back up " + from);
        error = CompareFile(to, from); if (error) return Error(error, L"Backup verification failed: " + to);
    }
    return StageResult::Pass();
}
StageResult RestoreProductBackup(const wchar_t* directory, const std::wstring& backup, IStepSink* sink) {
    if (backup.empty()) return Error(ERROR_PATH_NOT_FOUND, L"No verified backup exists");
    for (const auto* name : files) {
        auto from = Join(backup, name), to = Join(directory, name);
        DWORD attr = GetFileAttributesW(from.c_str());
        if (attr == INVALID_FILE_ATTRIBUTES) {
            DWORD error = GetLastError(); if (error != ERROR_FILE_NOT_FOUND && error != ERROR_PATH_NOT_FOUND) return Error(error, L"Cannot inspect backup");
            DWORD prepared = PrepareProductFile(to.c_str(), sink);
            if (prepared) return Error(prepared, L"Cannot prepare new product file for removal: " + to);
            if (!DeleteFileW(to.c_str())) {
                DWORD deleteError = GetLastError();
                if (deleteError != ERROR_FILE_NOT_FOUND && deleteError != ERROR_PATH_NOT_FOUND) return Error(deleteError, L"Cannot remove newly created product file: " + to);
            }
        } else {
            if (CompareFile(to, from) == ERROR_SUCCESS) {
                if (sink) sink->OnDetail((L"Rollback: original bytes already present, no overwrite needed: " + to).c_str());
                continue; // In particular, never overwrite an unchanged running setup.
            }
            DWORD prepared = PrepareProductFile(to.c_str(), sink);
            if (prepared) return Error(prepared, L"Cannot prepare product file for restore: " + to);
            if (!CopyFileW(from.c_str(), to.c_str(), FALSE)) return Error(GetLastError(), L"Cannot restore " + to);
            DWORD error = PrepareProductFile(to.c_str(), sink);
            if (!error) error = CompareFile(to, from); if (error) return Error(error, L"Restored bytes do not match backup");
        }
    }
    return StageResult::Pass();
}
StageResult VerifyInstalledProduct(IStepSink* sink) {
    wchar_t directory[MAX_PATH] = {}, version[64] = {};
    if (!ReadInstalledState(directory, ARRAYSIZE(directory), version, ARRAYSIZE(version))) return Error(GetLastError(), L"Installed product registration is missing or unreadable");
    if (wcscmp(version, kProductVersion)) return Error(ERROR_REVISION_MISMATCH, L"Use the installer for the registered product version");
    auto filesResult = VerifyProductFiles(directory); if (!filesResult.ok) return filesResult;
    std::vector<DeviceNodeInfo> nodes;
    DWORD error = ListDeviceNodes(DESKSPLIT_HARDWARE_ID, false, nodes);
    if (error || nodes.size() != 1) return Error(error ? error : ERROR_DUP_NAME, L"Expected exactly one DesktopSplitter root device");
    std::wstring bound; error = BoundInfName(nodes[0], bound);
    if (error) return Error(error, L"Cannot read the bound INF");
    if (bound.find(L"\\") != std::wstring::npos || bound.find(L"/") != std::wstring::npos) return Error(ERROR_INVALID_DATA, L"Unexpected published INF name");
    ExtractedPackage package = {};
    error = ExtractPayload(&package);
    if (error) return Error(error, L"Cannot extract reference package for verification");
    wchar_t windows[MAX_PATH] = {};
    UINT chars = GetWindowsDirectoryW(windows, ARRAYSIZE(windows));
    if (!chars || chars >= ARRAYSIZE(windows)) return Error(chars ? ERROR_INSUFFICIENT_BUFFER : GetLastError(), L"Windows directory lookup failed");
    error = CompareFile(Join(windows, L"INF\\" + bound), package.InfPath);
    CleanupPayload(&package);
    if (error) return Error(error, L"Bound INF bytes do not match this installer; driver version is not certified");
    return VerifyInstalledDriver(bound.c_str(), false, sink);
}

}
