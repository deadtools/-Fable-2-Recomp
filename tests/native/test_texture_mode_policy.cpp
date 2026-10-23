#include "../../src/core/texture_mode_policy.h"
#include <iostream>
#include <stdexcept>

int main() {
  using namespace fable2::textures;
  static_assert(RequestedMode(true, false) == Mode::GpuRealtime);
  static_assert(RequestedMode(true, true) == Mode::GpuRealtime);
  static_assert(RequestedMode(false, true) == Mode::CompatibilityReadback);
  static_assert(RequestedMode(false, false) == Mode::DiagnosticOff);
  static_assert(ReadbackSeed(false).empty());
  static_assert(ReadbackSeed(true) == "0x12704000");
  static_assert(Name(Mode::CompatibilityReadback) == "compatibility-readback");
  static_assert(Name(Mode::GpuRealtime) == "gpu-realtime");
  static_assert(Name(Mode::DiagnosticOff) == "diagnostic-off");
  std::cout << "Texture mode descriptions and unchanged seed policy: 9 assertions passed.\n";
}
