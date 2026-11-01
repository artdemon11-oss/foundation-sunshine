/**
 * @file src/platform/windows/ds5/ds5_sidecar_client.h
 * @brief Narrow named-pipe client for the optional DualSense sidecar.
 */
#pragma once

#include <cstdint>
#include <filesystem>
#include <memory>
#include <optional>

#include <boost/atomic.hpp>

#include "src/platform/common.h"

namespace platf::ds5 {
  /** Cached result for input hot paths; refreshed at lifecycle boundaries. */
  bool component_available() noexcept;

  /** Revalidate the fixed component path, manifest, and executable digest. */
  bool refresh_component_availability() noexcept;

  /** Return the validated executable path used by virtual-device-host clients. */
  std::optional<std::filesystem::path> trusted_component_path();

  /** Cached "switch-pro" feature of the validated component; refreshed with availability. */
  bool component_supports_switch_pro() noexcept;

  /** Host-side Switch Pro input policy, fixed when the controller is allocated. */
  struct switch_pro_settings_t {
    bool label_layout = false;  ///< Swap A/B and X/Y so Nintendo labels match the client's.
    int trigger_threshold = 64;  ///< ZL/ZR press level; release is 16 lower.
  };

  /** Swap Moonlight A↔B and X↔Y when the label layout is selected. */
  std::uint32_t switch_pro_buttons(std::uint32_t button_flags, bool label_layout) noexcept;

  /** Debounce an analog trigger into 0 or 255 with a 16-step release hysteresis. */
  std::uint8_t switch_pro_trigger(std::uint8_t value, std::uint8_t previous, int threshold) noexcept;

  class sidecar_client_t {
  public:
    sidecar_client_t();
    ~sidecar_client_t();

    sidecar_client_t(const sidecar_client_t &) = delete;
    sidecar_client_t &operator=(const sidecar_client_t &) = delete;

    bool configured() const;
    bool owns(int global_index) const;
    /** @brief The single HIDMaestro slot already holds a DualSense or Switch Pro. */
    bool slot_in_use() const;
    bool owns_switch_pro(int global_index) const;
    /** @brief Check whether negotiated audio haptics remain online without accessing the replaceable implementation. */
    bool audio_haptics_active() const noexcept;
    int alloc(const gamepad_id_t &id, feedback_queue_t feedback_queue, bool audio_haptics,
              bool genshin_compatibility = false);
    /** @brief Attach a Switch Pro Controller; fails when the slot is busy or the sidecar lacks the profile. */
    int alloc_switch_pro(const gamepad_id_t &id, feedback_queue_t feedback_queue,
                         const switch_pro_settings_t &settings);
    void free(int global_index);
    void submit_input(int global_index, const gamepad_state_t &state);
    void submit_touch(const gamepad_touch_t &touch);
    void submit_motion(const gamepad_motion_t &motion);
    void submit_battery(const gamepad_battery_t &battery);

  private:
    struct impl_t;

    // 必须比 _impl 后析构，保证 reader 退出前发布目标始终有效。
    // 分配、释放及输入提交仍在输入线程串行执行，控制线程只读取这个原子状态。
    boost::atomic<bool> published_audio_haptics { false };
    std::unique_ptr<impl_t> _impl;
  };
}  // namespace platf::ds5
