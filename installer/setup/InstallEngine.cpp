#include "InstallEngine.h"
#include "InstallPolicy.h"
#include "InstallLog.h"
#include "InstalledVerification.h"
#include <newdev.h>
#include <shlobj.h>

#include "Payload.h"
#include "DriverPackage.h"
#include "Edition.h"
#include "resource.h"
#include "../common/DeviceNode.h"

#include <winioctl.h>          // CTL_CODE for the IOCTLs in DeskSplitProtocol.h
#include "DeskSplitProtocol.h"

#include <cfgmgr32.h>
#include <strsafe.h>
#include <stdio.h>
#include <stdlib.h>

namespace
{
    const wchar_t kHardwareId[] = DESKSPLIT_HARDWARE_ID;
    const wchar_t kHardwareIdMultiSz[] = DESKSPLIT_HARDWARE_ID L"\0";
    const wchar_t kDeviceDescription[] = L"DesktopSplitter Virtual Display";
    const wchar_t kCatalogFileName[] = L"DesktopSplitterVdd.cat";

    using DeskSplit::IStepSink;

    void Detailf(IStepSink* pSink, _Printf_format_string_ const wchar_t* Format, ...)
    {
        wchar_t Buffer[1024];
        va_list Args;
        va_start(Args, Format);
        _vsnwprintf_s(Buffer, _countof(Buffer), _TRUNCATE, Format, Args);
        va_end(Args);
        pSink->OnDetail(Buffer);
    }

    void Stepf(IStepSink* pSink, _Printf_format_string_ const wchar_t* Format, ...)
    {
        wchar_t Buffer[1024];
        va_list Args;
        va_start(Args, Format);
        _vsnwprintf_s(Buffer, _countof(Buffer), _TRUNCATE, Format, Args);
        va_end(Args);
        pSink->OnStep(Buffer);
    }

    void Errorf(IStepSink* pSink, _Printf_format_string_ const wchar_t* Format, ...)
    {
        wchar_t Buffer[1024];
        va_list Args;
        va_start(Args, Format);
        _vsnwprintf_s(Buffer, _countof(Buffer), _TRUNCATE, Format, Args);
        va_end(Args);
        pSink->OnError(Buffer);
    }

    void ReportWin32(IStepSink* pSink, const wchar_t* What, DWORD Error)
    {
        wchar_t Message[512] = {};
        DeskSplit::FormatWin32Error(Error, Message, ARRAYSIZE(Message));
        Errorf(pSink, L"%s failed: %s (0x%08X)", What, Message, Error);
    }

    // ---- EDID override detection ------------------------------------------
    // edidoverride.exe stashes the original EDID under this value when it
    // patches a monitor, so its presence means that monitor is currently
    // overridden (hidden from the desktop).
    const wchar_t kEdidBackupValue[] = L"EDID_OVERRIDE_DSBACKUP";

    struct OverriddenMonitors
    {
        static constexpr size_t kMax = 16;
        // Full instance path under Enum\DISPLAY, e.g. "PHLC310\7&224ecef0&0&UID260".
        // The bare enum key ("PHLC310") is ambiguous: stale devnode instances of the
        // same monitor sort first and would make edidoverride target the wrong one.
        wchar_t Ids[kMax][200];
        size_t Count;
    };

