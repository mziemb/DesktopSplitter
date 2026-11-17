#include "DriverPackage.h"

#include <setupapi.h>
#include <strsafe.h>
#include <cstring>

#pragma comment(lib, "setupapi.lib")

namespace
{
    // DiInstallDriverW lives in newdev.dll. It is resolved dynamically so the
    // installer keeps working (via the pnputil fallback) on any SKU where the
    // export is unavailable, instead of failing to start at all.
    typedef BOOL(WINAPI* PFN_DiInstallDriverW)(HWND, LPCWSTR, DWORD, PBOOL);

    bool InfIsOurs(HINF inf, const wchar_t* catalogName)
    {
        wchar_t catalog[MAX_PATH] = {}, classGuid[64] = {};
        if (!SetupGetLineTextW(nullptr, inf, L"Version", L"CatalogFile", catalog, ARRAYSIZE(catalog), nullptr) ||
            _wcsicmp(catalog, catalogName) ||
            !SetupGetLineTextW(nullptr, inf, L"Version", L"ClassGUID", classGuid, ARRAYSIZE(classGuid), nullptr) ||
            _wcsicmp(classGuid, L"{4D36E968-E325-11CE-BFC1-08002BE10318}")) return false;
        for (UINT index = 0;; ++index) {
            wchar_t section[256] = {};
            if (!SetupEnumInfSectionsW(inf, index, section, ARRAYSIZE(section), nullptr)) break;
            INFCONTEXT context = {};
            if (!SetupFindFirstLineW(inf, section, nullptr, &context)) continue;
            do {
                DWORD fields = SetupGetFieldCount(&context);
                for (DWORD field = 2; field <= fields; ++field) {
                    wchar_t id[256] = {};
                    if (SetupGetStringFieldW(&context, field, id, ARRAYSIZE(id), nullptr) &&
                        _wcsicmp(id, L"Root\\DesktopSplitterVdd") == 0) return true;
                }
            } while (SetupFindNextLine(&context, &context));
        }
        return false;
    }

    DWORD RunProcessAndWait(const wchar_t* ApplicationPath, wchar_t* MutableCommandLine, DWORD* pExitCode)
    {
        STARTUPINFOW StartupInfo = {};
        StartupInfo.cb = sizeof(StartupInfo);

        PROCESS_INFORMATION ProcessInfo = {};

        if (!CreateProcessW(
                ApplicationPath,
                MutableCommandLine,
                nullptr,
                nullptr,
                FALSE,
                0,
                nullptr,
                nullptr,
                &StartupInfo,
                &ProcessInfo))
        {
            return GetLastError();
        }

        DWORD wait = WaitForSingleObject(ProcessInfo.hProcess, 120000);
        if (wait != WAIT_OBJECT_0) {
            DWORD error = wait == WAIT_TIMEOUT ? ERROR_TIMEOUT : GetLastError();
            TerminateProcess(ProcessInfo.hProcess, error); // Only our own timed-out pnputil child.
            WaitForSingleObject(ProcessInfo.hProcess, 3000);
            CloseHandle(ProcessInfo.hThread); CloseHandle(ProcessInfo.hProcess);
            return error;
        }
        DWORD ExitCode = 0;
        if (!GetExitCodeProcess(ProcessInfo.hProcess, &ExitCode))
        {
            DWORD error = GetLastError();
            CloseHandle(ProcessInfo.hThread); CloseHandle(ProcessInfo.hProcess);
            return error;
        }

        CloseHandle(ProcessInfo.hThread);
        CloseHandle(ProcessInfo.hProcess);

        *pExitCode = ExitCode;
        return ExitCode == STILL_ACTIVE ? ERROR_TIMEOUT : ERROR_SUCCESS;
    }

