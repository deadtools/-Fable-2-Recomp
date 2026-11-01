#pragma once
#include <cstdint>
namespace rex {
// Harness stub: host-endian wrapper (byte order is irrelevant for this test).
template <typename T> struct be {
  T v{};
  be() = default;
  be(T x) : v(x) {}
  operator T() const { return v; }
  be& operator=(T x) { v = x; return *this; }
};
}
namespace rex::input { using rex::be; }