    // Walks HKLM\SYSTEM\CurrentControlSet\Enum\DISPLAY\<id>\<instance>\Device Parameters
    // looking for the backup marker.
    void FindOverriddenMonitors(OverriddenMonitors* pOut)
    {
        ZeroMemory(pOut, sizeof(*pOut));

        HKEY DisplayKey = nullptr;
        if (RegOpenKeyExW(HKEY_LOCAL_MACHINE,
                          L"SYSTEM\\CurrentControlSet\\Enum\\DISPLAY",
                          0, KEY_ENUMERATE_SUB_KEYS, &DisplayKey) != ERROR_SUCCESS)
        {
            return;
        }

        for (DWORD MonitorIndex = 0; pOut->Count < OverriddenMonitors::kMax; ++MonitorIndex)
        {
            wchar_t MonitorId[64] = {};
            DWORD MonitorIdChars = ARRAYSIZE(MonitorId);
            if (RegEnumKeyExW(DisplayKey, MonitorIndex, MonitorId, &MonitorIdChars,
                              nullptr, nullptr, nullptr, nullptr) != ERROR_SUCCESS)
            {
                break;
            }

            HKEY MonitorKey = nullptr;
            if (RegOpenKeyExW(DisplayKey, MonitorId, 0, KEY_ENUMERATE_SUB_KEYS, &MonitorKey) != ERROR_SUCCESS)
            {
                continue;
            }

            bool Overridden = false;
            wchar_t OverriddenInstance[128] = {};
            for (DWORD InstanceIndex = 0; !Overridden; ++InstanceIndex)
            {
                wchar_t Instance[128] = {};
                DWORD InstanceChars = ARRAYSIZE(Instance);
                if (RegEnumKeyExW(MonitorKey, InstanceIndex, Instance, &InstanceChars,
                                  nullptr, nullptr, nullptr, nullptr) != ERROR_SUCCESS)
                {
                    break;
                }

                wchar_t ParamsPath[256] = {};
                if (FAILED(StringCchPrintfW(ParamsPath, ARRAYSIZE(ParamsPath),
                                            L"%s\\Device Parameters", Instance)))
                {
                    continue;
                }

                HKEY ParamsKey = nullptr;
                if (RegOpenKeyExW(MonitorKey, ParamsPath, 0,
                                  KEY_QUERY_VALUE | KEY_ENUMERATE_SUB_KEYS, &ParamsKey) != ERROR_SUCCESS)
                {
                    continue;
                }

                // edidoverride stores the backup as a SUBKEY of Device Parameters (a copy
                // of the whole EDID_OVERRIDE key); accept a value too, defensively.
                HKEY BackupKey = nullptr;
                if (RegOpenKeyExW(ParamsKey, kEdidBackupValue, 0, KEY_QUERY_VALUE, &BackupKey) == ERROR_SUCCESS)
                {
                    RegCloseKey(BackupKey);
                    Overridden = true;
                }
                else if (RegQueryValueExW(ParamsKey, kEdidBackupValue, nullptr, nullptr, nullptr, nullptr) == ERROR_SUCCESS)
                {
                    Overridden = true;
                }
                if (Overridden)
                {
                    StringCchCopyW(OverriddenInstance, ARRAYSIZE(OverriddenInstance), Instance);
                }
                RegCloseKey(ParamsKey);
            }

            RegCloseKey(MonitorKey);

            if (Overridden)
            {
                StringCchPrintfW(pOut->Ids[pOut->Count], ARRAYSIZE(pOut->Ids[pOut->Count]),
                                 L"%s\\%s", MonitorId, OverriddenInstance);
                ++pOut->Count;
            }
        }

        RegCloseKey(DisplayKey);
    }

    bool ReadHidePhysicalDisplayFlag()
    {
        HKEY Key = nullptr;
        if (RegOpenKeyExW(HKEY_LOCAL_MACHINE, DeskSplit::kProductRegKey, 0, KEY_QUERY_VALUE, &Key) != ERROR_SUCCESS)
        {
            return false;
        }

        DWORD Value = 0;
        DWORD Bytes = sizeof(Value);
        DWORD Type = 0;
        const bool Ok = RegQueryValueExW(Key, L"HidePhysicalDisplay", nullptr, &Type,
                                         reinterpret_cast<BYTE*>(&Value), &Bytes) == ERROR_SUCCESS &&
                        Type == REG_DWORD;
        RegCloseKey(Key);
        return Ok && Value != 0;
    }

    // Runs edidoverride.exe from the install directory. Returns false when the
    // helper could not be launched at all.
    bool RunEdidOverride(const wchar_t* InstallDir, const wchar_t* Arguments, DWORD* pExitCode)
    {
        wchar_t Exe[MAX_PATH] = {};
        if (FAILED(StringCchPrintfW(Exe, ARRAYSIZE(Exe), L"%s\\edidoverride.exe", InstallDir)))
        {
            return false;
        }
        if (GetFileAttributesW(Exe) == INVALID_FILE_ATTRIBUTES)
        {
            return false;
        }

        wchar_t CommandLine[MAX_PATH + 256] = {};
        if (FAILED(StringCchPrintfW(CommandLine, ARRAYSIZE(CommandLine), L"\"%s\" %s", Exe, Arguments)))
        {
            return false;
        }

        STARTUPINFOW StartupInfo = { sizeof(STARTUPINFOW) };
        StartupInfo.dwFlags = STARTF_USESHOWWINDOW;
        StartupInfo.wShowWindow = SW_HIDE;
        PROCESS_INFORMATION ProcessInfo = {};

        if (!CreateProcessW(Exe, CommandLine, nullptr, nullptr, FALSE, CREATE_NO_WINDOW,
                            nullptr, InstallDir, &StartupInfo, &ProcessInfo))
        {
            return false;
        }

        DWORD wait = WaitForSingleObject(ProcessInfo.hProcess, 120000);
        if (wait != WAIT_OBJECT_0) {
            DWORD error = wait == WAIT_TIMEOUT ? ERROR_TIMEOUT : GetLastError();
            TerminateProcess(ProcessInfo.hProcess, error);
            CloseHandle(ProcessInfo.hThread); CloseHandle(ProcessInfo.hProcess); SetLastError(error); return false;
        }
        if (!GetExitCodeProcess(ProcessInfo.hProcess, pExitCode))
        {
            DWORD error = GetLastError(); CloseHandle(ProcessInfo.hThread); CloseHandle(ProcessInfo.hProcess); SetLastError(error); return false;
        }

        CloseHandle(ProcessInfo.hThread);
        CloseHandle(ProcessInfo.hProcess);
        return true;
    }

