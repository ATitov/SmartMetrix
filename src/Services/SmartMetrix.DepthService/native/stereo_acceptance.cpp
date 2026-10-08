#include "smartmetrix_stereo.h"
#include <algorithm>
#include <cmath>
#include <iostream>
#include <random>
#include <vector>

int main() {
    if (smartmetrix_stereo_probe() != SMARTMETRIX_STEREO_ABI_VERSION) {
        std::cerr << "OpenCV CUDA production build and a CUDA device are required\n";
        return 1;
    }
    constexpr int width = 384, height = 128, shift = 12;
    std::mt19937 random(1701);
    std::vector<uint8_t> left(width * height), right(width * height, 0);
    for (auto& value : left) value = static_cast<uint8_t>(30 + random() % 190);
    for (int y = 0; y < height; ++y) for (int x = 0; x < width - shift; ++x)
        right[y * width + x] = left[y * width + x + shift];
    smartmetrix_stereo_configuration config{SMARTMETRIX_STEREO_ABI_VERSION, 2, 64, 1.5f, 4, .1f, 10, "OpenCvCuda"};
    smartmetrix_stereo_output output{};
    if (smartmetrix_stereo_compute(&config, left.data(), right.data(), width, height, &output) != SMARTMETRIX_STEREO_OK) return 2;
    std::vector<float> disparities;
    int interior = 0, invalid_border = 0, border = 0;
    for (int y = 8; y < height - 8; ++y) for (int x = 0; x < width - 8; ++x) {
        const int i = y * width + x;
        const bool valid = output.confidence[i] > 0 && std::isfinite(output.disparity[i]);
        if (x < shift) { ++border; if (!valid) ++invalid_border; }
        if (x >= 80 && x < width - 32) { ++interior; if (valid) disparities.push_back(output.disparity[i]); }
    }
    smartmetrix_stereo_release(&output);
    if (disparities.empty()) return 3;
    std::sort(disparities.begin(), disparities.end());
    const float median = disparities[disparities.size() / 2];
    const double coverage = static_cast<double>(disparities.size()) / interior;
    const double rejected_border = static_cast<double>(invalid_border) / border;
    std::cout << "shift=" << shift << " median=" << median << " coverage=" << coverage << " occlusionRejected=" << rejected_border << '\n';
    return std::abs(median - shift) <= 1 && coverage >= .5 && rejected_border >= .9 ? 0 : 4;
}
