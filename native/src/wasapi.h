#pragma once
#include "dsp.h"
#include "wavetandem.h"
#include <windows.h>
#include <audioclient.h>
#include <mmdeviceapi.h>
#include <wrl/client.h>
#include <memory>
#include <string>

namespace se {
using Microsoft::WRL::ComPtr;

struct ComApartment {
    HRESULT result = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    ~ComApartment() { if (SUCCEEDED(result)) CoUninitialize(); }
};

struct HandleCloser {
    void operator()(void* handle) const noexcept { CloseHandle(handle); }
};
using Event = std::unique_ptr<void, HandleCloser>;

struct TaskMemory {
    void operator()(void* memory) const noexcept { CoTaskMemFree(memory); }
};

class Format {
public:
    HRESULT set(const WAVEFORMATEX& wave) noexcept;
    Frame read(const BYTE* frame) const noexcept;
    void write(BYTE* frame, Frame sample) const noexcept;
    uint32_t rate = 0;
    uint32_t channels = 0;
    uint32_t stride = 0;
private:
    float decode(const BYTE* sample) const noexcept;
    void encode(BYTE* destination, float value) const noexcept;
    unsigned bytes_ = 0;
    unsigned valid_bits_ = 0;
    unsigned left_ = 0;
    unsigned right_ = 1;
    unsigned center_ = 32;
    bool float_ = false;
};

struct Stream {
    HRESULT open(const std::wstring& id, bool capture);
    HRESULT wait(HANDLE stop) const noexcept;
    ~Stream();
    Event ready;
    ComPtr<IAudioClient> client;
    Format format;
    uint32_t period_frames = 0;
    uint32_t buffer_frames = 0;
    bool started = false;
};

HRESULT enumerate(se_endpoint_callback callback, void* context);
}