    DWORD RunPnpUtilAddDriver(const wchar_t* InfPath, bool* pRebootRequired)
    {
        wchar_t SystemDir[MAX_PATH] = {};
        if (GetSystemDirectoryW(SystemDir, static_cast<UINT>(ARRAYSIZE(SystemDir))) == 0)
        {
            return GetLastError();
        }

        wchar_t PnpUtilPath[MAX_PATH] = {};
        if (FAILED(StringCchPrintfW(PnpUtilPath, ARRAYSIZE(PnpUtilPath), L"%s\\pnputil.exe", SystemDir)))
        {
            return ERROR_BUFFER_OVERFLOW;
        }

        wchar_t CommandLine[MAX_PATH * 2] = {};
        if (FAILED(StringCchPrintfW(
                CommandLine,
                ARRAYSIZE(CommandLine),
                L"\"%s\" /add-driver \"%s\" /install",
                PnpUtilPath,
                InfPath)))
        {
            return ERROR_BUFFER_OVERFLOW;
        }

        DWORD ExitCode = 0;
        const DWORD Error = RunProcessAndWait(PnpUtilPath, CommandLine, &ExitCode);
        if (Error != ERROR_SUCCESS)
        {
            return Error;
        }

        if (ExitCode == ERROR_SUCCESS_REBOOT_REQUIRED)
        {
            *pRebootRequired = true;
            return ERROR_SUCCESS;
        }

        // 259 (ERROR_NO_MORE_ITEMS) means "staged, but no present device matched
        // it" - expected here, because the device node is created afterwards.
        if (ExitCode == ERROR_NO_MORE_ITEMS)
        {
            return ERROR_SUCCESS;
        }

        return ExitCode;
    }
}

namespace DeskSplit
{
    DWORD StageExactDriverPackage(const wchar_t* InfPath, std::wstring& PublishedName)
    {
        wchar_t destination[MAX_PATH] = {};
        if (!SetupCopyOEMInfW(InfPath, nullptr, SPOST_PATH, 0, destination, ARRAYSIZE(destination), nullptr, nullptr)) return GetLastError();
        const wchar_t* name = wcsrchr(destination, L'\\');
        PublishedName = name ? name + 1 : destination;
        if (PublishedName.empty()) return ERROR_INVALID_DATA;
        // Verify the staged bytes, not just that some catalog with the same basename exists.
        HANDLE original = CreateFileW(InfPath, GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, 0, nullptr);
        HANDLE staged = CreateFileW(destination, GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, 0, nullptr);
        if (original == INVALID_HANDLE_VALUE || staged == INVALID_HANDLE_VALUE) {
            DWORD error = GetLastError();
            if (original != INVALID_HANDLE_VALUE) CloseHandle(original);
            if (staged != INVALID_HANDLE_VALUE) CloseHandle(staged);
            return error;
        }
        BYTE a[4096], b[4096]; DWORD ac = 0, bc = 0, error = ERROR_SUCCESS;
        for (;;) {
            if (!ReadFile(original, a, sizeof(a), &ac, nullptr) || !ReadFile(staged, b, sizeof(b), &bc, nullptr)) { error = GetLastError(); break; }
            if (ac != bc || memcmp(a, b, ac)) { error = ERROR_CRC; break; }
            if (!ac) break;
        }
        CloseHandle(original); CloseHandle(staged); return error;
    }