    // Opens the driver's device interface, if the driver is running.
    HANDLE OpenDriverInterface()
    {
        ULONG Length = 0;
        if (CM_Get_Device_Interface_List_SizeW(
                &Length, const_cast<GUID*>(&GUID_DEVINTERFACE_DESKSPLIT), nullptr,
                CM_GET_DEVICE_INTERFACE_LIST_PRESENT) != CR_SUCCESS || Length < 2)
        {
            return INVALID_HANDLE_VALUE;
        }

        wchar_t* Buffer = static_cast<wchar_t*>(LocalAlloc(LPTR, Length * sizeof(wchar_t)));
        if (Buffer == nullptr)
        {
            return INVALID_HANDLE_VALUE;
        }

        HANDLE Device = INVALID_HANDLE_VALUE;
        if (CM_Get_Device_Interface_ListW(
                const_cast<GUID*>(&GUID_DEVINTERFACE_DESKSPLIT), nullptr, Buffer, Length,
                CM_GET_DEVICE_INTERFACE_LIST_PRESENT) == CR_SUCCESS && Buffer[0] != L'\0')
        {
            Device = CreateFileW(Buffer, GENERIC_READ | GENERIC_WRITE,
                                 FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr,
                                 OPEN_EXISTING, 0, nullptr);
        }

        LocalFree(Buffer);
        return Device;
    }
}

namespace DeskSplit
{
    _Use_decl_annotations_
    void FormatWin32Error(DWORD Error, wchar_t* Buffer, size_t Chars)
    {
        Buffer[0] = L'\0';

        const DWORD Length = FormatMessageW(
            FORMAT_MESSAGE_FROM_SYSTEM | FORMAT_MESSAGE_IGNORE_INSERTS,
            nullptr, Error, MAKELANGID(LANG_NEUTRAL, SUBLANG_DEFAULT),
            Buffer, static_cast<DWORD>(Chars), nullptr);

        if (Length == 0)
        {
            StringCchPrintfW(Buffer, Chars, L"(no description)");
            return;
        }

        for (size_t Index = wcslen(Buffer); Index > 0; --Index)
        {
            const wchar_t Ch = Buffer[Index - 1];
            if (Ch == L'\r' || Ch == L'\n' || Ch == L' ' || Ch == L'.')
            {
                Buffer[Index - 1] = L'\0';
            }
            else
            {
                break;
            }
        }
    }

    _Use_decl_annotations_
    bool RevertSplit(IStepSink* pSink)
    {
        HANDLE Device = OpenDriverInterface();
        if (Device == INVALID_HANDLE_VALUE)
        {
            Detailf(pSink, L"Driver not running - no split to revert.");
            return true;
        }

        DESKSPLIT_CONFIG Config = {};
        Config.Version = DESKSPLIT_PROTOCOL_VERSION;
        Config.MonitorCount = 0;

        DWORD Returned = 0;
        const BOOL Ok = DeviceIoControl(Device, IOCTL_DESKSPLIT_SET_CONFIG,
                                        &Config, sizeof(Config), nullptr, 0, &Returned, nullptr);
        const DWORD Error = Ok ? ERROR_SUCCESS : GetLastError();
        CloseHandle(Device);

        if (Ok)
        {
            Detailf(pSink, L"Removed all virtual monitors; the desktop is back on the real display.");
            Sleep(1200);   // let the topology settle before the device disappears
            return true;
        }

        ReportWin32(pSink, L"Removing the virtual monitors", Error);
        return false;
    }

