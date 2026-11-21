#pragma once
#include <string>
#include <vector>
#include <cwctype>
#include <cstdint>

namespace ds {
struct MonitorIdentity {
    std::wstring stableMonitorId;
    std::wstring monitorDevicePath;
    std::wstring adapterPath;
    std::wstring manufacturer;
    int productCode = -1;
    uint32_t serialNumber = 0;
    std::wstring virtualAdapterId;
    int segmentIndex = -1;
};
struct MonitorObservation {
    MonitorIdentity identity;
    std::wstring gdiDeviceName;
    bool active = false;
    // Current operation addresses, never persisted or used as matching evidence.
    uint32_t adapterIdLow = 0, targetId = 0;
    int32_t adapterIdHigh = 0;
};
inline bool IdentityEqual(const std::wstring& a, const std::wstring& b) {
    if (a.empty() || a.size() != b.size()) return false;
    for (size_t i = 0; i < a.size(); ++i) if (std::towlower(a[i]) != std::towlower(b[i])) return false;
    return true;
}
inline bool HasSerial(const MonitorIdentity& i) { return i.serialNumber != 0 && i.serialNumber != UINT32_MAX; }
inline bool SameModel(const MonitorIdentity& a, const MonitorIdentity& b) {
    return IdentityEqual(a.manufacturer, b.manufacturer) && a.productCode >= 0 && a.productCode == b.productCode;
}
// Returns -1 for missing, conflicting or ambiguous evidence. GDI names and LUIDs never participate.
inline int MatchMonitorIdentity(const MonitorIdentity& wanted, const std::vector<MonitorObservation>& live) {
    if (wanted.stableMonitorId.empty() || wanted.monitorDevicePath.empty() || wanted.manufacturer.empty() || wanted.productCode < 0)
        return -1;
    int result = -1;
    int exactCount = 0;
    for (size_t n = 0; n < live.size(); ++n) {
        const auto& candidate = live[n].identity;
        if (!IdentityEqual(wanted.monitorDevicePath, candidate.monitorDevicePath)) continue;
        ++exactCount;
        if (SameModel(wanted, candidate) && (wanted.segmentIndex >= 0) == (candidate.segmentIndex >= 0) &&
            (!HasSerial(wanted) || !HasSerial(candidate) || wanted.serialNumber == candidate.serialNumber) &&
            (wanted.segmentIndex < 0 || (wanted.segmentIndex == candidate.segmentIndex &&
                IdentityEqual(wanted.adapterPath, candidate.adapterPath)))) result = static_cast<int>(n);
    }
    if (exactCount) return exactCount == 1 ? result : -1;
    int count = 0;
    for (size_t n = 0; n < live.size(); ++n) {
        const auto& candidate = live[n].identity;
        const bool matches = wanted.segmentIndex >= 0
            ? candidate.segmentIndex == wanted.segmentIndex && SameModel(wanted, candidate) &&
                IdentityEqual(wanted.adapterPath, candidate.adapterPath)
            : candidate.segmentIndex < 0 && SameModel(wanted, candidate) && HasSerial(wanted) &&
                candidate.serialNumber == wanted.serialNumber;
        if (matches) { ++count; result = static_cast<int>(n); }
    }
    return count == 1 ? result : -1;
}
} // namespace ds
