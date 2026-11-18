#pragma once
#include <string>
#include <vector>
#include <cwctype>
namespace DeskSplit {
inline std::wstring PendingPathKey(std::wstring path) {
    if (path.compare(0, 4, L"\\??\\") == 0 || path.compare(0, 4, L"\\\\?\\") == 0) path.erase(0, 4);
    for (auto& ch : path) { if (ch == L'/') ch = L'\\'; ch = static_cast<wchar_t>(std::towlower(ch)); }
    return path;
}
// Preserve empty destination fields; a generic MULTI_SZ walk would stop too early.
inline bool ParsePendingRenamePairs(const std::vector<wchar_t>& data, std::vector<std::wstring>& pairs) {
    pairs.clear();
    size_t offset = 0;
    while (offset < data.size()) {
        if (!data[offset]) {
            for (; offset < data.size(); ++offset) if (data[offset]) return false;
            break;
        }
        for (int field = 0; field < 2; ++field) {
            size_t start = offset;
            while (offset < data.size() && data[offset]) ++offset;
            if (offset == data.size()) return false;
            pairs.emplace_back(data.data() + start, offset - start);
            ++offset;
        }
    }
    return true;
}
// The registry contains source/destination pairs. An empty destination means deletion.
// Match only our exact known paths; never block on unrelated system reboot requests.
inline bool HasPendingProductDeletion(const std::vector<std::wstring>& pairs,
    const std::vector<std::wstring>& ownedPaths, std::wstring& matched) {
    for (size_t i = 0; i + 1 < pairs.size(); i += 2) {
        if (!pairs[i + 1].empty()) continue;
        for (const auto& path : ownedPaths) {
            if (PendingPathKey(pairs[i]) == PendingPathKey(path)) { matched = path; return true; }
        }
    }
    return false;
}
inline const wchar_t* RestartFinishTitle(bool uninstall) {
    return uninstall ? L"Restart required to finish removal" : L"Restart required - verification pending";
}
inline const wchar_t* RestartFinishSubtitle(bool uninstall) {
    return uninstall ? L"Restart Windows before installing DesktopSplitter again."
        : L"The driver is not certified ready; restart and verify again.";
}
inline const wchar_t* RestartFinishInstructions(bool uninstall) {
    return uninstall ? L"Removal requires a restart (exit 3010). Windows may still need to remove the running uninstaller or finish driver removal.\r\n\r\nRestart Windows before installing DesktopSplitter again."
        : L"Restart required (exit 3010). The driver is NOT certified ready.\r\n\r\nAfter restart, run DesktopSplitterSetup.exe /verify-installed before Apply.";
}
}
