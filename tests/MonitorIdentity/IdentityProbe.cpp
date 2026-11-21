#include "../../shared/MonitorIdentityMatch.h"
#include <cassert>
#include <iostream>
using namespace ds;
int main() {
    MonitorIdentity saved;
    saved.stableMonitorId = L"MON-001"; saved.monitorDevicePath = L"panel-a";
    saved.manufacturer = L"SAM"; saved.productCode = 42; saved.serialNumber = 123;
    MonitorObservation live; live.identity = saved; live.gdiDeviceName = L"DISPLAY3"; live.active = true;
    assert(MatchMonitorIdentity(saved, {live}) == 0);
    live.gdiDeviceName = L"DISPLAY2";
    assert(MatchMonitorIdentity(saved, {live}) == 0);
    live.active = false; live.gdiDeviceName.clear();
    assert(MatchMonitorIdentity(saved, {live}) == 0);
    assert(MatchMonitorIdentity(saved, {}) == -1);
    live.identity.monitorDevicePath = L"new-port";
    assert(MatchMonitorIdentity(saved, {live}) == 0);
    auto duplicate = live; duplicate.identity.monitorDevicePath = L"other-port";
    assert(MatchMonitorIdentity(saved, {live, duplicate}) == -1);
    saved.serialNumber = 0;
    assert(MatchMonitorIdentity(saved, {live}) == -1);
    live.identity = saved;
    assert(MatchMonitorIdentity(saved, {live}) == 0);
    live.identity.productCode = 99;
    assert(MatchMonitorIdentity(saved, {live}) == -1);
    saved.segmentIndex = 0; saved.adapterPath = L"adapter-a"; saved.manufacturer = L"DSP";
    live.identity = saved; live.identity.monitorDevicePath = L"new-virtual";
    assert(MatchMonitorIdentity(saved, {live}) == 0);
    live.identity.adapterPath = L"adapter-b";
    assert(MatchMonitorIdentity(saved, {live}) == -1);
    std::cout << "11 native identity assertions passed\n";
}
