#include <initguid.h>
#include "wasapi.h"
#include <functiondiscoverykeys_devpkey.h>
#include <ksmedia.h>
#include <bit>
#include <cstring>

namespace se {
namespace {
HRESULT make_enumerator(ComPtr<IMMDeviceEnumerator>& result) noexcept {
    return CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL,
        IID_PPV_ARGS(&result));
}

struct Property {
    PROPVARIANT value{};
    ~Property() { PropVariantClear(&value); }
};
}

HRESULT Format::set(const WAVEFORMATEX& wave) noexcept {
    rate = wave.nSamplesPerSec;
    channels = wave.nChannels;
    stride = wave.nBlockAlign;
    bytes_ = wave.wBitsPerSample / 8;
    valid_bits_ = wave.wBitsPerSample;
    float_ = wave.wFormatTag == WAVE_FORMAT_IEEE_FLOAT;
    bool pcm = wave.wFormatTag == WAVE_FORMAT_PCM;
    DWORD mask = 0;
    if (wave.wFormatTag == WAVE_FORMAT_EXTENSIBLE &&
        wave.cbSize >= sizeof(WAVEFORMATEXTENSIBLE) - sizeof(WAVEFORMATEX)) {
        const auto& extended = reinterpret_cast<const WAVEFORMATEXTENSIBLE&>(wave);
        float_ = extended.SubFormat == KSDATAFORMAT_SUBTYPE_IEEE_FLOAT;
        pcm = extended.SubFormat == KSDATAFORMAT_SUBTYPE_PCM;
        valid_bits_ = extended.Samples.wValidBitsPerSample;
        mask = extended.dwChannelMask;
    }
    const bool bit_depth = (float_ && bytes_ == 4 && valid_bits_ == 32) ||
        (pcm && bytes_ >= 1 && bytes_ <= 4 && valid_bits_ >= 2 && valid_bits_ <= wave.wBitsPerSample);
    if (!bit_depth || wave.wBitsPerSample != bytes_ * 8 || !channels || channels > 32 ||
        rate < 8000 || rate > 384000 || stride != channels * bytes_) return AUDCLNT_E_UNSUPPORTED_FORMAT;

    left_ = 0;
    right_ = channels == 1 ? 0 : 1;
    center_ = 32;
    if (std::popcount(mask) == static_cast<int>(channels)) {
        if ((mask & (SPEAKER_FRONT_LEFT | SPEAKER_FRONT_RIGHT)) ==
            (SPEAKER_FRONT_LEFT | SPEAKER_FRONT_RIGHT)) {
            left_ = static_cast<unsigned>(std::popcount(mask & (SPEAKER_FRONT_LEFT - 1u)));
            right_ = static_cast<unsigned>(std::popcount(mask & (SPEAKER_FRONT_RIGHT - 1u)));
        }
        if (mask & SPEAKER_FRONT_CENTER)
            center_ = static_cast<unsigned>(std::popcount(mask & (SPEAKER_FRONT_CENTER - 1u)));
    }
    if (center_ == left_ || center_ == right_) center_ = 32;
    return S_OK;
}

float Format::decode(const BYTE* sample) const noexcept {
    uint32_t bits = 0;
    std::memcpy(&bits, sample, bytes_);
    if (float_) return clean(std::bit_cast<float>(bits));
    if (bytes_ == 1) return static_cast<float>(static_cast<int>(bits) - 128) / 128.0f;
    const auto shifted = std::bit_cast<int32_t>(bits << (32u - bytes_ * 8u));
    return static_cast<float>(static_cast<double>(shifted) / 2147483648.0);
}

void Format::encode(BYTE* destination, float value) const noexcept {
    const float sample = clean(value);
    if (float_) {
        std::memcpy(destination, &sample, sizeof(sample));
        return;
    }
    const auto scale = int64_t{1} << (valid_bits_ - 1);
    const auto converted = std::clamp(std::llround(static_cast<double>(sample) * static_cast<double>(scale)),
        -scale, scale - 1);
    const auto aligned = static_cast<uint32_t>(converted) << (bytes_ * 8u - valid_bits_);
    const uint32_t bits = bytes_ == 1 ? aligned + 128u : aligned;
    std::memcpy(destination, &bits, bytes_);
}

Frame Format::read(const BYTE* frame) const noexcept {
    const float center = center_ < channels ? decode(frame + center_ * bytes_) * 0.7071f : 0.0f;
    return {clean(decode(frame + left_ * bytes_) + center), clean(decode(frame + right_ * bytes_) + center)};
}

void Format::write(BYTE* frame, Frame sample) const noexcept {
    for (uint32_t channel = 0; channel < channels; ++channel) {
        float value = 0;
        if (channels == 1) value = mono(sample);
        else if (channel == left_) value = sample.left;
        else if (channel == right_) value = sample.right;
        encode(frame + channel * bytes_, value);
    }
}

