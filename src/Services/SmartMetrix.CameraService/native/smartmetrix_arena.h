#pragma once
#include <stddef.h>
#include <stdint.h>

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

typedef struct smartmetrix_arena_configuration { double exposure_microseconds; int32_t required_camera_count; } smartmetrix_arena_configuration;
typedef struct smartmetrix_arena_frame { const char* camera_id; int64_t frame_id; int64_t hardware_timestamp_nanoseconds; const uint8_t* data; size_t size; const char* content_type; } smartmetrix_arena_frame;
typedef struct smartmetrix_arena_frame_set { smartmetrix_arena_frame* frames; int32_t count; } smartmetrix_arena_frame_set;

smartmetrix_arena_status smartmetrix_arena_create(const smartmetrix_arena_configuration* configuration, void** context);
smartmetrix_arena_status smartmetrix_arena_capture(void* context, smartmetrix_arena_frame_set* frame_set);
void smartmetrix_arena_release_frame_set(void* context, smartmetrix_arena_frame_set* frame_set);
void smartmetrix_arena_destroy(void* context);

#ifdef __cplusplus
}
#endif
