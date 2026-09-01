#include "smartmetrix_stereo.h"
#include <algorithm>
#include <cmath>
#include <cstring>
#include <limits>
#include <memory>
#include <string>

#ifdef SMARTMETRIX_WITH_OPENCV_CUDA
#include <opencv2/calib3d.hpp>
#include <opencv2/core/cuda.hpp>
#include <opencv2/cudastereo.hpp>
#endif

extern "C" smartmetrix_stereo_status smartmetrix_stereo_compute(
    const smartmetrix_stereo_configuration* config, const uint8_t* left, const uint8_t* right,
    int32_t width, int32_t height, smartmetrix_stereo_output* output) {
    if (!output) return SMARTMETRIX_STEREO_INVALID_INPUT;
    *output = {};
    if (!config || config->abi_version != SMARTMETRIX_STEREO_ABI_VERSION || !left || !right ||
        width <= 0 || height <= 0 || config->minimum_disparity < 0 ||
        config->maximum_disparity <= config->minimum_disparity ||
        config->maximum_disparity - config->minimum_disparity > 256 || config->left_right_tolerance_pixels <= 0)
        return SMARTMETRIX_STEREO_INVALID_INPUT;
#ifndef SMARTMETRIX_WITH_OPENCV_CUDA
    return SMARTMETRIX_STEREO_NOT_CONFIGURED;
#else
    if (!config->provider || std::string(config->provider) != "OpenCvCuda")
        return SMARTMETRIX_STEREO_NOT_CONFIGURED;
    try {
        if (cv::cuda::getCudaEnabledDeviceCount() <= 0) return SMARTMETRIX_STEREO_NOT_CONFIGURED;
        const int range = config->maximum_disparity - config->minimum_disparity;
        const int disparities = range <= 64 ? 64 : range <= 128 ? 128 : 256;
        cv::Mat left_host(height, width, CV_8UC1, const_cast<uint8_t*>(left));
        cv::Mat right_host(height, width, CV_8UC1, const_cast<uint8_t*>(right));
        cv::cuda::GpuMat left_gpu, right_gpu, forward_gpu, reverse_gpu;
        left_gpu.upload(left_host); right_gpu.upload(right_host);
        auto matcher = cv::cuda::createStereoSGM(config->minimum_disparity, disparities,
            10, 120, config->uniqueness_ratio, cv::cuda::StereoSGM::MODE_HH4);
        matcher->compute(left_gpu, right_gpu, forward_gpu);
        matcher->compute(right_gpu, left_gpu, reverse_gpu);
        cv::Mat forward_fixed, reverse_fixed;
        forward_gpu.download(forward_fixed); reverse_gpu.download(reverse_fixed);
        cv::filterSpeckles(forward_fixed, 0, std::max(config->minimum_speckle_size, 0), 16);

        const int count = width * height;
        auto disparity = std::make_unique<float[]>(count);
        auto confidence = std::make_unique<float[]>(count);
        const float invalid = std::numeric_limits<float>::quiet_NaN();
        for (int y = 0; y < height; ++y) for (int x = 0; x < width; ++x) {
            const int index = y * width + x;
            const float d = forward_fixed.at<int16_t>(y, x) / 16.0f;
            const int rx = x - static_cast<int>(std::lround(d));
            if (d <= 0 || rx < 0 || rx >= width) { disparity[index] = invalid; confidence[index] = 0; continue; }
            const float reverse_d = std::abs(reverse_fixed.at<int16_t>(y, rx) / 16.0f);
            const float residual = std::abs(d - reverse_d);
            const float score = std::clamp(1.0f - residual / config->left_right_tolerance_pixels, 0.0f, 1.0f);
            if (residual > config->left_right_tolerance_pixels || score < config->minimum_confidence) {
                disparity[index] = invalid; confidence[index] = 0;
            } else { disparity[index] = d; confidence[index] = score; }
        }
        output->disparity = disparity.release(); output->confidence = confidence.release(); output->count = count;
        return SMARTMETRIX_STEREO_OK;
    } catch (const cv::Exception&) { return SMARTMETRIX_STEREO_CUDA_FAILURE; }
    catch (...) { return SMARTMETRIX_STEREO_FAILURE; }
#endif
}

extern "C" void smartmetrix_stereo_release(smartmetrix_stereo_output* output) {
    if (!output) return;
    delete[] output->disparity; delete[] output->confidence; *output = {};
}