    _Use_decl_annotations_
    int RunInstallSequence(const InstallOptions* options, IStepSink* sink)
    {
        if (!IsUserAnAdmin()) { sink->OnError(L"Stage=Administrator rights Win32=5: administrator rights required; no installation performed."); return ExitNotElevated; }
        InstallLog log(sink);
        if (!log.Healthy()) { sink->OnError(L"Cannot create durable ProgramData installation log; installation stopped before changes."); return ExitLogFailed; }
        struct Backend final : IInstallBackend {
            const InstallOptions* options;
            InstallLog& log;
            ExtractedPackage package = {};
            std::wstring published, backup;
            bool reboot = false, certChanged = false;
            DWORD bytes = 0;
            Backend(const InstallOptions* o, InstallLog& l) : options(o), log(l) {}
            ~Backend() { if (package.Directory[0]) CleanupPayload(&package); }
            bool Cancelled() override { return log.IsCancelled() || !log.Healthy(); }
            void RecordResult(InstallStage stage, const StageResult& result) override {
                Detailf(&log, L"Stage result=%s ok=%u domain=%s code=%u (0x%08X) restart=%u %s", StageName(stage),
                    result.ok ? 1u : 0u, result.domain.c_str(), result.code, result.code, result.restart ? 1u : 0u, result.detail.c_str());
            }
            StageResult Execute(InstallStage stage) override {
                log.OnStep(StageName(stage));
                log.OnProgress(static_cast<int>(stage) * 100 / 12);
                if (!log.Healthy()) return StageResult::Fail(ERROR_WRITE_FAULT, L"Persistent log write failed");
                switch (stage) {
                case InstallStage::Admin: return IsUserAnAdmin() ? StageResult::Pass() : StageResult::Fail(ERROR_ACCESS_DENIED, L"Administrator rights required");
                case InstallStage::Payload: {
                    if (!HasEmbeddedPayload() || !ResourceExists(IDR_APP_EXE) || !ResourceExists(IDR_DSCOMP_EXE) ||
                        !ResourceExists(IDR_DEVCREATE_EXE) || !ResourceExists(IDR_EDIDOVERRIDE_EXE) || !ResourceExists(IDR_COMPOSITOR_README))
                        return StageResult::Fail(ERROR_RESOURCE_DATA_NOT_FOUND, L"A required embedded component is missing");
                    DWORD error = ExtractPayload(&package);
                    return error ? StageResult::Fail(error, L"Extracting driver payload failed") : StageResult::Pass();
                }
                case InstallStage::Preflight: {
                    DWORD pending = CheckPendingProductRemoval(options->InstallDir, &log);
                    if (pending) return StageResult::Fail(pending, pending == ERROR_SUCCESS_REBOOT_REQUIRED
                        ? L"Previous removal is pending. Restart Windows before installation; no driver or product files were changed."
                        : L"Cannot inspect pending product removal; installation stopped before driver changes");
                    wchar_t dir[MAX_PATH] = {}, version[64] = {};
                    if (ReadInstalledState(dir, ARRAYSIZE(dir), version, ARRAYSIZE(version))) {
                        log.OnDetail((L"Previous installation: " + std::wstring(dir) + L" version=" + version).c_str());
                        if (_wcsicmp(dir, options->InstallDir)) return StageResult::Fail(ERROR_INVALID_PARAMETER, L"Upgrade must use the existing installation directory; user settings were not changed");
                    } else {
                        DWORD error = GetLastError();
                        if (error != ERROR_FILE_NOT_FOUND && error != ERROR_PATH_NOT_FOUND) return StageResult::Fail(error, L"Cannot read prior product registration");
                        log.OnDetail(L"No prior registered product location; existing device and files are still inspected.");
                    }
                    return CheckUpgradeState(options->InstallDir, &log);
                }
                case InstallStage::Trust: {
                    LONG trust = VerifyFileTrust(package.CatPath);
                    Detailf(&log, L"Catalog WinVerifyTrust HRESULT=0x%08lX (%s)", static_cast<DWORD>(trust), TrustStatusText(trust));
                    if (trust == ERROR_SUCCESS) return StageResult::Pass();
                    if (trust != CERT_E_UNTRUSTEDROOT && trust != CERT_E_CHAINING)
                        return StageResult::Fail(static_cast<uint32_t>(trust), L"Catalog signature validation failed; certificate stores unchanged", L"HRESULT");
                    PCCERT_CONTEXT cert = LoadSignerCertificate(package.CatPath);
                    if (!cert) return StageResult::Fail(GetLastError(), L"Cannot read publisher certificate");
                    CertificateSummary summary = {};
                    if (!SummarizeCertificate(cert, &summary)) { CertFreeCertificateContext(cert); return StageResult::Fail(ERROR_INVALID_DATA, L"Invalid publisher certificate"); }
                    if (!log.ConfirmCertificateTrust(summary)) { CertFreeCertificateContext(cert); return StageResult::Fail(ERROR_CANCELLED, L"Explicit certificate-store consent was not granted"); }
                    DWORD error = ERROR_SUCCESS;
                    for (const wchar_t* store : {L"Root", L"TrustedPublisher"}) {
                        error = AddCertificateToStore(cert, store);
                        Detailf(&log, L"Consent-based certificate store=%s Win32=0x%08lX", store, error);
                        if (error) break;
                        certChanged = true;
                    }
                    CertFreeCertificateContext(cert);
                    if (error) return StageResult::Fail(error, L"Certificate-store change failed; earlier consented changes may remain");
                    trust = VerifyFileTrust(package.CatPath);
                    return trust == ERROR_SUCCESS ? StageResult::Pass() : StageResult::Fail(static_cast<uint32_t>(trust), L"Catalog still fails trust after explicit consent", L"HRESULT");
                }
                case InstallStage::DriverStore: {
                    DWORD error = StageExactDriverPackage(package.InfPath, published);
                    Detailf(&log, L"Exact driver-store stage Win32/SetupAPI=0x%08lX OEM_INF=%s", error, published.c_str());
                    if (error) return StageResult::Fail(error, L"Staging or verifying the exact INF failed", L"Win32/SetupAPI");
                    InstallMethod method = InstallMethod::DiInstallDriver; bool current = false;
                    error = InstallDriverPackage(package.InfPath, &method, &reboot, &current, &log);
                    Detailf(&log, L"Driver bind method=%u Win32/SetupAPI/process=0x%08lX restart=%u", static_cast<unsigned>(method), error, reboot ? 1u : 0u);
                    if (error) return StageResult::Fail(error, L"Driver package installation failed", method == InstallMethod::PnpUtil ? L"Process(pnputil)" : L"Win32/SetupAPI(DiInstallDriver)");
                    log.OnDetail(L"Old driver packages retained; no superseded-package pruning.");
                    return StageResult::Pass(reboot);
                }
                case InstallStage::Device: {
                    bool existed = false, created = false, restart = false;
                    DWORD error = CreateRootDeviceNode(kHardwareId, kHardwareIdMultiSz, static_cast<DWORD>(sizeof(kHardwareIdMultiSz)), kDeviceDescription, &existed, &created, &restart);
                    reboot = reboot || restart;
                    Detailf(&log, L"CreateRootDeviceNode Win32=0x%08lX existed=%u created=%u restart=%u", error, existed ? 1u : 0u, created ? 1u : 0u, restart ? 1u : 0u);
                    if (error) return StageResult::Fail(error, L"Device registration/binding failed even if a node was created", L"SetupAPI");
                    // Never force-update a second or unrelated instance. Recheck identity immediately before binding.
                    std::vector<DeviceNodeInfo> nodes;
                    error = ListDeviceNodes(kHardwareId, true, nodes);
                    if (error || nodes.size() != 1) return StageResult::Fail(error ? error : ERROR_DUP_NAME, L"Cannot identify a unique present root device");
                    BOOL needRestart = FALSE;
                    if (!UpdateDriverForPlugAndPlayDevicesW(nullptr, kHardwareId, package.InfPath, INSTALLFLAG_FORCE, &needRestart))
                        return StageResult::Fail(GetLastError(), L"Binding the requested INF to the identified root device failed", L"SetupAPI");
                    reboot = reboot || needRestart != FALSE;
                    return StageResult::Pass(reboot);
                }
                case InstallStage::DriverVerify: return VerifyInstalledDriver(published.c_str(), reboot, &log);
                case InstallStage::Backup: {
                    auto result = BackUpProductFiles(options->InstallDir, backup);
                    log.OnDetail((L"Product file recovery directory: " + backup).c_str()); return result;
                }
                case InstallStage::Files: {
                    DWORD error = InstallProductFiles(options->InstallDir, &bytes, &log);
                    return error ? StageResult::Fail(error, L"Product file copy failed (installer exit 12 is not PnP Code 12)") : StageResult::Pass();
                }
                case InstallStage::FilesVerify: return VerifyProductFiles(options->InstallDir);
                case InstallStage::Registration: {
                    DWORD error = CreateShortcuts(options);
                    if (error) return StageResult::Fail(error, L"Required requested shortcut creation failed", L"Win32/HRESULT");
                    error = WriteUninstallEntry(options, bytes / 1024);
                    if (error) return StageResult::Fail(error, L"Add/Remove Programs registration failed");
                    error = WriteProductFlags(options);
                    return error ? StageResult::Fail(error, L"Product registration failed") : StageResult::Pass();
                }
                case InstallStage::FinalVerify: {
                    auto files = VerifyProductFiles(options->InstallDir); if (!files.ok) return files;
                    wchar_t dir[MAX_PATH] = {}, version[64] = {};
                    if (!ReadInstalledState(dir, ARRAYSIZE(dir), version, ARRAYSIZE(version)) ||
                        _wcsicmp(dir, options->InstallDir) || wcscmp(version, kProductVersion))
                        return StageResult::Fail(ERROR_INVALID_DATA, L"Installed product registration/version does not match");
                    return VerifyInstalledDriver(published.c_str(), reboot, &log);
                }
                }
                return StageResult::Fail(ERROR_INVALID_FUNCTION, L"Unimplemented mandatory stage");
            }
            StageResult RestoreFiles() override {
                log.OnStep(L"Rollback of product files only");
                auto result = RestoreProductBackup(options->InstallDir, backup, &log);
                if (!result.ok) Detailf(&log, L"ROLLBACK FAILED Win32=0x%08X %s", result.code, result.detail.c_str());
                else log.OnDetail(L"Previous product bytes restored; driver bindings and partial registration are not automatically rolled back.");
                return result;
            }
        } backend(options, log);
        InstallOutcome result = ExecuteInstall(backend);
        if (!log.Healthy()) { sink->OnError(L"Durable log failed; partial installation is possible. Success is not certified."); return ExitLogFailed; }
        if (result.state == InstallState::Failed) {
            std::vector<DeviceNodeInfo> evidence;
            DWORD enumerate = ListDeviceNodes(kHardwareId, false, evidence);
            Detailf(&log, L"Failure snapshot root-device enumeration Win32=0x%08lX count=%zu", enumerate, evidence.size());
            for (const auto& node : evidence) {
                std::wstring bound; DWORD binding = BoundInfName(node, bound);
                Detailf(&log, L"Failure device=%s CONFIGRET=0x%08lX DN_status=0x%08lX PnP_code=%lu (%s) bound_INF=%s binding_query_Win32=0x%08lX",
                    node.InstanceId, node.StatusResult, node.Status, node.Problem, ProblemCodeText(node.Problem), bound.c_str(), binding);
                Detailf(&log, L"Failure problem NTSTATUS available=%u value=0x%08lX", node.HasProblemStatus ? 1u : 0u, node.ProblemStatus);
            }
            Errorf(&log, L"FAILED stage=%s component=DesktopSplitter/%s domain=%s code=%u (0x%08X): %s. restart=%u partial=%u rollback_files_attempted=%u rollback_files_ok=%u. Log: %s",
                StageName(result.stage), kHardwareId, result.result.domain.c_str(), result.result.code, result.result.code,
                result.result.detail.c_str(), result.restart ? 1u : 0u, (result.partial || backend.certChanged) ? 1u : 0u,
                result.rollbackAttempted ? 1u : 0u, result.rollbackSucceeded ? 1u : 0u, log.Path().c_str());
            log.OnDetail(L"The cause has not been established, unless the explicit API evidence above identifies it. Keep the log and the matching timestamp/instance entries in setupapi.dev.log. Old driver packages and user settings were retained.");
            switch (result.stage) {
            case InstallStage::Admin: return ExitNotElevated;
            case InstallStage::Preflight: return result.result.code == ERROR_SUCCESS_REBOOT_REQUIRED ? ExitPendingRemoval : ExitVerificationFailed;
            case InstallStage::Payload: return ExitPayloadExtractFailed;
            case InstallStage::Trust: return ExitCatalogUnusable;
            case InstallStage::DriverStore: return ExitDriverInstallFailed;
            case InstallStage::Device: return ExitDevNodeFailed;
            case InstallStage::Backup: case InstallStage::Files: case InstallStage::FilesVerify: return ExitFileInstallFailed;
            case InstallStage::Registration: return ExitRegistrationFailed;
            default: return result.result.code == ERROR_CANCELLED ? ExitCancelled : ExitVerificationFailed;
            }
        }
        log.OnProgress(100);
        if (result.state == InstallState::RestartRequired) {
            log.OnDetail(L"RESTART REQUIRED (exit 3010). Installation files are complete; driver readiness is NOT certified. Reboot, then run DesktopSplitterSetup.exe /verify-installed before Apply.");
            return log.Healthy() ? ExitRestartRequired : ExitLogFailed;
        }
        log.OnDetail(L"FINAL RESULT: READY. Mandatory stages, exact files, PnP, bound INF and GET_STATUS verified.");
        return log.Healthy() ? ExitSuccess : ExitLogFailed;
    }

