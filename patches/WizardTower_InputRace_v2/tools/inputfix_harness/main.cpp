#include <catch2/catch_test_macros.hpp>
#include <cstring>
int main(int argc, char** argv) {
  int failed = 0, run = 0;
  for (auto& t : HarnessTests()) {
    if (argc > 1 && !std::strstr(t.name, argv[1])) continue;
    ++run;
    try { t.fn(); std::printf("PASS  %s\n", t.name); }
    catch (std::exception& e) { ++failed; std::printf("FAIL  %s : %s\n", t.name, e.what()); }
    std::fflush(stdout);
  }
  std::printf("%d run, %d failed\n", run, failed);
  return failed ? 1 : 0;
}
