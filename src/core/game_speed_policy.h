// Startup-only game-speed policy. No rendering or input remapping.
#pragma once
#include <algorithm>
#include <cmath>

namespace fable2::speed {
inline constexpr double kDefault = 1.0;
inline constexpr double kMin = 0.1;
inline constexpr double kMax = 2.0;
inline constexpr const char* kCapability = "FABLE2_GAME_SPEED_V18_GUEST_CLOCK";
inline double Normalize(double value) noexcept {
  if (!std::isfinite(value)) return kDefault;
  return std::round(std::clamp(value, kMin, kMax) * 10.0) / 10.0;
}
} // namespace fable2::speed
