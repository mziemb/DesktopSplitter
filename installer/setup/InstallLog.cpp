#include "InstallLog.h"
#include <shlobj.h>
#include <strsafe.h>
#include <vector>
namespace DeskSplit {
InstallLog::InstallLog(IStepSink* sink) : sink_(sink) {
    wchar_t root[MAX_PATH] = {}, directory[MAX_PATH] = {}, name[MAX_PATH] = {};
    SYSTEMTIME now = {}; GetSystemTime(&now);
    if (FAILED(SHGetFolderPathW(nullptr, CSIDL_COMMON_APPDATA, nullptr, SHGFP_TYPE_CURRENT, root))) { healthy_ = false; return; }
    if (FAILED(StringCchPrintfW(directory, ARRAYSIZE(directory), L"%s\\DesktopSplitter\\Logs", root)) || EnsureDirectory(directory)) { healthy_ = false; return; }
    StringCchPrintfW(name, ARRAYSIZE(name), L"%s\\install-%04u%02u%02uT%02u%02u%02u-%lu-%llu.log", directory,
        now.wYear, now.wMonth, now.wDay, now.wHour, now.wMinute, now.wSecond, GetCurrentProcessId(), GetTickCount64());
    path_ = name;
    file_ = CreateFileW(name, GENERIC_WRITE, FILE_SHARE_READ, nullptr, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file_ == INVALID_HANDLE_VALUE) { healthy_ = false; return; }
    OnDetail((L"Persistent log: " + path_).c_str());
    OnDetail((L"Installer version: " + std::wstring(kProductVersion)).c_str());
    typedef LONG(WINAPI* VersionFunction)(OSVERSIONINFOW*);
    auto getVersion = reinterpret_cast<VersionFunction>(reinterpret_cast<void*>(GetProcAddress(GetModuleHandleW(L"ntdll.dll"), "RtlGetVersion")));
    OSVERSIONINFOW version = {}; version.dwOSVersionInfoSize = sizeof(version);
    if (getVersion && getVersion(&version) == 0) {
        wchar_t text[256] = {}; StringCchPrintfW(text, ARRAYSIZE(text), L"Windows: %lu.%lu build %lu, installer x64", version.dwMajorVersion, version.dwMinorVersion, version.dwBuildNumber);
        OnDetail(text);
    } else OnDetail(L"Windows version: unknown (RtlGetVersion failed)");
    OnDetail(L"PnP evidence: %WINDIR%\\INF\\setupapi.dev.log (read-only reference; not modified)");
}
InstallLog::~InstallLog() { if (file_ != INVALID_HANDLE_VALUE) CloseHandle(file_); }
void InstallLog::Write(const wchar_t* kind, const wchar_t* text) {
    if (!Healthy()) return;
    SYSTEMTIME now = {}; GetSystemTime(&now);
    wchar_t prefix[128] = {}; StringCchPrintfW(prefix, ARRAYSIZE(prefix), L"%04u-%02u-%02uT%02u:%02u:%02u.%03uZ [%s] ",
        now.wYear, now.wMonth, now.wDay, now.wHour, now.wMinute, now.wSecond, now.wMilliseconds, kind);
    std::wstring line = prefix + stage_ + L": " + text + L"\r\n";
    int length = WideCharToMultiByte(CP_UTF8, 0, line.c_str(), static_cast<int>(line.size()), nullptr, 0, nullptr, nullptr);
    if (length <= 0) { healthy_ = false; return; }
    std::vector<char> bytes(static_cast<size_t>(length));
    WideCharToMultiByte(CP_UTF8, 0, line.c_str(), static_cast<int>(line.size()), bytes.data(), length, nullptr, nullptr);
    DWORD written = 0;
    if (!WriteFile(file_, bytes.data(), static_cast<DWORD>(length), &written, nullptr) || written != static_cast<DWORD>(length) || !FlushFileBuffers(file_)) healthy_ = false;
}
void InstallLog::OnStep(const wchar_t* text) { stage_ = text; Write(L"STEP", text); sink_->OnStep(text); }
void InstallLog::OnDetail(const wchar_t* text) { Write(L"DETAIL", text); sink_->OnDetail(text); }
void InstallLog::OnError(const wchar_t* text) { Write(L"ERROR", text); sink_->OnError(text); }
}