    int RunInstalledVerification(IStepSink* sink)
    {
        InstallLog log(sink);
        if (!log.Healthy()) { sink->OnError(L"Cannot create durable verification log."); return ExitLogFailed; }
        log.OnStep(L"Post-reboot installed product verification (no PnP changes)");
        auto result = VerifyInstalledProduct(&log);
        if (!result.ok) {
            Errorf(&log, L"FAILED verification domain=%s code=%u (0x%08X): %s. The cause has not been established beyond these API results. Log: %s",
                result.domain.c_str(), result.code, result.code, result.detail.c_str(), log.Path().c_str());
            return ExitVerificationFailed;
        }
        log.OnDetail(result.restart ? L"Restart still required; driver readiness NOT certified." : L"READY: installed files, exact INF, PnP and protocol verified.");
        if (!log.Healthy()) return ExitLogFailed;
        return result.restart ? ExitRestartRequired : ExitSuccess;
    }

    _Use_decl_annotations_
    int RunUninstallSequence(const UninstallOptions* pOptions, IStepSink* pSink)
    {
        InstallLog persistent(pSink);
        if (!persistent.Healthy()) { pSink->OnError(L"Cannot create persistent uninstall log; no removal attempted."); return ExitLogFailed; }
        pSink = &persistent;
        pSink->OnProgress(0);
        int Result = ExitSuccess;
        wchar_t InstallDir[MAX_PATH] = {}, Version[64] = {};
        const bool Installed = ReadInstalledState(InstallDir, ARRAYSIZE(InstallDir), Version, ARRAYSIZE(Version));
        if (!Installed) { Errorf(pSink, L"Product location is unknown; refusing automatic removal. Log: %s", persistent.Path().c_str()); return ExitUninstallFailed; }
        auto preflight = CheckUpgradeState(InstallDir, pSink);
        if (!preflight.ok) { Errorf(pSink, L"Removal preflight failed domain=%s code=%u: %s. Log: %s", preflight.domain.c_str(), preflight.code, preflight.detail.c_str(), persistent.Path().c_str()); return ExitUninstallFailed; }
        OverriddenMonitors legacy = {}; FindOverriddenMonitors(&legacy);
        if (legacy.Count) { Errorf(pSink, L"Legacy hidden-display recovery is required before removal; helpers and settings retained. Log: %s", persistent.Path().c_str()); return ExitUninstallFailed; }
        pSink->OnProgress(20);

        // ---- un-hide any monitor we hid --------------------------------
        // This MUST happen while edidoverride.exe is still on disk, and before
        // the files are deleted. Leaving a monitor overridden after uninstall
        // would hide a real display with no tool left to bring it back.
        Stepf(pSink, L"Restoring hidden displays");
        {
            OverriddenMonitors Overridden = {};
            FindOverriddenMonitors(&Overridden);
            const bool FlagSet = ReadHidePhysicalDisplayFlag();

            if (Overridden.Count == 0)
            {
                Detailf(pSink, FlagSet
                            ? L"The hide-display option was on, but no monitor is currently overridden."
                            : L"No monitor is hidden - nothing to restore.");
            }
            else if (!Installed)
            {
                // No install dir recorded, so no helper to run.
                Errorf(pSink, L"%zu monitor(s) still have an EDID override but the install "
                              L"location is unknown.", Overridden.Count);
                for (size_t i = 0; i < Overridden.Count; ++i)
                {
                    Errorf(pSink, L"  RECOVER MANUALLY: edidoverride.exe guarded-revert %s --yes",
                           Overridden.Ids[i]);
                }
            }
            else
            {
                for (size_t i = 0; i < Overridden.Count; ++i)
                {
                    Detailf(pSink, L"Reverting EDID override on %s", Overridden.Ids[i]);

                    wchar_t Arguments[280] = {};
                    StringCchPrintfW(Arguments, ARRAYSIZE(Arguments),
                                     L"guarded-revert \"%s\" --yes", Overridden.Ids[i]);

                    DWORD ExitCode = 0;
                    if (!RunEdidOverride(InstallDir, Arguments, &ExitCode))
                    {
                        // Non-fatal, but the user must be told exactly how to fix it.
                        Errorf(pSink, L"Could not run edidoverride.exe for %s.", Overridden.Ids[i]);
                        Errorf(pSink, L"  RECOVER MANUALLY: edidoverride.exe guarded-revert %s --yes",
                               Overridden.Ids[i]);
                        Errorf(pSink, L"  or delete HKLM\\SYSTEM\\CurrentControlSet\\Enum\\DISPLAY\\%s"
                                      L"\\Device Parameters\\EDID_OVERRIDE and reboot.",
                               Overridden.Ids[i]);
                    }
                    else if (ExitCode == 0)
                    {
                        Detailf(pSink, L"%s is visible to Windows again.", Overridden.Ids[i]);
                    }
                    else
                    {
                        Errorf(pSink, L"edidoverride.exe returned %u for %s; the monitor may still be hidden.",
                               ExitCode, Overridden.Ids[i]);
                        Errorf(pSink, L"  RECOVER MANUALLY: edidoverride.exe guarded-revert %s --yes",
                               Overridden.Ids[i]);
                    }
                }
            }
        }
        pSink->OnProgress(28);

        Stepf(pSink, L"Removing the virtual display device");
        int Removed = 0;
        bool RebootRequired = false;
        DWORD Error = RemoveDeviceNodes(kHardwareId, &Removed, &RebootRequired);
        if (Error != ERROR_SUCCESS)
        {
            ReportWin32(pSink, L"Removing the device", Error);
            return ExitUninstallFailed;
        }
        Detailf(pSink, L"Removed %d device(s).", Removed);
        pSink->OnProgress(45);

        Stepf(pSink, L"Removing the display driver");
        {
            OemInfList Staged = {};
            if (!FindAllOemInfs(kCatalogFileName, &Staged))
            {
                DWORD listError = GetLastError();
                if (listError != ERROR_NOT_FOUND && listError != ERROR_FILE_NOT_FOUND) {
                    ReportWin32(pSink, L"Enumerating owned driver packages", listError);
                    return ExitUninstallFailed;
                }
                Detailf(pSink, L"The driver was not present.");
            }
            else
            {
                for (size_t i = 0; i < Staged.Count; ++i)
                {
                    const DWORD PkgError = UninstallOemInf(Staged.Names[i], false);
                    if (PkgError == ERROR_SUCCESS)
                    {
                        Detailf(pSink, L"Removed %s (%s)", Staged.Names[i], Staged.DriverVer[i]);
                    }
                    else
                    {
                        ReportWin32(pSink, L"Removing the driver package", PkgError);
                        Result = ExitUninstallFailed;
                    }
                }
            }
        }
        if (Result != ExitSuccess) return Result;
        pSink->OnProgress(65);

        Stepf(pSink, L"Removing shortcuts and program files");
        DWORD cleanupError = RemoveShortcuts();
        if (cleanupError) { ReportWin32(pSink, L"Removing shortcuts", cleanupError); return ExitUninstallFailed; }
        if (Installed)
        {
            DWORD fileError = RemoveProductFiles(InstallDir, pSink);
            if (fileError == ERROR_SUCCESS_REBOOT_REQUIRED) RebootRequired = TRUE;
            else if (fileError) { ReportWin32(pSink, L"Removing product files", fileError); return ExitUninstallFailed; }
            Detailf(pSink, fileError == ERROR_SUCCESS_REBOOT_REQUIRED
                ? L"Removed product files; running setup/empty-directory cleanup pending restart: %s"
                : L"Known product files removed from %s; unrelated files, if any, retained.", InstallDir);
        }
        cleanupError = RemoveUninstallEntry();
        if (!cleanupError) cleanupError = RemoveProductFlags();
        if (cleanupError) { ReportWin32(pSink, L"Removing registration", cleanupError); return ExitUninstallFailed; }
        Detailf(pSink, L"Removed registry entries.");
        pSink->OnProgress(85);

        Stepf(pSink, L"Publisher certificate");
        if (!pOptions->RemoveCertificate)
        {
            Detailf(pSink, L"Left in place. Re-run with the certificate option to remove it.");
        }
        else
        {
            ExtractedPackage Package = {};
            if (ExtractPayload(&Package) != ERROR_SUCCESS)
            {
                Errorf(pSink, L"Could not unpack the package to identify the certificate.");
                Result = ExitUninstallFailed;
            }
            else
            {
                PCCERT_CONTEXT Cert = LoadSignerCertificate(Package.CatPath);
                if (Cert == nullptr)
                {
                    Errorf(pSink, L"Could not read the certificate; trust stores untouched.");
                    Result = ExitUninstallFailed;
                }
                else
                {
                    CertificateSummary Summary = {};
                    if (SummarizeCertificate(Cert, &Summary))
                    {
                        const wchar_t* Stores[] = { L"Root", L"TrustedPublisher" };
                        for (const wchar_t* StoreName : Stores)
                        {
                            DWORD certError = RemoveCertificateFromStore(Summary.Sha1, StoreName);
                            if (certError == ERROR_SUCCESS)
                            {
                                Detailf(pSink, L"Removed from LocalMachine\\%s", StoreName);
                            }
                            else { ReportWin32(pSink, L"Removing consented publisher certificate", certError); Result = ExitUninstallFailed; }
                        }
                    }
                    else { Errorf(pSink, L"Could not identify publisher certificate; trust stores untouched."); Result = ExitUninstallFailed; }
                    CertFreeCertificateContext(Cert);
                }

                if (!pOptions->KeepTemp)
                {
                    CleanupPayload(&Package);
                }
            }
        }

        pSink->OnProgress(100);
        if (Result == ExitSuccess) persistent.OnDetail(RebootRequired
            ? L"FINAL RESULT: REMOVAL PENDING RESTART (exit 3010). Restart Windows before reinstalling. Do not run /verify-installed after removal."
            : L"FINAL RESULT: REMOVAL COMPLETE (exit 0).");
        else persistent.OnError(L"FINAL RESULT: REMOVAL INCOMPLETE. Partial changes may remain; retain this log.");
        if (!persistent.Healthy()) return ExitLogFailed;
        return Result == ExitSuccess && RebootRequired ? ExitRestartRequired : Result;
    }
}
