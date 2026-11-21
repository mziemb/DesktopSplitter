#pragma once
#include "InstallEngine.h"
#include <string>
namespace DeskSplit {
class InstallLog : public IStepSink {
    IStepSink* sink_;
    HANDLE file_ = INVALID_HANDLE_VALUE;
    bool healthy_ = true;
    std::wstring path_, stage_;
    void Write(const wchar_t* kind, const wchar_t* text);
public:
    explicit InstallLog(IStepSink* sink);
    ~InstallLog();
    bool Healthy() const { return healthy_ && file_ != INVALID_HANDLE_VALUE; }
    const std::wstring& Path() const { return path_; }
    void OnStep(const wchar_t* text) override;
    void OnDetail(const wchar_t* text) override;
    void OnError(const wchar_t* text) override;
    void OnProgress(int percent) override { sink_->OnProgress(percent); }
    bool ConfirmCertificateTrust(const CertificateSummary& s) override {
        bool consent = sink_->ConfirmCertificateTrust(s);
        OnDetail(consent ? L"Explicit user consent to certificate-store changes granted" : L"Certificate-store changes declined");
        return consent;
    }
    bool IsCancelled() override { return sink_->IsCancelled(); }
};
}