    _Use_decl_annotations_
    DWORD InstallDriverPackage(
        const wchar_t* InfPath,
        InstallMethod* pMethod,
        bool* pRebootRequired,
        bool* pAlreadyCurrent)
    {
        *pMethod = InstallMethod::DiInstallDriver;
        *pRebootRequired = false;
        *pAlreadyCurrent = false;

        // Fall back to pnputil only when DiInstallDriverW is genuinely
        // unavailable; a real installation failure is reported as-is.
        bool NeedFallback = true;
        DWORD DirectResult = ERROR_SUCCESS;

        HMODULE NewDev = LoadLibraryExW(L"newdev.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
        if (NewDev != nullptr)
        {
            auto Install = reinterpret_cast<PFN_DiInstallDriverW>(
                reinterpret_cast<void*>(GetProcAddress(NewDev, "DiInstallDriverW")));

            if (Install != nullptr)
            {
                BOOL Reboot = FALSE;
                const BOOL Ok = Install(nullptr, InfPath, 0, &Reboot);
                const DWORD Error = Ok ? ERROR_SUCCESS : GetLastError();

                if (Ok)
                {
                    *pRebootRequired = (Reboot != FALSE);
                    NeedFallback = false;
                }
                else
                {
                    switch (Error)
                    {
                    case ERROR_NO_MORE_ITEMS:
                    case ERROR_NO_SUCH_DEVINST:
                        // Package staged; no present device matched it yet. The
                        // device node is created in the next step.
                        NeedFallback = false;
                        break;

                    case ERROR_CALL_NOT_IMPLEMENTED:
                    case ERROR_INVALID_FUNCTION:
                    case ERROR_NOT_SUPPORTED:
                    case ERROR_PROC_NOT_FOUND:
                        NeedFallback = true;
                        break;

                    default:
                        DirectResult = Error;
                        NeedFallback = false;
                        break;
                    }
                }
            }

            FreeLibrary(NewDev);
        }

        if (!NeedFallback)
        {
            return DirectResult;
        }

        *pMethod = InstallMethod::PnpUtil;
        return RunPnpUtilAddDriver(InfPath, pRebootRequired);
    }

    _Use_decl_annotations_
    bool FindAllOemInfs(const wchar_t* CatalogFileName, OemInfList* pList)
    {
        ZeroMemory(pList, sizeof(*pList));

        wchar_t WindowsDir[MAX_PATH] = {};
        if (GetWindowsDirectoryW(WindowsDir, static_cast<UINT>(ARRAYSIZE(WindowsDir))) == 0)
        {
            return false;
        }

        wchar_t SearchPattern[MAX_PATH] = {};
        if (FAILED(StringCchPrintfW(SearchPattern, ARRAYSIZE(SearchPattern), L"%s\\INF\\oem*.inf", WindowsDir)))
        {
            return false;
        }

        WIN32_FIND_DATAW FindData = {};
        HANDLE Find = FindFirstFileW(SearchPattern, &FindData);
        if (Find == INVALID_HANDLE_VALUE)
        {
            return false;
        }

        do
        {
            if ((FindData.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0)
            {
                continue;
            }
            if (pList->Count >= OemInfList::kMax)
            {
                FindClose(Find); SetLastError(ERROR_MORE_DATA); return false;
            }

            wchar_t FullPath[MAX_PATH] = {};
            if (FAILED(StringCchPrintfW(FullPath, ARRAYSIZE(FullPath), L"%s\\INF\\%s",
                                        WindowsDir, FindData.cFileName)))
            {
                continue;
            }

            HINF Inf = SetupOpenInfFileW(FullPath, nullptr, INF_STYLE_WIN4, nullptr);
            if (Inf == INVALID_HANDLE_VALUE)
            {
                continue;
            }

            wchar_t Catalog[MAX_PATH] = {};
            DWORD Required = 0;
            if (SetupGetLineTextW(nullptr, Inf, L"Version", L"CatalogFile", Catalog,
                                  static_cast<DWORD>(ARRAYSIZE(Catalog)), &Required) &&
                _wcsicmp(Catalog, CatalogFileName) == 0 && InfIsOurs(Inf, CatalogFileName))
            {
                const size_t Index = pList->Count;
                StringCchCopyW(pList->Names[Index], MAX_PATH, FindData.cFileName);

                // DriverVer is informational, for the install log.
                wchar_t Ver[128] = {};
                if (SetupGetLineTextW(nullptr, Inf, L"Version", L"DriverVer", Ver,
                                      static_cast<DWORD>(ARRAYSIZE(Ver)), &Required))
                {
                    StringCchCopyW(pList->DriverVer[Index], 128, Ver);
                }

                ++pList->Count;
            }

            SetupCloseInfFile(Inf);
        } while (FindNextFileW(Find, &FindData));

        DWORD enumeration = GetLastError();
        FindClose(Find);
        if (enumeration != ERROR_NO_MORE_FILES) { SetLastError(enumeration); return false; }
        SetLastError(pList->Count ? ERROR_SUCCESS : ERROR_NOT_FOUND);
        return pList->Count > 0;
    }

    _Use_decl_annotations_
    DWORD RemoveSupersededPackages(
        const wchar_t* CatalogFileName,
        const wchar_t* KeepOemName,
        int* pRemovedCount,
        int* pFailedCount)
    {
        *pRemovedCount = 0;
        *pFailedCount = 0;

        OemInfList List = {};
        if (!FindAllOemInfs(CatalogFileName, &List))
        {
            return ERROR_SUCCESS;   // nothing staged at all
        }

        DWORD LastError = ERROR_SUCCESS;

        for (size_t Index = 0; Index < List.Count; ++Index)
        {
            if (_wcsicmp(List.Names[Index], KeepOemName) == 0)
            {
                continue;
            }

            // Not /force: a superseded package that is somehow still bound to a
            // device should stay put rather than be ripped out underneath it.
            const DWORD Error = UninstallOemInf(List.Names[Index], false);
            if (Error == ERROR_SUCCESS)
            {
                ++(*pRemovedCount);
            }
            else
            {
                ++(*pFailedCount);
                LastError = Error;
            }
        }

        return (*pFailedCount > 0) ? LastError : ERROR_SUCCESS;
    }

    _Use_decl_annotations_
    bool FindOemInfName(const wchar_t* CatalogFileName, wchar_t* OemName, size_t NameChars)
    {
        if (NameChars == 0)
        {
            return false;
        }
        OemName[0] = L'\0';

        wchar_t WindowsDir[MAX_PATH] = {};
        if (GetWindowsDirectoryW(WindowsDir, static_cast<UINT>(ARRAYSIZE(WindowsDir))) == 0)
        {
            return false;
        }

        wchar_t SearchPattern[MAX_PATH] = {};
        if (FAILED(StringCchPrintfW(SearchPattern, ARRAYSIZE(SearchPattern), L"%s\\INF\\oem*.inf", WindowsDir)))
        {
            return false;
        }

        WIN32_FIND_DATAW FindData = {};
        HANDLE Find = FindFirstFileW(SearchPattern, &FindData);
        if (Find == INVALID_HANDLE_VALUE)
        {
            return false;
        }

        bool Found = false;

        do
        {
            if ((FindData.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0)
            {
                continue;
            }

            wchar_t FullPath[MAX_PATH] = {};
            if (FAILED(StringCchPrintfW(
                    FullPath,
                    ARRAYSIZE(FullPath),
                    L"%s\\INF\\%s",
                    WindowsDir,
                    FindData.cFileName)))
            {
                continue;
            }

            // Match on the [Version] CatalogFile entry: it is unique to our
            // package and immune to the text-encoding guesswork a raw file scan
            // would need.
            HINF Inf = SetupOpenInfFileW(FullPath, nullptr, INF_STYLE_WIN4, nullptr);
            if (Inf == INVALID_HANDLE_VALUE)
            {
                continue;
            }

            wchar_t Catalog[MAX_PATH] = {};
            DWORD Required = 0;
            if (SetupGetLineTextW(
                    nullptr,
                    Inf,
                    L"Version",
                    L"CatalogFile",
                    Catalog,
                    static_cast<DWORD>(ARRAYSIZE(Catalog)),
                    &Required))
            {
                if (_wcsicmp(Catalog, CatalogFileName) == 0 && InfIsOurs(Inf, CatalogFileName))
                {
                    StringCchCopyW(OemName, NameChars, FindData.cFileName);
                    Found = true;
                }
            }

            SetupCloseInfFile(Inf);
        } while (!Found && FindNextFileW(Find, &FindData));

        FindClose(Find);
        return Found;
    }

    _Use_decl_annotations_
    DWORD UninstallOemInf(const wchar_t* OemName, bool Force)
    {
        const DWORD Flags = Force ? SUOI_FORCEDELETE : 0u;

        if (!SetupUninstallOEMInfW(OemName, Flags, nullptr))
        {
            return GetLastError();
        }

        return ERROR_SUCCESS;
    }
}
