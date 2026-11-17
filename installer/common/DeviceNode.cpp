#include "DeviceNode.h"

#include <newdev.h>
#include <devguid.h>
#include <wchar.h>

#pragma comment(lib, "setupapi.lib")
#pragma comment(lib, "newdev.lib")
#pragma comment(lib, "cfgmgr32.lib")

namespace
{
    // Does this device info element carry the given hardware ID?
    bool HasHardwareId(HDEVINFO DevInfo, SP_DEVINFO_DATA* pDevInfoData, const wchar_t* HardwareId)
    {
        BYTE Buffer[2048] = {};
        DWORD RequiredSize = 0;
        DWORD PropertyType = 0;

        if (!SetupDiGetDeviceRegistryPropertyW(
                DevInfo,
                pDevInfoData,
                SPDRP_HARDWAREID,
                &PropertyType,
                Buffer,
                sizeof(Buffer),
                &RequiredSize))
        {
            return false;
        }

        // REG_MULTI_SZ: walk each ID in the list.
        for (const wchar_t* Id = reinterpret_cast<const wchar_t*>(Buffer);
             *Id != L'\0';
             Id += wcslen(Id) + 1)
        {
            if (_wcsicmp(Id, HardwareId) == 0)
            {
                return true;
            }
        }

        return false;
    }
}

namespace DeskSplit
{
    DWORD ListDeviceNodes(const wchar_t* HardwareId, bool PresentOnly, std::vector<DeviceNodeInfo>& Nodes)
    {
        Nodes.clear();
        HDEVINFO set = SetupDiGetClassDevsW(nullptr, L"ROOT", nullptr, DIGCF_ALLCLASSES | (PresentOnly ? DIGCF_PRESENT : 0));
        if (set == INVALID_HANDLE_VALUE) return GetLastError();
        DWORD result = ERROR_SUCCESS;
        for (DWORD index = 0;; ++index) {
            SP_DEVINFO_DATA data = {}; data.cbSize = sizeof(data);
            if (!SetupDiEnumDeviceInfo(set, index, &data)) {
                DWORD error = GetLastError(); if (error != ERROR_NO_MORE_ITEMS) result = error;
                break;
            }
            DWORD size = 0, type = 0;
            if (!SetupDiGetDeviceRegistryPropertyW(set, &data, SPDRP_HARDWAREID, &type, nullptr, 0, &size)) {
                DWORD error = GetLastError();
                if (error == ERROR_INVALID_DATA) continue;
                if (error != ERROR_INSUFFICIENT_BUFFER) { result = error; break; }
            }
            if (size < 2 * sizeof(wchar_t) || size > 65536 || size % sizeof(wchar_t)) { result = ERROR_INVALID_DATA; break; }
            std::vector<wchar_t> ids(size / sizeof(wchar_t) + 2, 0);
            if (!SetupDiGetDeviceRegistryPropertyW(set, &data, SPDRP_HARDWAREID, &type,
                    reinterpret_cast<BYTE*>(ids.data()), size, nullptr)) { result = GetLastError(); break; }
            if (type != REG_MULTI_SZ) { result = ERROR_INVALID_DATA; break; }
            bool match = false;
            for (const wchar_t* id = ids.data(); *id; id += wcslen(id) + 1)
                if (_wcsicmp(id, HardwareId) == 0) match = true;
            if (!match) continue;
            if (!IsEqualGUID(data.ClassGuid, GUID_DEVCLASS_DISPLAY)) { result = ERROR_INVALID_DATA; break; }
            DeviceNodeInfo info = {};
            if (!SetupDiGetDeviceInstanceIdW(set, &data, info.InstanceId, ARRAYSIZE(info.InstanceId), nullptr)) {
                result = GetLastError(); break;
            }
            info.DevInst = data.DevInst;
            info.StatusResult = CM_Get_DevNode_Status(&info.Status, &info.Problem, info.DevInst, 0);
            info.HasStatus = info.StatusResult == CR_SUCCESS;
            Nodes.push_back(info);
        }
        SetupDiDestroyDeviceInfoList(set);
        return result;
    }

    int CountDeviceNodes(const wchar_t* HardwareId)
    {
        std::vector<DeviceNodeInfo> nodes;
        DWORD error = ListDeviceNodes(HardwareId, false, nodes);
        if (error) { SetLastError(error); return -1; }
        return static_cast<int>(nodes.size());
    }

    bool FindDeviceNode(const wchar_t* HardwareId, DeviceNodeInfo* pInfo)
    {
        ZeroMemory(pInfo, sizeof(*pInfo));
        std::vector<DeviceNodeInfo> nodes;
        DWORD error = ListDeviceNodes(HardwareId, true, nodes);
        if (error || nodes.size() != 1) { SetLastError(error ? error : nodes.empty() ? ERROR_NOT_FOUND : ERROR_DUP_NAME); return false; }
        *pInfo = nodes[0];
        return true;
    }

