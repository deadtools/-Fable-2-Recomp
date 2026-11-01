#pragma once
/**
 ******************************************************************************
 * Xenia : Xbox 360 Emulator Research Project                                 *
 ******************************************************************************
 * Copyright 2013 Ben Vanik. All rights reserved.                             *
 * Released under the BSD license - see LICENSE in the root for more details. *
 ******************************************************************************
 *
 * @modified    Tom Clay, 2026 - Adapted for ReXGlue runtime
 */

#include <memory>
#include <mutex>
#include <vector>

#include <rex/input/device_assignment.h>
#include <rex/input/input.h>
#include <rex/input/input_driver.h>
#include <rex/input/state_merge.h>
#include <rex/system/interfaces/input.h>

namespace rex::ui {
class Window;
}

namespace rex::input {

class InputSystem : public system::IInputSystem {
 public:
  explicit InputSystem(rex::ui::Window* window);
  ~InputSystem() override;

  rex::ui::Window* window() const { return window_; }

  X_STATUS Setup() override;
  void Shutdown() override;

  void AddDriver(std::unique_ptr<InputDriver> driver);
  void AttachWindow(rex::ui::Window* window);
  void SetActiveCallback(std::function<bool()> callback);

  /// Replaces any previous assignment. Call before the guest starts polling.
  void SetDeviceAssignment(std::unique_ptr<DeviceAssignment> assignment);

  X_RESULT GetCapabilities(uint32_t user_index, uint32_t flags, X_INPUT_CAPABILITIES* out_caps);
  X_RESULT GetState(uint32_t user_index, X_INPUT_STATE* out_state);
  X_RESULT SetState(uint32_t user_index, X_INPUT_VIBRATION* vibration);
  X_RESULT GetKeystroke(uint32_t user_index, uint32_t flags, X_INPUT_KEYSTROKE* out_keystroke);

 private:
  /// Re-enumerates every driver and notifies the assignment when the set
  /// changed.
  void RefreshDevices();
  InputDriver* DriverForDevice(DeviceId id);
  const DeviceInfo* DeviceInfoFor(DeviceId id) const;

  rex::ui::Window* window_ = nullptr;

  std::vector<std::unique_ptr<InputDriver>> drivers_;

  std::unique_ptr<DeviceAssignment> assignment_;
  ActiveDeviceTracker active_devices_;

  // Ordered by ordinal. Ordinals are never recycled, so unplugging pad one
  // does not renumber pad two.
  std::vector<DeviceInfo> devices_;
  std::vector<InputDriver*> device_owners_;

  // Guest threads (GameThread, "3D Engine", ...) call the XamInput* entry
  // points concurrently, and every one of them runs RefreshDevices(), which
  // rebuilds devices_ / device_owners_. Without this lock two threads free the
  // same DeviceInfo strings (heap double free in Wizard's Tower).
  // Guards drivers_, assignment_, active_devices_, devices_, device_owners_.
  // Recursive because driver/assignment callbacks are outside this file.
  // Kept as the LAST member so offsets of all existing members are unchanged;
  // instances are only created inside rexruntime (CreateDefaultInputSystem).
  mutable std::recursive_mutex state_mutex_;
};

/// Create a default InputSystem with SDL + NOP drivers.
/// In tool mode, only the NOP driver is added.
std::unique_ptr<InputSystem> CreateDefaultInputSystem(bool tool_mode);

}  // namespace rex::input
