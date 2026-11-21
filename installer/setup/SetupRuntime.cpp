#include "SetupRuntime.h"
#include "InstallEngine.h"
#include <shlobj.h>
#include <sddl.h>
#include <string>
#include <vector>
namespace DeskSplit {
bool WaitForSetupParent(ULONG_PTR inheritedHandle) {
    HANDLE parent = reinterpret_cast<HANDLE>(inheritedHandle);
    if (!parent || GetProcessId(parent) == GetCurrentProcessId() || !GetProcessId(parent)) return false;
    DWORD wait = WaitForSingleObject(parent, 30000);
    CloseHandle(parent);
    return wait == WAIT_OBJECT_0;
}
SetupInstanceLock::SetupInstanceLock() {
    handle_ = CreateMutexW(nullptr, FALSE, L"Global\\DesktopSplitter.Setup.Mutation.v1");
    if (handle_) { DWORD wait = WaitForSingleObject(handle_, 0); Acquired = wait == WAIT_OBJECT_0 || wait == WAIT_ABANDONED; }
}
SetupInstanceLock::~SetupInstanceLock() { if (handle_) { if (Acquired) ReleaseMutex(handle_); CloseHandle(handle_); } }
int RelocateInstalledSetup(const wchar_t* arguments, bool quiet, const wchar_t* directoryOverride) {
    wchar_t self[MAX_PATH] = {}, directory[MAX_PATH] = {}, version[64] = {};
    DWORD length = GetModuleFileNameW(nullptr, self, ARRAYSIZE(self));
    if (!length || length >= ARRAYSIZE(self)) return ExitFileInstallFailed;
    if (directoryOverride) wcsncpy_s(directory, directoryOverride, _TRUNCATE);
    else if (!ReadInstalledState(directory, ARRAYSIZE(directory), version, ARRAYSIZE(version))) GetDefaultInstallDir(directory, ARRAYSIZE(directory));
    std::wstring installed = std::wstring(directory) + L"\\DesktopSplitterSetup.exe";
    if (_wcsicmp(self, installed.c_str())) return -1;
    // A waiting parent would keep its installed image mapped. GUI hands off and exits;
    // quiet callers must start an external package to retain truthful synchronous exit codes.
    if (quiet) return ExitSetupHandoff;
    wchar_t common[MAX_PATH] = {};
    if (FAILED(SHGetFolderPathW(nullptr, CSIDL_COMMON_APPDATA, nullptr, SHGFP_TYPE_CURRENT, common))) return ExitFileInstallFailed;
    std::wstring root = std::wstring(common) + L"\\DesktopSplitter\\SetupSessions";
    if (EnsureDirectory(root.c_str())) return ExitFileInstallFailed;
    GUID id = {}; wchar_t guid[40] = {};
    if (FAILED(CoCreateGuid(&id)) || !StringFromGUID2(id, guid, ARRAYSIZE(guid))) return ExitFileInstallFailed;
    std::wstring session = root + L"\\" + guid, worker = session + L"\\DesktopSplitterSetup.exe";
    PSECURITY_DESCRIPTOR descriptor = nullptr;
    if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(L"D:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)", SDDL_REVISION_1, &descriptor, nullptr)) return ExitFileInstallFailed;
    SECURITY_ATTRIBUTES security = {sizeof(security), descriptor, FALSE};
    BOOL created = CreateDirectoryW(session.c_str(), &security); LocalFree(descriptor);
    if (!created) return ExitFileInstallFailed;
    if (!CopyFileW(self, worker.c_str(), TRUE) || PrepareProductFile(worker.c_str()) != ERROR_SUCCESS) {
        DeleteFileW(worker.c_str()); RemoveDirectoryW(session.c_str()); return ExitFileInstallFailed;
    }
    HANDLE parent = OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, TRUE, GetCurrentProcessId());
    if (!parent) { DeleteFileW(worker.c_str()); RemoveDirectoryW(session.c_str()); return ExitFileInstallFailed; }
    SIZE_T bytes = 0;
    InitializeProcThreadAttributeList(nullptr, 1, 0, &bytes);
    std::vector<BYTE> buffer(bytes);
    auto* list = reinterpret_cast<LPPROC_THREAD_ATTRIBUTE_LIST>(buffer.data());
    if (!InitializeProcThreadAttributeList(list, 1, 0, &bytes)) { CloseHandle(parent); return ExitFileInstallFailed; }
    BOOL attributes = UpdateProcThreadAttribute(list, 0, PROC_THREAD_ATTRIBUTE_HANDLE_LIST, &parent, sizeof(parent), nullptr, nullptr);
    std::wstring command = L"\"" + worker + L"\" " + arguments + L" /setup-parent " + std::to_wstring(reinterpret_cast<ULONG_PTR>(parent));
    STARTUPINFOEXW startup = {}; startup.StartupInfo.cb = sizeof(startup); startup.lpAttributeList = list;
    PROCESS_INFORMATION process = {};
    BOOL started = attributes && CreateProcessW(worker.c_str(), command.data(), nullptr, nullptr, TRUE, EXTENDED_STARTUPINFO_PRESENT,
        nullptr, session.c_str(), &startup.StartupInfo, &process);
    DeleteProcThreadAttributeList(list); CloseHandle(parent);
    if (!started) { DeleteFileW(worker.c_str()); RemoveDirectoryW(session.c_str()); return ExitFileInstallFailed; }
    CloseHandle(process.hThread); CloseHandle(process.hProcess);
    return ExitSetupHandoff; // Explicitly not installation success; worker owns the final GUI result.
}
void ScheduleWorkerCleanup() {
    wchar_t self[MAX_PATH] = {};
    if (!GetModuleFileNameW(nullptr, self, ARRAYSIZE(self))) return;
    std::wstring directory = self;
    auto slash = directory.find_last_of(L'\\'); if (slash == std::wstring::npos) return;
    directory.resize(slash);
    MoveFileExW(self, nullptr, MOVEFILE_DELAY_UNTIL_REBOOT);
    MoveFileExW(directory.c_str(), nullptr, MOVEFILE_DELAY_UNTIL_REBOOT);
}
}
