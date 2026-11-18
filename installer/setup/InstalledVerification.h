#pragma once
#include "InstallPolicy.h"
#include "InstallEngine.h"
#include "../common/DeviceNode.h"
#include <string>
namespace DeskSplit {
StageResult ProbeDriver(const DeviceNodeInfo& node, uint32_t& activeMonitors);
StageResult VerifyInstalledDriver(const wchar_t* expectedInf, bool reboot, IStepSink* sink);
StageResult VerifyProductFiles(const wchar_t* directory);
StageResult CheckUpgradeState(const wchar_t* directory, IStepSink* sink);
StageResult BackUpProductFiles(const wchar_t* directory, std::wstring& backup);
StageResult RestoreProductBackup(const wchar_t* directory, const std::wstring& backup, IStepSink* sink = nullptr);
StageResult VerifyInstalledProduct(IStepSink* sink);
DWORD BoundInfName(const DeviceNodeInfo& node, std::wstring& name);
}
