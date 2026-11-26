#include "wasapi.h"
#include <chrono>
#include <iostream>
#include <string_view>
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

bool wait_for(se_session* session, const wchar_t* endpoint, se_stream_state expected) {
    for (int i = 0; i < 30; ++i) {
        se_status status{};
        const auto result = endpoint ? se_output_status(session, endpoint, &status) : se_source_status(session, &status);
        if (result >= 0 && status.state == static_cast<uint32_t>(expected)) return true;
        Sleep(100);
    }
    return false;
}

bool capture_silent_packets(se_session* session, const std::wstring& id) {
    const se::ComApartment apartment;
    if (FAILED(apartment.result)) return false;
    se::Stream stimulus;
    if (FAILED(stimulus.open(id, false))) return false;
    se::ComPtr<IAudioRenderClient> sink;
    if (FAILED(stimulus.client->GetService(IID_PPV_ARGS(&sink)))) return false;
    BYTE* buffer = nullptr;
    if (FAILED(sink->GetBuffer(stimulus.buffer_frames, &buffer)) ||
        FAILED(sink->ReleaseBuffer(stimulus.buffer_frames, AUDCLNT_BUFFERFLAGS_SILENT)) ||
        FAILED(stimulus.client->Start())) return false;
    stimulus.started = true;
    const se::Event stop(CreateEventW(nullptr, TRUE, FALSE, nullptr));
    if (!stop) return false;
    const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(3);
    while (std::chrono::steady_clock::now() < deadline) {
        if (stimulus.wait(stop.get()) != S_OK) return false;
        UINT32 padding = 0;
        if (FAILED(stimulus.client->GetCurrentPadding(&padding)) || padding > stimulus.buffer_frames) return false;
        const auto frames = stimulus.buffer_frames - padding;
        if (frames && (FAILED(sink->GetBuffer(frames, &buffer)) ||
            FAILED(sink->ReleaseBuffer(frames, AUDCLNT_BUFFERFLAGS_SILENT)))) return false;
        se_status status{};
        if (se_source_status(session, &status) >= 0 && status.timestamp_100ns && status.position) return true;
    }
    return false;
}

int main(int argc, char** argv) {
    if (se_abi_version() != 1 || se_enumerate(nullptr, nullptr) >= 0) return 1;
    Endpoints endpoints;
    const auto result = se_enumerate(report, &endpoints);
    if (result < 0) {
        std::cerr << "Endpoint enumeration failed (HRESULT " << std::hex << result << ")\n";
        return 1;
    }
    std::cout << endpoints.active.size() << " active playback endpoints\n";
    if (!endpoints.valid) return 1;
    if (argc == 1) {
        se_session* session = nullptr;
        if (se_start(L"WaveTandem.test.absent.source", &session) < 0) return 1;
        const auto* absent = L"WaveTandem.test.absent.destination";
        const bool passed = se_output(session, absent, 1, 0, 501) < 0 &&
            se_output(session, absent, 1, 0, 0) >= 0 &&
            wait_for(session, nullptr, SE_RETRYING) &&
            wait_for(session, absent, SE_RETRYING) &&
            se_output(session, absent, 0, 0, 0) >= 0 &&
            wait_for(session, absent, SE_DISABLED);
        se_stop(session);
        return passed ? 0 : 1;
    }
    if (std::string_view(argv[1]) == "--capture") {
        if (endpoints.active.empty()) return 2;
        se_session* session = nullptr;
        if (se_start(endpoints.active.front().c_str(), &session) < 0) return 1;
        const auto passed = wait_for(session, nullptr, SE_STREAMING) &&
            capture_silent_packets(session, endpoints.active.front());
        se_stop(session);
        std::cout << (passed ? "Loopback captured silent WASAPI packets and stopped\n" :
            "Could not confirm loopback packet capture\n");
        return passed ? 0 : 1;
    }
    if (std::string_view(argv[1]) != "--routing") return 2;
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
