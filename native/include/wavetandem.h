#pragma once
#include <stdint.h>

#ifdef SE_EXPORTS
#define SE_API __declspec(dllexport)
#else
#define SE_API __declspec(dllimport)
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef struct se_session se_session;
typedef void (__cdecl *se_endpoint_callback)(const wchar_t* id, const wchar_t* name,
    uint32_t state, uint32_t form_factor, uint32_t is_default,
    uint32_t sample_rate, uint32_t channels, void* context);

enum se_stream_state { SE_DISABLED, SE_CONNECTING, SE_STREAMING, SE_RETRYING };

typedef struct se_status {
    uint32_t state;
    int32_t error;
    uint64_t underruns;
    uint64_t overruns;
    uint64_t reconnects;
    uint64_t position;
    uint64_t timestamp_100ns;
    float queue_ms;
    float latency_ms;
    float drift_ppm;
    uint32_t discontinuities;
} se_status;

// Status functions return HRESULT. Callback strings are valid only during the callback.
// Control calls for a session must be serialized by the caller.
SE_API uint32_t __cdecl se_abi_version(void);
SE_API int32_t __cdecl se_enumerate(se_endpoint_callback callback, void* context);
SE_API int32_t __cdecl se_start(const wchar_t* source, se_session** session);
SE_API void __cdecl se_stop(se_session* session);
// Gain is 0..1. Delay is 0..500 ms in addition to the software buffer target.
SE_API int32_t __cdecl se_output(se_session* session, const wchar_t* endpoint,
    uint32_t enabled, float gain, uint32_t delay_ms);
SE_API int32_t __cdecl se_source_status(se_session* session, se_status* status);
SE_API int32_t __cdecl se_output_status(se_session* session, const wchar_t* endpoint, se_status* status);
SE_API int32_t __cdecl se_set_paused(se_session* session, uint32_t paused);
SE_API int32_t __cdecl se_source_signal(se_session* session, uint32_t* present);
SE_API int32_t __cdecl se_test_output(se_session* session, const wchar_t* endpoint);

#ifdef __cplusplus
}
#endif
