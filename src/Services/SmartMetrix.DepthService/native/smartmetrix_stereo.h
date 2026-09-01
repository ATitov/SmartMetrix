#pragma once
#include <stdint.h>

#if defined(_WIN32)
#define SMARTMETRIX_STEREO_API __declspec(dllexport)
#else
#define SMARTMETRIX_STEREO_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

#define SMARTMETRIX_STEREO_ABI_VERSION 1
typedef enum smartmetrix_stereo_status {
    SMARTMETRIX_STEREO_OK = 0,
    SMARTMETRIX_STEREO_NOT_CONFIGURED = 1,
    SMARTMETRIX_STEREO_INVALID_INPUT = 2,
    SMARTMETRIX_STEREO_CUDA_FAILURE = 3,
    SMARTMETRIX_STEREO_FAILURE = 255
} smartmetrix_stereo_status;

typedef struct smartmetrix_stereo_configuration {
    int32_t abi_version;
    int32_t minimum_disparity;
    int32_t maximum_disparity;
    float left_right_tolerance_pixels;
    int32_t minimum_speckle_size;
    float minimum_confidence;
    int32_t uniqueness_ratio;
    const char* provider;
} smartmetrix_stereo_configuration;

typedef struct smartmetrix_stereo_output {
    float* disparity;
    float* confidence;
    int32_t count;
} smartmetrix_stereo_output;

SMARTMETRIX_STEREO_API smartmetrix_stereo_status smartmetrix_stereo_compute(
    const smartmetrix_stereo_configuration* configuration,
    const uint8_t* left, const uint8_t* right, int32_t width, int32_t height,
    smartmetrix_stereo_output* output);
SMARTMETRIX_STEREO_API void smartmetrix_stereo_release(smartmetrix_stereo_output* output);

#ifdef __cplusplus
}
#endif
