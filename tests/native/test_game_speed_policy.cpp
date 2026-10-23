#include "game_speed_policy.h"
#include <cassert>
#include <limits>
#include <iostream>
int main() {
  using namespace fable2::speed;
  assert(Normalize(1.0) == 1.0);
  assert(Normalize(std::numeric_limits<double>::quiet_NaN()) == 1.0);
  assert(Normalize(std::numeric_limits<double>::infinity()) == 1.0);
  assert(Normalize(-std::numeric_limits<double>::infinity()) == 1.0);
  assert(Normalize(-1) == 0.1 && Normalize(5) == 2.0);
  assert(Normalize(0.15) == 0.2 && Normalize(0.64) == 0.6);
  for(int i=1; i<=20; ++i) assert(Normalize(i/10.0) == i/10.0);
  std::cout << "Native game-speed normalization: all 20 values and edge cases passed\n";
}
