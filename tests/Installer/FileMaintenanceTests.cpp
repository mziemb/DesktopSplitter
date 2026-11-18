// Windows filesystem regression tests. Only temporary fixture files are modified.
// No installer sequence, registry writes, driver/PnP operations or delayed deletes.
#include "../../installer/setup/InstalledVerification.h"
#include <iostream>
#include <stdexcept>
#include <functional>
#include <cstring>
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
int wmain() {
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
    Check(SetFileAttributesW(original.c_str(), FILE_ATTRIBUTE_NORMAL));
    Check(DeleteFileW(original.c_str()) && DeleteFileW(file.c_str()) && DeleteFileW((target + L"\\user.txt").c_str()));
    Check(RemoveDirectoryW(backup.c_str()) && RemoveDirectoryW(target.c_str()) && RemoveDirectoryW(root));
    std::cout << count << " Windows file maintenance tests passed; temporary files only, no PnP/registry writes\n";
}
