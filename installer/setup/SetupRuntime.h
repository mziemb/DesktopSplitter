#pragma once
#include <windows.h>
namespace DeskSplit {
// -1: continue here; otherwise return this exit code to the caller.
int RelocateInstalledSetup(const wchar_t* arguments, bool quiet, const wchar_t* directoryOverride);
bool WaitForSetupParent(ULONG_PTR inheritedHandle);
class SetupInstanceLock {
    HANDLE handle_ = nullptr;
public:
    SetupInstanceLock();
    ~SetupInstanceLock();
    bool Acquired = false;
};
void ScheduleWorkerCleanup();
}
