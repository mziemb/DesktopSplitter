#include "MonitorUtil.h"
#include "Log.h"

namespace ds {

static std::wstring Manufacturer(USHORT raw) {
    // DisplayConfig exposes the EDID big-endian bytes as a USHORT.
    const USHORT v = static_cast<USHORT>((raw >> 8) | (raw << 8));
    wchar_t text[4] = { static_cast<wchar_t>(((v >> 10) & 31) + L'A' - 1),
        static_cast<wchar_t>(((v >> 5) & 31) + L'A' - 1), static_cast<wchar_t>((v & 31) + L'A' - 1), 0 };
    return text;
}

static void ReadEdidIdentity(MonitorIdentity& identity) {
    // Read-only registry access; no overrides or device changes. Missing EDID is not fabricated.
    auto path = identity.monitorDevicePath;
    if (path.size() < 4) return;
    path = path.substr(4);
    const auto end = path.find(L"#{");
    if (end == std::wstring::npos) return;
    path.resize(end);
    for (auto& ch : path) if (ch == L'#') ch = L'\\';
    path = L"SYSTEM\\CurrentControlSet\\Enum\\" + path + L"\\Device Parameters";
    HKEY key = nullptr;
    if (::RegOpenKeyExW(HKEY_LOCAL_MACHINE, path.c_str(), 0, KEY_QUERY_VALUE, &key) != ERROR_SUCCESS) return;
    BYTE edid[128] = {};
    DWORD size = sizeof(edid), type = 0;
    const LONG rc = ::RegQueryValueExW(key, L"EDID", nullptr, &type, edid, &size);
    ::RegCloseKey(key);
    // EDID may contain extension blocks; query the complete blob separately below.
    if ((rc != ERROR_SUCCESS && rc != ERROR_MORE_DATA) || type != REG_BINARY || size < 128) return;
    if (rc == ERROR_MORE_DATA) {
        HKEY again = nullptr;
        if (::RegOpenKeyExW(HKEY_LOCAL_MACHINE, path.c_str(), 0, KEY_QUERY_VALUE, &again) != ERROR_SUCCESS) return;
        if (size > 32768) { ::RegCloseKey(again); return; }
        std::vector<BYTE> all(size);
        const LONG read = ::RegQueryValueExW(again, L"EDID", nullptr, &type, all.data(), &size);
        ::RegCloseKey(again);
        if (read != ERROR_SUCCESS || all.size() < 128) return;
        for (size_t n = 0; n < 128; ++n) edid[n] = all[n];
    }
    const BYTE header[8] = {0,255,255,255,255,255,255,0};
    unsigned checksum = 0;
    for (size_t n = 0; n < 128; ++n) checksum += edid[n];
    for (size_t n = 0; n < 8; ++n) if (edid[n] != header[n]) return;
    if ((checksum & 255) != 0) return;
    identity.manufacturer = Manufacturer(static_cast<USHORT>(edid[8] | (edid[9] << 8)));
    identity.productCode = edid[10] | (edid[11] << 8);
    identity.serialNumber = static_cast<uint32_t>(edid[12]) | (static_cast<uint32_t>(edid[13]) << 8) |
        (static_cast<uint32_t>(edid[14]) << 16) | (static_cast<uint32_t>(edid[15]) << 24);
}

bool ResolveMonitorIdentity(const MonitorIdentity& identity, MonitorObservation& out, bool requireActive) {
    std::vector<DISPLAYCONFIG_PATH_INFO> paths;
    std::vector<DISPLAYCONFIG_MODE_INFO> modes;
    UINT32 pc = 0, mc = 0;
    bool queried = false;
    for (int attempt = 0; attempt < 5; ++attempt) {
        if (::GetDisplayConfigBufferSizes(QDC_ALL_PATHS, &pc, &mc) != ERROR_SUCCESS) return false;
        paths.resize(pc); modes.resize(mc);
        const LONG rc = ::QueryDisplayConfig(QDC_ALL_PATHS, &pc, paths.data(), &mc, modes.data(), nullptr);
        if (rc == ERROR_SUCCESS) { queried = true; break; }
        if (rc != ERROR_INSUFFICIENT_BUFFER) return false;
    }
    if (!queried) return false;
    paths.resize(pc);
    std::vector<MonitorObservation> observations;
    for (const auto& path : paths) {
        if (!path.targetInfo.targetAvailable) continue;
        DISPLAYCONFIG_TARGET_DEVICE_NAME target = {};
        target.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME;
        target.header.size = sizeof(target);
        target.header.adapterId = path.targetInfo.adapterId;
        target.header.id = path.targetInfo.id;
        if (::DisplayConfigGetDeviceInfo(&target.header) != ERROR_SUCCESS) continue;
        const bool active = (path.flags & DISPLAYCONFIG_PATH_ACTIVE) != 0;
        auto existing = observations.end();
        for (auto it = observations.begin(); it != observations.end(); ++it)
            if (IdentityEqual(it->identity.monitorDevicePath, target.monitorDevicePath)) { existing = it; break; }
        if (existing != observations.end() && (existing->active || !active)) continue;
        MonitorObservation observed;
        observed.active = active;
        observed.adapterIdLow = path.targetInfo.adapterId.LowPart;
        observed.adapterIdHigh = path.targetInfo.adapterId.HighPart;
        observed.targetId = path.targetInfo.id;
        observed.identity.monitorDevicePath = target.monitorDevicePath;
        observed.identity.manufacturer = Manufacturer(target.edidManufactureId);
        observed.identity.productCode = target.edidProductCodeId;
        ReadEdidIdentity(observed.identity);
        DISPLAYCONFIG_ADAPTER_NAME adapter = {};
        adapter.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_ADAPTER_NAME;
        adapter.header.size = sizeof(adapter);
        adapter.header.adapterId = path.targetInfo.adapterId;
        if (::DisplayConfigGetDeviceInfo(&adapter.header) == ERROR_SUCCESS) observed.identity.adapterPath = adapter.adapterDevicePath;
        if (IdentityEqual(observed.identity.manufacturer, L"DSP") && observed.identity.serialNumber >= 0xD5000001u &&
            observed.identity.serialNumber <= 0xD5000004u)
            observed.identity.segmentIndex = static_cast<int>(observed.identity.serialNumber - 0xD5000001u);
        if (active) {
            DISPLAYCONFIG_SOURCE_DEVICE_NAME source = {};
            source.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
            source.header.size = sizeof(source);
            source.header.adapterId = path.sourceInfo.adapterId;
            source.header.id = path.sourceInfo.id;
            if (::DisplayConfigGetDeviceInfo(&source.header) == ERROR_SUCCESS) observed.gdiDeviceName = source.viewGdiDeviceName;
        }
        if (existing == observations.end()) observations.push_back(std::move(observed));
        else *existing = std::move(observed);
    }
    const int match = MatchMonitorIdentity(identity, observations);
    if (match < 0) return false;
    out = observations[static_cast<size_t>(match)];
    return !requireActive || (out.active && !out.gdiDeviceName.empty());
}


namespace {

BOOL CALLBACK EnumAllProc(HMONITOR hMon, HDC, LPRECT, LPARAM lp) {
    auto* out = reinterpret_cast<std::vector<MonitorEntry>*>(lp);
    MONITORINFOEXW mi = {};
    mi.cbSize = sizeof(mi);
    if (::GetMonitorInfoW(hMon, &mi)) {
        MonitorEntry e;
        e.device = mi.szDevice;
        e.rect = mi.rcMonitor;
        e.work = mi.rcWork;
        e.primary = (mi.dwFlags & MONITORINFOF_PRIMARY) != 0;
        out->push_back(std::move(e));
    }
    return TRUE;
}

const MonitorEntry* FindEntry(const std::vector<MonitorEntry>& all,
                              const std::wstring& deviceName) {
    for (const MonitorEntry& e : all) {
        if (::_wcsicmp(e.device.c_str(), deviceName.c_str()) == 0) return &e;
    }
    return nullptr;
}

} // namespace

size_t EnumerateMonitors(std::vector<MonitorEntry>& out) {
    out.clear();
    ::EnumDisplayMonitors(nullptr, nullptr, EnumAllProc,
                          reinterpret_cast<LPARAM>(&out));
    return out.size();
}

bool FindMonitorRect(const std::wstring& deviceName, RECT& rect) {
    std::vector<MonitorEntry> all;
    EnumerateMonitors(all);
    const MonitorEntry* e = FindEntry(all, deviceName);
    if (!e) return false;
    rect = e->rect;
    return true;
}

bool FindMonitorWorkArea(const std::wstring& deviceName, RECT& work) {
    std::vector<MonitorEntry> all;
    EnumerateMonitors(all);
    const MonitorEntry* e = FindEntry(all, deviceName);
    if (!e) return false;
    work = e->work;
    return true;
}

size_t FindMonitorRects(const std::vector<std::wstring>& deviceNames,
                        std::vector<RECT>& rects) {
    std::vector<MonitorEntry> all;
    EnumerateMonitors(all);

    rects.clear();
    size_t found = 0;
    for (const std::wstring& name : deviceNames) {
        const MonitorEntry* e = FindEntry(all, name);
        if (e) {
            rects.push_back(e->rect);
            ++found;
        } else {
            DS_WARN(L"display device '%s' is not currently attached", name.c_str());
        }
    }
    return found;
}

} // namespace ds
