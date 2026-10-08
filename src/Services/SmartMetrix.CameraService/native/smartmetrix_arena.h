#pragma once
#include <stddef.h>
#include <stdint.h>

#if defined(_WIN32)
#if defined(SMARTMETRIX_ARENA_EXPORTS)
#define SMARTMETRIX_ARENA_API __declspec(dllexport)
#else
#define SMARTMETRIX_ARENA_API __declspec(dllimport)
#endif
#else
#define SMARTMETRIX_ARENA_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef enum smartmetrix_arena_status {
    SMARTMETRIX_ARENA_OK = 0,
    SMARTMETRIX_ARENA_NOT_CONFIGURED = 1,
    SMARTMETRIX_ARENA_FRAME_MISSING = 2,
    SMARTMETRIX_ARENA_TIMEOUT = 3,
    SMARTMETRIX_ARENA_FAILURE = 255
} smartmetrix_arena_status;

#define SMARTMETRIX_ARENA_ABI_VERSION 2
typedef struct smartmetrix_arena_configuration {
    int32_t abi_version;
    double exposure_microseconds;
    int32_t required_camera_count;
    int32_t capture_timeout_milliseconds;
    const char* camera_a_serial_number;
    const char* camera_b_serial_number;
    const char* camera_c_serial_number;
    const char* trigger_source;
    const char* trigger_activation;
    const char* pixel_format;
} smartmetrix_arena_configuration;
typedef struct smartmetrix_arena_frame { const char* camera_id; int64_t frame_id; int64_t hardware_timestamp_nanoseconds; const uint8_t* data; size_t size; const char* content_type; } smartmetrix_arena_frame;
typedef struct smartmetrix_arena_frame_set { smartmetrix_arena_frame* frames; int32_t count; } smartmetrix_arena_frame_set;

SMARTMETRIX_ARENA_API smartmetrix_arena_status smartmetrix_arena_create(const smartmetrix_arena_configuration* configuration, void** context);
SMARTMETRIX_ARENA_API smartmetrix_arena_status smartmetrix_arena_capture(void* context, smartmetrix_arena_frame_set* frame_set);
SMARTMETRIX_ARENA_API void smartmetrix_arena_release_frame_set(void* context, smartmetrix_arena_frame_set* frame_set);
SMARTMETRIX_ARENA_API void smartmetrix_arena_destroy(void* context);
SMARTMETRIX_ARENA_API int32_t smartmetrix_arena_probe(void);

#ifdef __cplusplus
}
#endif
