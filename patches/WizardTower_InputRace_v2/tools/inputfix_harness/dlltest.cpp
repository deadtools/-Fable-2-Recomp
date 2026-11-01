// Loads a rexruntime.dll and hammers InputSystem::GetState/GetCapabilities/
// SetState/GetKeystroke (internal RVAs of build SHA256 1D8CBB87...) from 4 threads.
#include <windows.h>
#include <atomic>
#include <cstdio>
#include <cstdint>
#include <cstring>
#include <thread>
using CreateFn = void* (*)(void** out, bool tool_mode);
using GetStateFn = uint32_t (*)(void*, uint32_t, void*);
using SetStateFn = uint32_t (*)(void*, uint32_t, void*);
using Get4Fn = uint32_t (*)(void*, uint32_t, uint32_t, void*);
int main(int argc, char** argv) {
  if (argc < 3) { std::printf("usage: dlltest <rexruntime.dll> <deps dir> [iterations] [single]\n"); return 2; }
  SetDllDirectoryA(argv[2]);
  HMODULE m = LoadLibraryA(argv[1]);
  if (!m) { std::printf("LoadLibrary failed %lu\n", GetLastError()); return 3; }
  auto base = reinterpret_cast<uint8_t*>(m);
  auto create = reinterpret_cast<CreateFn>(GetProcAddress(m,
      "?CreateDefaultInputSystem@input@rex@@YA?AV?$unique_ptr@VInputSystem@input@rex@@U?$default_delete@VInputSystem@input@rex@@@std@@@std@@_N@Z"));
  if (!create) { std::printf("no CreateDefaultInputSystem\n"); return 4; }
  auto get_caps = reinterpret_cast<Get4Fn>(base + 0x129320);
  auto get_state = reinterpret_cast<GetStateFn>(base + 0x1294B0);
  auto set_state = reinterpret_cast<SetStateFn>(base + 0x1296B0);
  auto get_key = reinterpret_cast<Get4Fn>(base + 0x1298A0);
  void* sys = nullptr;
  create(&sys, true);  // tool mode: NOP driver only, no SDL/window
  if (!sys) { std::printf("create returned null\n"); return 5; }
  uint8_t st[16] = {}, caps[20] = {}, vib[4] = {}, ks[8] = {};
  uint32_t r1 = get_state(sys, 0, st), r2 = get_caps(sys, 0, 0, caps), r3 = set_state(sys, 0, vib), r4 = get_key(sys, 0, 0, ks);
  auto* dev = reinterpret_cast<uint8_t**>(static_cast<uint8_t*>(sys) + 0x50);
  std::printf("single-thread: GetState=0x%X Caps=0x%X SetState=0x%X Keystroke=0x%X devices=%zu\n",
              r1, r2, r3, r4, (size_t)((dev[1] - dev[0]) / 0x58));
  if (argc > 4) return 0;
  int n = argc > 3 ? std::atoi(argv[3]) : 200000;
  std::atomic<bool> go{false};
  auto w = [&](int k) {
    while (!go) std::this_thread::yield();
    uint8_t a[20] = {}, b[20] = {};
    for (int i = 0; i < n; ++i) {
      switch (k) {
        case 0: get_state(sys, 0, a); break;
        case 1: get_state(sys, 0, a); get_caps(sys, 0, 0, b); break;
        case 2: set_state(sys, 0, a); break;
        default: get_key(sys, 0, 0, a); break;
      }
    }
  };
  std::thread t0(w, 0), t1(w, 1), t2(w, 2), t3(w, 3);
  go = true;
  t0.join(); t1.join(); t2.join(); t3.join();
  std::printf("4 threads x %d iterations: OK, devices=%zu\n", n, (size_t)((dev[1] - dev[0]) / 0x58));
  return 0;
}
