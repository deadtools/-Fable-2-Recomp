// Connect the launch-time option to the SDK's existing guest clock.
// Runtime::Setup resets its clock to 1x: apply AFTER setup, before guest launch.
#pragma once
#include <cmath>
#include <stdexcept>
#include <rex/chrono/clock.h>
#include <rex/cvar.h>
#include <rex/logging.h>
#include "fable2_config.h"
#include "game_speed_policy.h"

namespace fable2::speed {
inline void PrepareBeforeRuntime() {
  const double requested = Normalize(fable2::config::Get().game_speed);
  // Preserve the user's original runtime behavior completely at 1x.
  if (requested == kDefault) return;
  if (!rex::cvar::SetFlagByName("clock_no_scaling", "false") ||
      REXCVAR_GET(clock_no_scaling)) {
    throw std::runtime_error("Game speed requires clock_no_scaling=false.");
  }
}
inline void ApplyAfterRuntimeSetup() {
  const double requested = Normalize(fable2::config::Get().game_speed);
  // Keep this marker referenced by live code: the launcher checks the actual
  // executable, not a sidecar that could be copied beside an old executable.
  REXSYS_INFO("[game-speed] {} requested={}x", kCapability, requested);
  if (requested == kDefault) {
    REXSYS_INFO("[game-speed] 1.0x: original timing retained; no clock override");
    return;
  }
  if (REXCVAR_GET(clock_no_scaling)) {
    throw std::runtime_error("Clock scaling was disabled during runtime setup.");
  }
  rex::chrono::Clock::set_guest_time_scalar(requested);
  const auto ratio = rex::chrono::Clock::guest_tick_ratio();
  const auto guest_frequency = rex::chrono::Clock::guest_tick_frequency();
  const auto host_frequency = rex::chrono::Clock::QueryHostTickFrequency();
  if (!ratio.second || !guest_frequency || !host_frequency) {
    throw std::runtime_error("Invalid guest clock frequency or ratio.");
  }
  const double actual = (static_cast<double>(ratio.first) / ratio.second) *
      (static_cast<double>(host_frequency) / guest_frequency);
  if (std::abs(rex::chrono::Clock::guest_time_scalar() - requested) > 1e-9 ||
      std::abs(actual - requested) > 1e-7) {
    REXSYS_ERROR("[game-speed] requested={} actual={}; mismatched runtime DLL", requested, actual);
    throw std::runtime_error("Game-speed runtime mismatch. Run BUILD-GAME-SPEED-LOCAL.cmd "
                             "and install its matching rexruntime.dll.");
  }
  REXSYS_INFO("[game-speed] active={}x, ratio={}/{}, FPS settings unchanged", actual,
              ratio.first, ratio.second);
}
} // namespace fable2::speed
