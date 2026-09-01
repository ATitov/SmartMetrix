#include "smartmetrix_arena.h"

#include <array>
#include <cstring>
#include <memory>
#include <mutex>
#include <string>
#include <vector>

#ifdef SMARTMETRIX_WITH_ARENA
#include "ArenaApi.h"
#endif

namespace {
struct owned_frame {
    std::string camera_id;
    std::string content_type{"application/octet-stream"};
    int64_t frame_id{};
    int64_t timestamp_ns{};
    std::vector<uint8_t> data;
};

struct arena_context {
    std::mutex gate;
    int timeout_ms{};
    std::array<std::string, 3> serials;
    std::array<owned_frame, 3> owned;
    std::array<smartmetrix_arena_frame, 3> exported{};
#ifdef SMARTMETRIX_WITH_ARENA
    Arena::ISystem* system{};
    std::array<Arena::IDevice*, 3> devices{};
#endif
};

bool valid_text(const char* value) { return value != nullptr && value[0] != '\0'; }

#ifdef SMARTMETRIX_WITH_ARENA
void configure_device(Arena::IDevice* device, const smartmetrix_arena_configuration& config) {
    auto* nodes = device->GetNodeMap();
    Arena::SetNodeValue<GenICam::gcstring>(nodes, "TriggerMode", "Off");
    Arena::SetNodeValue<GenICam::gcstring>(nodes, "AcquisitionMode", "Continuous");
    Arena::SetNodeValue<GenICam::gcstring>(nodes, "PixelFormat", config.pixel_format);
    Arena::SetNodeValue<GenICam::gcstring>(nodes, "ExposureAuto", "Off");
    Arena::SetNodeValue<double>(nodes, "ExposureTime", config.exposure_microseconds);
    Arena::SetNodeValue<GenICam::gcstring>(nodes, "TriggerSelector", "FrameStart");
    Arena::SetNodeValue<GenICam::gcstring>(nodes, "TriggerSource", config.trigger_source);
    Arena::SetNodeValue<GenICam::gcstring>(nodes, "TriggerActivation", config.trigger_activation);
    Arena::SetNodeValue<GenICam::gcstring>(nodes, "TriggerMode", "On");
    Arena::SetNodeValue<bool>(device->GetTLStreamNodeMap(), "StreamAutoNegotiatePacketSize", true);
    Arena::SetNodeValue<bool>(device->GetTLStreamNodeMap(), "StreamPacketResendEnable", true);
    Arena::SetNodeValue<GenICam::gcstring>(device->GetTLStreamNodeMap(), "StreamBufferHandlingMode", "NewestOnly");
}

void destroy_sdk(arena_context& context) noexcept {
    for (auto*& device : context.devices) {
        if (!device) continue;
        try { device->StopStream(); } catch (...) {}
        try { context.system->DestroyDevice(device); } catch (...) {}
        device = nullptr;
    }
    if (context.system) {
        try { Arena::CloseSystem(context.system); } catch (...) {}
        context.system = nullptr;
    }
}
#endif
}

