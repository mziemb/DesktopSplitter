#pragma once
#include <cstdint>
#include <string>

namespace DeskSplit {
enum class InstallStage { Admin, Payload, Preflight, Trust, DriverStore, Device, DriverVerify,
    Backup, Files, FilesVerify, Registration, FinalVerify };
enum class InstallState { Failed, Ready, RestartRequired };
struct StageResult {
    bool ok = false;
    bool restart = false;
    uint32_t code = 0;
    std::wstring domain = L"Win32";
    std::wstring detail;
    static StageResult Pass(bool reboot = false) { StageResult r; r.ok = true; r.restart = reboot; return r; }
    static StageResult Fail(uint32_t error, const std::wstring& message, const std::wstring& kind = L"Win32") {
        StageResult r; r.code = error; r.detail = message; r.domain = kind; return r;
    }
};
struct InstallOutcome {
    InstallState state = InstallState::Failed;
    InstallStage stage = InstallStage::Admin;
    StageResult result;
    bool partial = false;
    bool rollbackAttempted = false, rollbackSucceeded = false;
    bool restart = false;
};
class IInstallBackend {
public:
    virtual ~IInstallBackend() = default;
    virtual StageResult Execute(InstallStage stage) = 0;
    virtual StageResult RestoreFiles() = 0;
    virtual void RecordResult(InstallStage, const StageResult&) {}
    virtual bool Cancelled() = 0;
};
inline const wchar_t* StageName(InstallStage stage) {
    switch (stage) {
    case InstallStage::Admin: return L"Administrator rights";
    case InstallStage::Payload: return L"Embedded payload";
    case InstallStage::Preflight: return L"Existing installation / active split / processes";
    case InstallStage::Trust: return L"Catalog signature and publisher trust";
    case InstallStage::DriverStore: return L"Stage and bind exact driver package";
    case InstallStage::Device: return L"Create or update device node";
    case InstallStage::DriverVerify: return L"PnP / bound INF / GET_STATUS verification";
    case InstallStage::Backup: return L"Back up existing product files";
    case InstallStage::Files: return L"Copy product files";
    case InstallStage::FilesVerify: return L"Verify installed file contents";
    case InstallStage::Registration: return L"Shortcuts and product registration";
    case InstallStage::FinalVerify: return L"Final runtime verification";
    }
    return L"Unknown stage";
}
// Production orchestrator, also exercised using mocks. No stage may default to success.
inline InstallOutcome ExecuteInstall(IInstallBackend& backend) {
    InstallOutcome out;
    bool filesTouched = false;
    const InstallStage stages[] = {InstallStage::Admin, InstallStage::Payload, InstallStage::Preflight,
        InstallStage::Trust, InstallStage::DriverStore, InstallStage::Device, InstallStage::DriverVerify,
        InstallStage::Backup, InstallStage::Files, InstallStage::FilesVerify, InstallStage::Registration, InstallStage::FinalVerify};
    for (auto stage : stages) {
        out.stage = stage;
        if (backend.Cancelled()) { out.result = StageResult::Fail(1223, L"Cancelled"); break; }
        if (stage == InstallStage::DriverStore) out.partial = true; // SetupAPI may partially mutate on failure.
        if (stage == InstallStage::Files) filesTouched = true;
        out.result = backend.Execute(stage);
        backend.RecordResult(stage, out.result);
        out.restart = out.restart || out.result.restart;
        if (!out.result.ok) break;
        if (stage == InstallStage::FinalVerify) {
            out.state = out.restart ? InstallState::RestartRequired : InstallState::Ready;
            out.partial = false;
            return out;
        }
    }
    if (filesTouched) {
        out.rollbackAttempted = true;
        out.rollbackSucceeded = backend.RestoreFiles().ok;
    }
    return out;
}
struct DeviceEvidence {
    bool present = false, statusRead = false, started = false, correctInf = false;
    uint32_t problem = 0, activeMonitors = 0;
    bool replied = false, validReply = false, compatibleProtocol = false;
    bool reboot = false;
};
inline StageResult EvaluateDevice(const DeviceEvidence& e) {
    if (!e.present) return StageResult::Fail(1168, L"Expected device is not present");
    if (!e.statusRead) return StageResult::Fail(31, L"CM_Get_DevNode_Status failed; health is unknown", L"ConfigurationManager");
    if (e.problem != 0 && e.problem != 14) // CM_PROB_NEED_RESTART is decimal 14, not Win32 3010.
        return StageResult::Fail(e.problem, e.problem == 12
            ? L"CM_PROB_NORMAL_CONFLICT (PnP Code 12). The cause has not been established. Collect setupapi.dev.log and device status."
            : L"PnP device reports a problem; driver is not operational", L"PnP");
    if (e.replied && e.validReply && e.activeMonitors != 0)
        return StageResult::Fail(170, L"Virtual monitors became active during installation; restore the split using its owning app before retrying");
    const bool reboot = e.reboot || e.problem == 14;
    if (!e.correctInf && !reboot) return StageResult::Fail(13, L"Device is bound to a different INF than the staged package", L"SetupAPI");
    if (!e.started || e.problem == 14 || !e.correctInf || !e.replied) {
        if (reboot) return StageResult::Pass(true);
        return StageResult::Fail(1460, L"Driver did not become operational / GET_STATUS timed out");
    }
    if (!e.validReply || !e.compatibleProtocol)
        return StageResult::Fail(13, L"Malformed GET_STATUS or incompatible protocol", L"Protocol");
    return StageResult::Pass(reboot);
}
} // namespace DeskSplit