HRESULT Stream::open(const std::wstring& id, bool capture) {
    ComPtr<IMMDeviceEnumerator> enumerator;
    if (const auto hr = make_enumerator(enumerator); FAILED(hr)) return hr;
    ComPtr<IMMDevice> device;
    if (const auto hr = enumerator->GetDevice(id.c_str(), &device); FAILED(hr)) return hr;
    if (const auto hr = device->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr,
        reinterpret_cast<void**>(client.GetAddressOf())); FAILED(hr)) return hr;

    WAVEFORMATEX* mix = nullptr;
    if (const auto hr = client->GetMixFormat(&mix); FAILED(hr)) return hr;
    const std::unique_ptr<WAVEFORMATEX, TaskMemory> memory(mix);
    if (const auto hr = format.set(*mix); FAILED(hr)) return hr;
    const DWORD flags = AUDCLNT_STREAMFLAGS_EVENTCALLBACK |
        (capture ? AUDCLNT_STREAMFLAGS_LOOPBACK : AUDCLNT_STREAMFLAGS_NOPERSIST);
    if (const auto hr = client->Initialize(AUDCLNT_SHAREMODE_SHARED, flags, 0, 0, mix, nullptr);
        FAILED(hr)) return hr;
    ready.reset(CreateEventW(nullptr, FALSE, FALSE, nullptr));
    if (!ready) return HRESULT_FROM_WIN32(GetLastError());
    if (const auto hr = client->SetEventHandle(ready.get()); FAILED(hr)) return hr;
    if (const auto hr = client->GetBufferSize(&buffer_frames); FAILED(hr)) return hr;
    REFERENCE_TIME period = 0;
    if (SUCCEEDED(client->GetDevicePeriod(&period, nullptr)))
        period_frames = static_cast<uint32_t>(period * format.rate / 10'000'000);
    return S_OK;
}

HRESULT Stream::wait(HANDLE stop) const noexcept {
    HANDLE handles[] = {stop, ready.get()};
    switch (WaitForMultipleObjects(2, handles, FALSE, 1500)) {
    case WAIT_OBJECT_0: return S_FALSE;
    case WAIT_OBJECT_0 + 1: return S_OK;
    case WAIT_TIMEOUT: return HRESULT_FROM_WIN32(WAIT_TIMEOUT);
    default: return HRESULT_FROM_WIN32(GetLastError());
    }
}

Stream::~Stream() {
    if (started) client->Stop();
}

HRESULT enumerate(se_endpoint_callback callback, void* context) {
    if (!callback) return E_POINTER;
    ComApartment apartment;
    if (FAILED(apartment.result) && apartment.result != RPC_E_CHANGED_MODE) return apartment.result;
    ComPtr<IMMDeviceEnumerator> enumerator;
    if (const auto hr = make_enumerator(enumerator); FAILED(hr)) return hr;
    ComPtr<IMMDevice> default_device;
    LPWSTR default_id = nullptr;
    if (SUCCEEDED(enumerator->GetDefaultAudioEndpoint(eRender, eMultimedia, &default_device)))
        default_device->GetId(&default_id);
    const std::unique_ptr<wchar_t, TaskMemory> default_memory(default_id);
    ComPtr<IMMDeviceCollection> devices;
    const DWORD states = DEVICE_STATE_ACTIVE | DEVICE_STATE_DISABLED | DEVICE_STATE_UNPLUGGED | DEVICE_STATE_NOTPRESENT;
    if (const auto hr = enumerator->EnumAudioEndpoints(eRender, states, &devices); FAILED(hr)) return hr;
    UINT count = 0;
    if (const auto hr = devices->GetCount(&count); FAILED(hr)) return hr;
    for (UINT index = 0; index < count; ++index) {
        ComPtr<IMMDevice> device;
        LPWSTR id = nullptr;
        if (FAILED(devices->Item(index, &device)) || FAILED(device->GetId(&id))) continue;
        const std::unique_ptr<wchar_t, TaskMemory> id_memory(id);
        DWORD state = 0;
        device->GetState(&state);
        Property name, form;
        ComPtr<IPropertyStore> store;
        if (SUCCEEDED(device->OpenPropertyStore(STGM_READ, &store))) {
            store->GetValue(PKEY_Device_FriendlyName, &name.value);
            store->GetValue(PKEY_AudioEndpoint_FormFactor, &form.value);
        }
        uint32_t rate = 0, channels = 0;
        if (state == DEVICE_STATE_ACTIVE) {
            ComPtr<IAudioClient> client;
            WAVEFORMATEX* wave = nullptr;
            if (SUCCEEDED(device->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr,
                reinterpret_cast<void**>(client.GetAddressOf()))) && SUCCEEDED(client->GetMixFormat(&wave))) {
                const std::unique_ptr<WAVEFORMATEX, TaskMemory> memory(wave);
                rate = wave->nSamplesPerSec;
                channels = wave->nChannels;
            }
        }
        callback(id, name.value.vt == VT_LPWSTR ? name.value.pwszVal : id, state,
            form.value.vt == VT_UI4 ? form.value.ulVal : UINT32_MAX,
            default_id && wcscmp(id, default_id) == 0, rate, channels, context);
    }
    return S_OK;
}
}
