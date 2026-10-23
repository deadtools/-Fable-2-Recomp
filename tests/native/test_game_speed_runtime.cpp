// Compiled by test_game_speed_v18.py against a controlled SDK test double.
// Tests our actual headers and call order, NOT the full Windows game.
#include <cassert>
#include <iostream>
#include "fable2_game_speed.h"
namespace fable2::config {
Values fixture;
const Values& Get() { return fixture; }
}
#define REX_QUERY_TIMEBASE() 1111ULL
#include "game_speed_timebase.h"
int main() {
  using namespace fable2::speed;
  using C = rex::chrono::Clock;
  auto& cfg = fable2::config::fixture;
  cfg.game_speed = 1;
  rex::cvar::disabled = true;
  PrepareBeforeRuntime(); ApplyAfterRuntimeSetup();
  assert(rex::cvar::disabled && C::calls == 0);
  assert(REX_QUERY_TIMEBASE() == 1111ULL);
  for(int i=1; i<=20; ++i) {
    cfg.game_speed = i/10.0;
    rex::cvar::disabled = false;
    C::scalar = 1.0;
    C::ratio = {5,1};
    PrepareBeforeRuntime(); ApplyAfterRuntimeSetup();
    assert(C::scalar == cfg.game_speed);
    assert(REX_QUERY_TIMEBASE() == (i==10 ? 1111ULL : 2222ULL));
  }
  cfg.game_speed = 0.6;
  C::bad_ratio = true;
  bool rejected = false;
  try { ApplyAfterRuntimeSetup(); } catch(const std::runtime_error&) { rejected=true; }
  assert(rejected); // old runtime's 0.6 -> 0.625 must never silently pass
  C::bad_ratio = false;
  rex::cvar::disabled = true;
  rejected=false;
  try { ApplyAfterRuntimeSetup(); } catch(const std::runtime_error&) { rejected=true; }
  assert(rejected);
  rex::cvar::setter_works = false;
  rejected=false;
  try { PrepareBeforeRuntime(); } catch(const std::runtime_error&) { rejected=true; }
  assert(rejected);
  std::cout << "Native startup/timebase routing and mismatched-runtime rejection passed\n";
}
