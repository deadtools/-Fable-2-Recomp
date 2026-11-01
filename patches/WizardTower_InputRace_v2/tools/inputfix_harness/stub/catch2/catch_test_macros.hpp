#pragma once
// Minimal Catch2 stand-in so the real test file compiles unchanged.
#include <cstdio>
#include <functional>
#include <stdexcept>
#include <string>
#include <vector>
struct HarnessTest { const char* name; void (*fn)(); };
inline std::vector<HarnessTest>& HarnessTests() { static std::vector<HarnessTest> t; return t; }
struct HarnessReg { HarnessReg(const char* n, void (*f)()) { HarnessTests().push_back({n, f}); } };
#define HCAT2(a, b) a##b
#define HCAT(a, b) HCAT2(a, b)
#define TEST_CASE(name, tags) \
  static void HCAT(htest_, __LINE__)(); \
  static HarnessReg HCAT(hreg_, __LINE__)(name, &HCAT(htest_, __LINE__)); \
  static void HCAT(htest_, __LINE__)()
#define REQUIRE(...) do { if (!(__VA_ARGS__)) throw std::runtime_error(std::string("REQUIRE failed: ") + #__VA_ARGS__ + " line " + std::to_string(__LINE__)); } while (0)