    _Use_decl_annotations_
    DWORD CreateRootDeviceNode(
        const wchar_t* HardwareId,
        const wchar_t* HardwareIdMultiSz,
        DWORD HardwareIdMultiSzBytes,
        const wchar_t* Description,
        bool* pAlreadyExisted,
        bool* pNodeCreated,
        bool* pRebootRequired)
    {
        *pAlreadyExisted = false;
        *pNodeCreated = false;
        *pRebootRequired = false;

        const int Existing = CountDeviceNodes(HardwareId);
        if (Existing < 0) return GetLastError();
        if (Existing > 1) return ERROR_DUP_NAME;
        if (Existing == 1)
        {
            DeviceNodeInfo existing = {};
            if (!FindDeviceNode(HardwareId, &existing)) return GetLastError();
            *pAlreadyExisted = true;
            return ERROR_SUCCESS;
        }

        GUID ClassGuid = GUID_DEVCLASS_DISPLAY;

        HDEVINFO DevInfo = SetupDiCreateDeviceInfoList(&ClassGuid, nullptr);
        if (DevInfo == INVALID_HANDLE_VALUE)
        {
            return GetLastError();
        }

        DWORD Result = ERROR_SUCCESS;

        SP_DEVINFO_DATA DevInfoData = {};
        DevInfoData.cbSize = sizeof(DevInfoData);

        if (!SetupDiCreateDeviceInfoW(
                DevInfo,
                L"Display",
                &ClassGuid,
                Description,
                nullptr,
                DICD_GENERATE_ID,
                &DevInfoData))
        {
            Result = GetLastError();
        }
        else if (!SetupDiSetDeviceRegistryPropertyW(
                     DevInfo,
                     &DevInfoData,
                     SPDRP_HARDWAREID,
                     reinterpret_cast<const BYTE*>(HardwareIdMultiSz),
                     HardwareIdMultiSzBytes))
        {
            Result = GetLastError();
        }
        else if (!SetupDiCallClassInstaller(DIF_REGISTERDEVICE, DevInfo, &DevInfoData))
        {
            Result = GetLastError();
        }
        else
        {
            // The node now exists even if driver binding below fails.
            *pNodeCreated = true;

            BOOL Reboot = FALSE;
            if (!DiInstallDevice(nullptr, DevInfo, &DevInfoData, nullptr, 0, &Reboot))
            {
                Result = GetLastError();
            }

            *pRebootRequired = (Reboot != FALSE);
        }

        SetupDiDestroyDeviceInfoList(DevInfo);
        return Result;
    }

    _Use_decl_annotations_
    DWORD RemoveDeviceNodes(const wchar_t* HardwareId, int* pRemovedCount, bool* pRebootRequired)
    {
        *pRemovedCount = 0;
        *pRebootRequired = false;

        HDEVINFO DevInfo = SetupDiGetClassDevsW(nullptr, L"ROOT", nullptr, DIGCF_ALLCLASSES);
        if (DevInfo == INVALID_HANDLE_VALUE)
        {
            return GetLastError();
        }

        DWORD Result = ERROR_SUCCESS;

        SP_DEVINFO_DATA DevInfoData = {};
        DevInfoData.cbSize = sizeof(DevInfoData);

        for (DWORD Index = 0; SetupDiEnumDeviceInfo(DevInfo, Index, &DevInfoData); ++Index)
        {
            if (!HasHardwareId(DevInfo, &DevInfoData, HardwareId))
            {
                continue;
            }

            BOOL Reboot = FALSE;
            if (DiUninstallDevice(nullptr, DevInfo, &DevInfoData, 0, &Reboot))
            {
                ++(*pRemovedCount);
                if (Reboot)
                {
                    *pRebootRequired = true;
                }
            }
            else
            {
                Result = GetLastError();
            }
        }

        SetupDiDestroyDeviceInfoList(DevInfo);
        return Result;
    }

    const wchar_t* ProblemCodeText(ULONG Problem)
    {
        switch (Problem)
        {
        case 0:                       return L"none";
        case CM_PROB_NOT_CONFIGURED:  return L"CM_PROB_NOT_CONFIGURED - no driver bound to the device";
        case CM_PROB_FAILED_START:    return L"CM_PROB_FAILED_START - the driver failed to start";
        case CM_PROB_NORMAL_CONFLICT: return L"CM_PROB_NORMAL_CONFLICT - resource conflict";
        case CM_PROB_NEED_RESTART:    return L"CM_PROB_NEED_RESTART - a restart is required";
        case CM_PROB_REINSTALL:       return L"CM_PROB_REINSTALL - the device must be reinstalled";
        case CM_PROB_DISABLED:        return L"CM_PROB_DISABLED - the device is disabled";
        case CM_PROB_FAILED_INSTALL:  return L"CM_PROB_FAILED_INSTALL - driver installation failed";
        case CM_PROB_FAILED_ADD:      return L"CM_PROB_FAILED_ADD - a driver in the stack failed AddDevice";
        case CM_PROB_DRIVER_FAILED_LOAD:
                                      return L"CM_PROB_DRIVER_FAILED_LOAD - the driver could not be loaded "
                                             L"(unsigned/untrusted package, or a missing dependency)";
        case CM_PROB_DRIVER_BLOCKED:  return L"CM_PROB_DRIVER_BLOCKED - the driver is blocked from loading";
        case CM_PROB_UNSIGNED_DRIVER: return L"CM_PROB_UNSIGNED_DRIVER - the driver package is not trusted";
        default:                      return L"see CM_PROB_* in cfgmgr32.h";
        }
    }
}
