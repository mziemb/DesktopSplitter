// Windows filesystem regression tests. Only temporary fixture files are modified.
// No installer sequence, registry writes, driver/PnP operations or delayed deletes.
#include "../../installer/setup/InstalledVerification.h"
#include <iostream>
#include <stdexcept>
#include <functional>
#include <cstring>
#include <thread>
#include "../../installer/setup/SetupRuntime.h"
using namespace DeskSplit;
void Check(bool value) { if (!value) throw std::runtime_error("assertion failed"); }
void Write(const std::wstring& path, const char* bytes) {
    HANDLE file = CreateFileW(path.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    Check(file != INVALID_HANDLE_VALUE); DWORD written = 0;
    Check(WriteFile(file, bytes, static_cast<DWORD>(strlen(bytes)), &written, nullptr) != FALSE);
    CloseHandle(file);
}
struct Sink : IStepSink {
    std::wstring details, errors;
    void OnStep(const wchar_t*) override {}
    void OnDetail(const wchar_t* text) override { details += text; }
    void OnError(const wchar_t* text) override { errors += text; }
    void OnProgress(int) override {}
    bool ConfirmCertificateTrust(const CertificateSummary&) override { return false; }
};
namespace {
std::wstring fixtureMode;
LRESULT CALLBACK FixtureWindow(HWND window, UINT message, WPARAM wparam, LPARAM lparam) {
    if (message == RegisterWindowMessageW(L"DesktopSplitter.InstallerExit.v1")) {
        if (fixtureMode == L"busy") return 2;
        if (fixtureMode == L"ack-stuck") return 1;
        if (fixtureMode == L"ipc") { PostQuitMessage(0); return 1; }
        return 0;
    }
    if (message == WM_CLOSE) return 0; // Reproduce a legacy tray app.
    if (message == WM_QUERYENDSESSION) {
        if (fixtureMode == L"stubborn") { Sleep(40000); return FALSE; }
        return TRUE;
    }
    if (message == WM_ENDSESSION && wparam) { PostQuitMessage(0); return 0; }
    if (message == WM_APP + 1) { PostQuitMessage(0); return 0; }
    return DefWindowProcW(window, message, wparam, lparam);
}
int RunFixture(const wchar_t* mode, const wchar_t* eventName) {
    fixtureMode = mode;
    if (fixtureMode == L"mutex") {
        SetupInstanceLock lock;
        HANDLE ready = OpenEventW(EVENT_MODIFY_STATE, FALSE, eventName); Check(ready != nullptr);
        Check(SetEvent(ready)); CloseHandle(ready);
        return lock.Acquired ? 0 : 18;
    }
    WNDCLASSW cls = {}; cls.lpfnWndProc = FixtureWindow; cls.lpszClassName = L"DS.Setup.ProcessFixture"; cls.hInstance = GetModuleHandleW(nullptr);
    Check(RegisterClassW(&cls) != 0);
    HWND window = CreateWindowW(cls.lpszClassName, L"DS test fixture", 0, 0, 0, 0, 0, nullptr, nullptr, cls.hInstance, nullptr);
    Check(window != nullptr);
    HANDLE event = OpenEventW(EVENT_MODIFY_STATE, FALSE, eventName); Check(event != nullptr); Check(SetEvent(event)); CloseHandle(event);
    MSG message = {}; while (GetMessageW(&message, nullptr, 0, 0) > 0) { TranslateMessage(&message); DispatchMessageW(&message); }
    DestroyWindow(window); return 0;
}
struct Fixture {
    PROCESS_INFORMATION process = {};
    Fixture(const std::wstring& directory, const wchar_t* mode) {
        wchar_t self[MAX_PATH] = {}; Check(GetModuleFileNameW(nullptr, self, ARRAYSIZE(self)) != 0);
        std::wstring exe = directory + L"\\DesktopSplitter.exe";
        Check(CopyFileW(self, exe.c_str(), FALSE));
        static unsigned serial = 0;
        std::wstring name = L"Local\\DS.ProcessFixture." + std::to_wstring(GetCurrentProcessId()) + L"." + std::to_wstring(++serial);
        HANDLE ready = CreateEventW(nullptr, TRUE, FALSE, name.c_str()); Check(ready != nullptr);
        std::wstring command = L"\"" + exe + L"\" --fixture " + mode + L" " + name;
        STARTUPINFOW startup = {}; startup.cb = sizeof(startup);
        Check(CreateProcessW(exe.c_str(), command.data(), nullptr, nullptr, FALSE, 0, nullptr, directory.c_str(), &startup, &process));
        Check(WaitForSingleObject(ready, 10000) == WAIT_OBJECT_0); CloseHandle(ready);
    }
    ~Fixture() {
        EnumWindows([](HWND window, LPARAM pidValue) -> BOOL {
            DWORD pid = 0; GetWindowThreadProcessId(window, &pid);
            if (pid == static_cast<DWORD>(pidValue)) PostMessageW(window, WM_APP + 1, 0, 0);
            return TRUE;
        }, static_cast<LPARAM>(process.dwProcessId));
        if (WaitForSingleObject(process.hProcess, 10000) == WAIT_TIMEOUT) {
            // Only this test-created fixture, never a real product process.
            TerminateProcess(process.hProcess, 99); WaitForSingleObject(process.hProcess, 5000);
        }
        CloseHandle(process.hThread); CloseHandle(process.hProcess);
    }
};
}
int wmain(int argc, wchar_t** argv) {
    if (argc == 4 && !wcscmp(argv[1], L"--fixture")) return RunFixture(argv[2], argv[3]);

    int count = 0;
    auto test = [&](const char* name, const std::function<void()>& run) { run(); ++count; std::cout << "PASS " << name << '\n'; };
    wchar_t temp[MAX_PATH] = {}, root[MAX_PATH] = {};
    Check(GetTempPathW(ARRAYSIZE(temp), temp) != 0);
    Check(GetTempFileNameW(temp, L"dsf", 0, root) != 0);
    Check(DeleteFileW(root) && CreateDirectoryW(root, nullptr));
    std::wstring base = root, file = base + L"\\DesktopSplitterSetup.exe", backup = base + L"\\backup", target = base + L"\\product";
    Check(CreateDirectoryW(backup.c_str(), nullptr) && CreateDirectoryW(target.c_str(), nullptr));
    Sink sink;
    test("ReadOnly is cleared while Archive and file contents are preserved", [&] {
        Write(file, "old"); Check(SetFileAttributesW(file.c_str(), FILE_ATTRIBUTE_READONLY | FILE_ATTRIBUTE_ARCHIVE));
        Check(PrepareProductFile(file.c_str(), &sink) == 0);
        DWORD attrs = GetFileAttributesW(file.c_str()); Check(!(attrs & FILE_ATTRIBUTE_READONLY) && (attrs & FILE_ATTRIBUTE_ARCHIVE));
        Check(sink.details.find(L"ACL unchanged") != std::wstring::npos);
    });
    test("missing product file is allowed", [&] { Check(PrepareProductFile((base + L"\\absent.exe").c_str()) == 0); });
    test("directory is never treated as a writable product file", [&] { Check(PrepareProductFile(base.c_str()) == ERROR_ACCESS_DENIED); });
    std::wstring original = backup + L"\\DesktopSplitterSetup.exe", installed = target + L"\\DesktopSplitterSetup.exe";
    test("rollback restores a ReadOnly target without propagating backup ReadOnly", [&] {
        Write(original, "previous"); Write(installed, "replacement");
        Check(SetFileAttributesW(original.c_str(), FILE_ATTRIBUTE_READONLY | FILE_ATTRIBUTE_ARCHIVE));
        Check(SetFileAttributesW(installed.c_str(), FILE_ATTRIBUTE_READONLY | FILE_ATTRIBUTE_ARCHIVE));
        Check(RestoreProductBackup(target.c_str(), backup, &sink).ok);
        Check(!(GetFileAttributesW(installed.c_str()) & FILE_ATTRIBUTE_READONLY));
    });
    test("unchanged locked file is not overwritten during rollback", [&] {
        HANDLE lock = CreateFileW(installed.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, 0, nullptr);
        Check(lock != INVALID_HANDLE_VALUE);
        auto result = RestoreProductBackup(target.c_str(), backup, &sink); CloseHandle(lock);
        Check(result.ok && sink.details.find(L"no overwrite needed") != std::wstring::npos);
    });
    test("changed locked file remains an explicit rollback failure", [&] {
        Write(installed, "changed");
        HANDLE lock = CreateFileW(installed.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, 0, nullptr);
        Check(lock != INVALID_HANDLE_VALUE);
        auto result = RestoreProductBackup(target.c_str(), backup, &sink); CloseHandle(lock);
        Check(!result.ok && result.detail.find(installed) != std::wstring::npos);
    });
    test("ReadOnly installed setup can be removed without changing unrelated files", [&] {
        Check(SetFileAttributesW(installed.c_str(), FILE_ATTRIBUTE_READONLY | FILE_ATTRIBUTE_ARCHIVE));
        Write(target + L"\\user.txt", "keep");
        Check(RemoveProductFiles(target.c_str(), &sink) == 0);
        Check(GetFileAttributesW(installed.c_str()) == INVALID_FILE_ATTRIBUTES);
        Check(GetFileAttributesW((target + L"\\user.txt").c_str()) != INVALID_FILE_ATTRIBUTES);
    });
    test("locked setup is detected before any PnP mutation", [&] {
        Write(installed, "locked");
        HANDLE lock = CreateFileW(installed.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, 0, nullptr);
        Check(lock != INVALID_HANDLE_VALUE);
        DWORD error = CheckProductFilesWritable(target.c_str(), &sink); CloseHandle(lock);
        Check(error != 0 && sink.errors.find(installed) != std::wstring::npos);
        Check(DeleteFileW(installed.c_str()));
    });
    test("installer IPC closes a tray app without WM_CLOSE", [&] {
        Fixture child(base, L"ipc"); int stopped = 0;
        Check(QuiesceProductProcesses(base.c_str(), &stopped, &sink) == 0 && stopped == 1);
    });
    test("Restart Manager closes a legacy app that ignores WM_CLOSE", [&] {
        Fixture child(base, L"legacy"); int stopped = 0;
        Check(QuiesceProductProcesses(base.c_str(), &stopped, &sink) == 0 && stopped == 1);
    });
    test("busy application refuses shutdown without being killed", [&] {
        Fixture child(base, L"busy"); int stopped = 0;
        int guardCalls = 0;
        Check(QuiesceProductProcesses(base.c_str(), &stopped, &sink, [&] { ++guardCalls; return true; }) == ERROR_BUSY && stopped == 0);
        Check(guardCalls == 0);
        Check(WaitForSingleObject(child.process.hProcess, 0) == WAIT_TIMEOUT);
    });
    test("same basename outside installation directory is never closed", [&] {
        Fixture child(base, L"ipc"); int stopped = 0;
        Check(QuiesceProductProcesses(target.c_str(), &stopped, &sink) == ERROR_BUSY && stopped == 0);
        Check(WaitForSingleObject(child.process.hProcess, 0) == WAIT_TIMEOUT);
    });
    test("acknowledged shutdown is never force-terminated", [&] {
        Fixture child(base, L"ack-stuck"); int stopped = 0, guardCalls = 0;
        Check(QuiesceProductProcesses(base.c_str(), &stopped, &sink, [&] { ++guardCalls; return true; }) == ERROR_BUSY);
        Check(guardCalls == 0 && WaitForSingleObject(child.process.hProcess, 0) == WAIT_TIMEOUT);
    });
    test("stubborn idle same-user process is closed after guarded force fallback", [&] {
        Fixture child(base, L"stubborn"); int stopped = 0, guardCalls = 0;
        Check(QuiesceProductProcesses(base.c_str(), &stopped, &sink, [&] { ++guardCalls; return true; }) == 0);
        Check(guardCalls == 1 && stopped == 1 && WaitForSingleObject(child.process.hProcess, 0) == WAIT_OBJECT_0);
        DWORD code = 0; Check(GetExitCodeProcess(child.process.hProcess, &code) && code == ERROR_PROCESS_ABORTED);
    });
    test("unsafe display-state guard prevents forced closure", [&] {
        Fixture child(base, L"stubborn"); int stopped = 0, guardCalls = 0;
        Check(QuiesceProductProcesses(base.c_str(), &stopped, &sink, [&] { ++guardCalls; return false; }) != 0);
        Check(guardCalls == 1 && stopped == 0 && WaitForSingleObject(child.process.hProcess, 0) == WAIT_TIMEOUT);
    });
    test("another setup process cannot acquire the mutation mutex", [&] {
        SetupInstanceLock first; Check(first.Acquired);
        Fixture child(base, L"mutex");
        Check(WaitForSingleObject(child.process.hProcess, 5000) == WAIT_OBJECT_0);
        DWORD code = 0; Check(GetExitCodeProcess(child.process.hProcess, &code) && code == 18);
    });
    test("handoff waits for the source process to exit before mutation", [&] {
        Fixture child(base, L"legacy");
        HANDLE inherited = nullptr;
        Check(DuplicateHandle(GetCurrentProcess(), child.process.hProcess, GetCurrentProcess(), &inherited, 0, FALSE, DUPLICATE_SAME_ACCESS));
        Check(WaitForSingleObject(child.process.hProcess, 0) == WAIT_TIMEOUT);
        std::thread close([&] {
            Sleep(100);
            EnumWindows([](HWND window, LPARAM value) -> BOOL {
                DWORD pid = 0; GetWindowThreadProcessId(window, &pid);
                if (pid == static_cast<DWORD>(value)) PostMessageW(window, WM_APP + 1, 0, 0);
                return TRUE;
            }, static_cast<LPARAM>(child.process.dwProcessId));
        });
        bool exited = WaitForSetupParent(reinterpret_cast<ULONG_PTR>(inherited)); close.join();
        Check(exited && WaitForSingleObject(child.process.hProcess, 0) == WAIT_OBJECT_0);
    });
    Check(DeleteFileW((base + L"\\DesktopSplitter.exe").c_str()));
    Check(SetFileAttributesW(original.c_str(), FILE_ATTRIBUTE_NORMAL));
    Check(DeleteFileW(original.c_str()) && DeleteFileW(file.c_str()) && DeleteFileW((target + L"\\user.txt").c_str()));
    Check(RemoveDirectoryW(backup.c_str()) && RemoveDirectoryW(target.c_str()) && RemoveDirectoryW(root));
    std::cout << count << " Windows file maintenance tests passed; temporary files and controlled child processes only, no PnP/registry writes\n";
}
