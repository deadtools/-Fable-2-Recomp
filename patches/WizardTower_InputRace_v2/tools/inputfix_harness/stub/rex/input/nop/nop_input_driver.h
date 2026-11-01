#pragma once
#include <rex/input/input_driver.h>
namespace rex::input::nop {
class NopInputDriver final : public InputDriver {
 public:
  NopInputDriver(rex::ui::Window* w, size_t z) : InputDriver(w, z) {}
  X_STATUS Setup() override { return X_STATUS_SUCCESS; }
  void EnumerateDevices(std::vector<DeviceInfo>&) override {}
  X_RESULT GetDeviceState(DeviceId, X_INPUT_STATE*) override { return X_ERROR_DEVICE_NOT_CONNECTED; }
  X_RESULT GetDeviceCapabilities(DeviceId, uint32_t, X_INPUT_CAPABILITIES*) override { return X_ERROR_DEVICE_NOT_CONNECTED; }
  X_RESULT SetDeviceVibration(DeviceId, X_INPUT_VIBRATION*) override { return X_ERROR_DEVICE_NOT_CONNECTED; }
  X_RESULT GetDeviceKeystroke(DeviceId, uint32_t, X_INPUT_KEYSTROKE*) override { return X_ERROR_DEVICE_NOT_CONNECTED; }
};
}
