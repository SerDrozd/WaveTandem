#include "syncear.h"
#include <iostream>
#include <string>
#include <vector>
#include <windows.h>

struct Endpoints {
    std::vector<std::wstring> active;
    bool valid = true;
};

void report(const wchar_t* id, const wchar_t* name, uint32_t state, uint32_t, uint32_t,
    uint32_t, uint32_t, void* context) {
    auto& result = *static_cast<Endpoints*>(context);
    if (!id || !*id || !name || !*name) result.valid = false;
    else if (state == 1) result.active.emplace_back(id);
}

int main(int argc, char**) {
    if (se_abi_version() != 1 || se_enumerate(nullptr, nullptr) >= 0) return 1;
    Endpoints endpoints;
    const auto result = se_enumerate(report, &endpoints);
    if (result < 0) {
        std::cerr << "Endpoint enumeration failed (HRESULT " << std::hex << result << ")\n";
        return 1;
    }
    std::cout << endpoints.active.size() << " active playback endpoints\n";
    if (argc == 1) return endpoints.valid ? 0 : 1;
    if (endpoints.active.size() < 2) {
        std::cerr << "Routing test requires two active endpoints and a playing source.\n";
        return 2;
    }
    se_session* session = nullptr;
    if (se_start(endpoints.active.front().c_str(), &session) < 0) return 1;
    const auto* source = endpoints.active.front().c_str();
    bool passed = se_output(session, source, 1, 0, 0) < 0;
    for (size_t i = 1; i < endpoints.active.size() && i <= 3; ++i)
        passed &= se_output(session, endpoints.active[i].c_str(), 1, 0.05f, 20) >= 0;
    Sleep(4000);
    se_status status{};
    passed &= se_source_status(session, &status) >= 0 && status.state == SE_STREAMING && status.timestamp_100ns > 0;
    for (size_t i = 1; i < endpoints.active.size() && i <= 3; ++i)
        passed &= se_output_status(session, endpoints.active[i].c_str(), &status) >= 0 && status.state == SE_STREAMING;
    se_stop(session);
    return passed ? 0 : 1;
}
