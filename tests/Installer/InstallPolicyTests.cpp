#include "../../installer/setup/InstallPolicy.h"
#include "../../installer/setup/FileMaintenancePolicy.h"
#include <iostream>
#include <stdexcept>
#include <functional>
#include <vector>
using namespace DeskSplit;
void Check(bool ok) { if (!ok) throw std::runtime_error("assertion failed"); }
struct MockBackend : IInstallBackend {
    std::vector<InstallStage> calls;
    InstallStage failAt = static_cast<InstallStage>(-1);
    StageResult failure = StageResult::Fail(5, L"mock failure");
    DeviceEvidence device;
    bool rollbackOk = true, cancel = false;
    int rollbacks = 0;
    MockBackend() {
        device.present = device.statusRead = device.started = device.correctInf = true;
        device.replied = device.validReply = device.compatibleProtocol = true;
    }
    StageResult Execute(InstallStage s) override {
        calls.push_back(s);
        if (s == failAt) return failure;
        if (s == InstallStage::DriverVerify || s == InstallStage::FinalVerify) return EvaluateDevice(device);
        return StageResult::Pass();
    }
    StageResult RestoreFiles() override { ++rollbacks; return rollbackOk ? StageResult::Pass() : StageResult::Fail(5, L"rollback failed"); }
    bool Cancelled() override { return cancel; }
};
int main() {
    int count = 0;
    auto test = [&](const char* name, const std::function<void()>& body) { body(); std::cout << "PASS " << name << '\n'; ++count; };
    test("clean install requires every mandatory stage", [] { MockBackend b; auto r = ExecuteInstall(b); Check(r.state == InstallState::Ready && b.calls.size() == 12); });
    test("upgrade verifies package and files and retains driver rollback options", [] { MockBackend b; auto r = ExecuteInstall(b); Check(r.state == InstallState::Ready && b.calls[4] == InstallStage::DriverStore && !r.rollbackAttempted); });
    for (const char* process : {"running app cannot close", "running compositor cannot close", "active split requires explicit Revert"}) {
        test(process, [] { MockBackend b; b.failAt = InstallStage::Preflight; b.failure = StageResult::Fail(170, L"busy"); auto r = ExecuteInstall(b); Check(r.state == InstallState::Failed && !r.partial && b.calls.size() == 3); });
    }
    test("missing administrator rights stops before staging", [] { MockBackend b; b.failAt = InstallStage::Admin; auto r = ExecuteInstall(b); Check(r.state == InstallState::Failed && b.calls.size() == 1); });
    test("INF stage failure is not success", [] { MockBackend b; b.failAt = InstallStage::DriverStore; auto r = ExecuteInstall(b); Check(r.state == InstallState::Failed && r.partial); });
    test("registered node with failed binding is not success", [] { MockBackend b; b.failAt = InstallStage::Device; auto r = ExecuteInstall(b); Check(r.state == InstallState::Failed && r.stage == InstallStage::Device); });
    test("PnP Code 12 is fatal even with a reboot flag", [] { MockBackend b; b.device.problem = 12; b.device.reboot = true; auto r = ExecuteInstall(b); Check(r.state == InstallState::Failed && r.result.domain == L"PnP" && r.result.code == 12); });
    test("other PnP problem is fatal", [] { MockBackend b; b.device.problem = 10; Check(ExecuteInstall(b).state == InstallState::Failed); });
    test("missing driver response is not success", [] { MockBackend b; b.device.replied = false; Check(ExecuteInstall(b).state == InstallState::Failed); });
    test("process timeout is fatal and remains a process-domain error", [] { MockBackend b; b.failAt = InstallStage::DriverStore; b.failure = StageResult::Fail(1460, L"timeout", L"Process(pnputil)"); auto r = ExecuteInstall(b); Check(r.state == InstallState::Failed && r.result.code == 1460); });
    test("rollback failure cannot hide original install failure", [] { MockBackend b; b.failAt = InstallStage::Registration; b.rollbackOk = false; auto r = ExecuteInstall(b); Check(r.state == InstallState::Failed && r.partial && r.rollbackAttempted && !r.rollbackSucceeded && b.rollbacks == 1); });
    test("restart is distinct from verified ready", [] { MockBackend b; b.device.problem = 14; b.device.started = b.device.replied = false; auto r = ExecuteInstall(b); Check(r.state == InstallState::RestartRequired && r.restart); });
    test("unknown CM status never means healthy", [] { MockBackend b; b.device.statusRead = false; b.device.reboot = true; Check(ExecuteInstall(b).state == InstallState::Failed); });
    test("problem zero without DN_STARTED is not healthy", [] { MockBackend b; b.device.started = false; Check(ExecuteInstall(b).state == InstallState::Failed); });
    test("wrong bound INF is rejected", [] { MockBackend b; b.device.correctInf = false; Check(ExecuteInstall(b).state == InstallState::Failed); });
    test("incompatible protocol is fatal even after requesting restart", [] { MockBackend b; b.device.compatibleProtocol = false; b.device.reboot = true; Check(ExecuteInstall(b).state == InstallState::Failed); });
    test("malformed reply is rejected", [] { MockBackend b; b.device.validReply = false; Check(ExecuteInstall(b).state == InstallState::Failed); });
    test("copy failure restores old files and reports partial PnP changes", [] { MockBackend b; b.failAt = InstallStage::Files; b.failure = StageResult::Fail(12, L"copy failure", L"Win32"); auto r = ExecuteInstall(b); Check(r.state == InstallState::Failed && r.rollbackSucceeded && r.result.domain != L"PnP"); });
    test("driver replaying an old active split is not installation success", [] { MockBackend b; b.device.activeMonitors = 2; Check(ExecuteInstall(b).state == InstallState::Failed); });
    test("cancellation cannot return an uninitialized success", [] { MockBackend b; b.cancel = true; Check(ExecuteInstall(b).state == InstallState::Failed && b.calls.empty()); });
    test("every mandatory stage failure prevents ready", [] {
        for (int i = 0; i < 12; ++i) { MockBackend b; b.failAt = static_cast<InstallStage>(i); auto r = ExecuteInstall(b); Check(r.state == InstallState::Failed && r.stage == b.failAt && b.calls.size() == static_cast<size_t>(i + 1)); }
    });
    test("pending product deletion blocks before driver or file changes", [] {
        MockBackend b; b.failAt = InstallStage::Preflight; b.failure = StageResult::Fail(3010, L"pending removal");
        auto r = ExecuteInstall(b); Check(r.state == InstallState::Failed && !r.partial && !r.restart && b.calls.size() == 3);
    });
    test("pending deletion matches exact native path ignoring case", [] {
        std::wstring match;
        Check(HasPendingProductDeletion({L"\\??\\C:\\Program Files\\DesktopSplitter\\DesktopSplitterSetup.exe", L""},
            {L"c:\\program files\\desktopsplitter\\desktopsplittersetup.exe"}, match));
    });
    test("unrelated reboot operations do not block this product", [] {
        std::wstring match;
        Check(!HasPendingProductDeletion({L"C:\\Other\\setup.exe", L"", L"C:\\Product-old\\setup.exe", L""}, {L"C:\\Product\\setup.exe"}, match));
    });
    test("rename is not mistaken for pending deletion", [] {
        std::wstring match;
        Check(!HasPendingProductDeletion({L"C:\\Product\\setup.exe", L"C:\\Product\\old.exe"}, {L"C:\\Product\\setup.exe"}, match));
    });
    test("multiple deletion pairs include empty destinations", [] {
        std::wstring match;
        Check(HasPendingProductDeletion({L"C:\\Other", L"", L"C:\\Product", L""}, {L"C:\\Product"}, match));
    });
    test("uninstall restart instructions never request verification or Apply", [] {
        std::wstring text = RestartFinishInstructions(true);
        Check(text.find(L"verify-installed") == std::wstring::npos && text.find(L"Apply") == std::wstring::npos);
        Check(text.find(L"before installing") != std::wstring::npos);
        Check(std::wstring(RestartFinishTitle(true)).find(L"removal") != std::wstring::npos);
    });
    test("installation restart instructions retain runtime verification", [] {
        Check(std::wstring(RestartFinishInstructions(false)).find(L"/verify-installed") != std::wstring::npos);
    });
    test("raw pending registry pairs preserve empty delete destinations", [] {
        const wchar_t raw[] = L"C:\\other\0\0C:\\product\0\0";
        std::vector<std::wstring> pairs;
        Check(ParsePendingRenamePairs(std::vector<wchar_t>(raw, raw + sizeof(raw) / sizeof(wchar_t)), pairs));
        Check(pairs.size() == 4 && pairs[1].empty() && pairs[2] == L"C:\\product" && pairs[3].empty());
    });
    test("malformed pending operations cannot be interpreted as clean", [] {
        std::vector<std::wstring> pairs;
        Check(!ParsePendingRenamePairs({L'c', L':', L'\\', 0}, pairs));
        Check(!ParsePendingRenamePairs({0, L'x', 0}, pairs));
    });
    std::cout << count << " installer policy tests passed; no PnP or installation APIs invoked\n";
}