extern "C" smartmetrix_arena_status smartmetrix_arena_create(
    const smartmetrix_arena_configuration* config, void** output) {
    if (!output) return SMARTMETRIX_ARENA_FAILURE;
    *output = nullptr;
    if (!config || config->abi_version != SMARTMETRIX_ARENA_ABI_VERSION ||
        config->required_camera_count != 3 || config->capture_timeout_milliseconds <= 0 ||
        !valid_text(config->camera_a_serial_number) || !valid_text(config->camera_b_serial_number) ||
        !valid_text(config->camera_c_serial_number) || !valid_text(config->trigger_source) ||
        !valid_text(config->trigger_activation) || !valid_text(config->pixel_format))
        return SMARTMETRIX_ARENA_NOT_CONFIGURED;

#ifndef SMARTMETRIX_WITH_ARENA
    return SMARTMETRIX_ARENA_NOT_CONFIGURED;
#else
    auto context = std::make_unique<arena_context>();
    context->timeout_ms = config->capture_timeout_milliseconds;
    context->serials = {config->camera_a_serial_number, config->camera_b_serial_number, config->camera_c_serial_number};
    if (context->serials[0] == context->serials[1] || context->serials[0] == context->serials[2] || context->serials[1] == context->serials[2])
        return SMARTMETRIX_ARENA_NOT_CONFIGURED;
    try {
        context->system = Arena::OpenSystem();
        context->system->UpdateDevices(3000);
        const auto infos = context->system->GetDevices();
        for (size_t channel = 0; channel < context->devices.size(); ++channel) {
            for (const auto& info : infos) {
                const std::string serial = info.SerialNumber().c_str();
                if (serial == context->serials[channel]) {
                    context->devices[channel] = context->system->CreateDevice(info);
                    break;
                }
            }
            if (!context->devices[channel]) { destroy_sdk(*context); return SMARTMETRIX_ARENA_NOT_CONFIGURED; }
            configure_device(context->devices[channel], *config);
        }
        for (auto* device : context->devices) device->StartStream(8);
        *output = context.release();
        return SMARTMETRIX_ARENA_OK;
    } catch (...) {
        destroy_sdk(*context);
        return SMARTMETRIX_ARENA_NOT_CONFIGURED;
    }
#endif
}

extern "C" smartmetrix_arena_status smartmetrix_arena_capture(void* value, smartmetrix_arena_frame_set* set) {
    if (!value || !set) return SMARTMETRIX_ARENA_FAILURE;
    set->frames = nullptr; set->count = 0;
#ifndef SMARTMETRIX_WITH_ARENA
    return SMARTMETRIX_ARENA_NOT_CONFIGURED;
#else
    auto& context = *static_cast<arena_context*>(value);
    std::lock_guard<std::mutex> lock(context.gate);
    std::array<Arena::IImage*, 3> images{};
    try {
        for (size_t index = 0; index < images.size(); ++index) {
            images[index] = context.devices[index]->GetImage(context.timeout_ms);
            if (!images[index] || images[index]->IsIncomplete()) {
                for (size_t n = 0; n <= index; ++n) if (images[n]) context.devices[n]->RequeueBuffer(images[n]);
                return SMARTMETRIX_ARENA_FRAME_MISSING;
            }
        }
        for (size_t index = 0; index < images.size(); ++index) {
            auto& owned = context.owned[index];
            owned.camera_id.assign(1, static_cast<char>('A' + index));
            owned.frame_id = static_cast<int64_t>(images[index]->GetFrameId());
            owned.timestamp_ns = static_cast<int64_t>(images[index]->GetTimestampNs());
            const auto size = images[index]->GetSizeFilled();
            const auto* data = static_cast<const uint8_t*>(images[index]->GetData());
            owned.data.assign(data, data + size);
            context.devices[index]->RequeueBuffer(images[index]); images[index] = nullptr;
            context.exported[index] = {owned.camera_id.c_str(), owned.frame_id, owned.timestamp_ns,
                owned.data.data(), owned.data.size(), owned.content_type.c_str()};
        }
        set->frames = context.exported.data(); set->count = 3;
        return SMARTMETRIX_ARENA_OK;
    } catch (...) {
        for (size_t index = 0; index < images.size(); ++index)
            if (images[index]) try { context.devices[index]->RequeueBuffer(images[index]); } catch (...) {}
        return SMARTMETRIX_ARENA_TIMEOUT;
    }
#endif
}

extern "C" void smartmetrix_arena_release_frame_set(void*, smartmetrix_arena_frame_set* set) {
    if (set) { set->frames = nullptr; set->count = 0; }
}

extern "C" void smartmetrix_arena_destroy(void* value) {
    if (!value) return;
    auto* context = static_cast<arena_context*>(value);
#ifdef SMARTMETRIX_WITH_ARENA
    std::lock_guard<std::mutex> lock(context->gate);
    destroy_sdk(*context);
#endif
    delete context;
}
