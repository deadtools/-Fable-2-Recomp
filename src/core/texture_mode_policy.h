#pragma once
#include <string_view>

namespace fable2::textures {
// Pure descriptions only: this patch does not change the rendering hooks.
enum class Mode { GpuRealtime, CompatibilityReadback, DiagnosticOff };
constexpr Mode RequestedMode(bool realtime, bool readback) noexcept {
  return realtime ? Mode::GpuRealtime : readback ? Mode::CompatibilityReadback : Mode::DiagnosticOff;
}
constexpr std::string_view Name(Mode mode) noexcept {
  switch (mode) {
    case Mode::GpuRealtime: return "gpu-realtime";
    case Mode::CompatibilityReadback: return "compatibility-readback";
    case Mode::DiagnosticOff: return "diagnostic-off";
  }
  return "unknown";
}
// Keep the 1.5.0 seed semantics, including hand-edited legacy configs with both
// flags true. The launcher normalizes only when the user explicitly saves a mode.
constexpr std::string_view ReadbackSeed(bool readback) noexcept {
  return readback ? "0x12704000" : "";
}
}  // namespace fable2::textures
