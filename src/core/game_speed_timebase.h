// Included AFTER the generated PPC PCH. The default uses the original SDK
// instruction implementation; custom speed explicitly shares the guest clock
// with kernel timers. No guessed guest addresses and no host QPC interception.
#pragma once
#include <cstdint>
#include <rex/chrono/clock.h>
#include "fable2_config.h"

#ifndef REX_QUERY_TIMEBASE
#error "game_speed_timebase.h must follow the generated fable_2_pch.h"
#endif
namespace fable2::speed {
inline uint64_t OriginalInstructionTimebase() { return REX_QUERY_TIMEBASE(); }
inline uint64_t QueryInstructionTimebase() {
  if (fable2::config::Get().game_speed == 1.0) return OriginalInstructionTimebase();
  return rex::chrono::Clock::QueryGuestTickCount();
}
}
#undef REX_QUERY_TIMEBASE
#define REX_QUERY_TIMEBASE() (::fable2::speed::QueryInstructionTimebase())
