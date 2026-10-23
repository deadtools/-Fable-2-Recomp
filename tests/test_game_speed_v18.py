"""Source, policy and SDK-transform regression tests; no original game required.
C++ tests use a controlled clock double, not a Windows runtime/game execution.
"""
import importlib.util
from pathlib import Path
import os
import re
import shutil
import sys
import subprocess
import tempfile
import unittest
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("speed_sdk", ROOT / "tools/prepare_game_speed_sdk.py")
SDK = importlib.util.module_from_spec(spec)
spec.loader.exec_module(SDK)
FIXTURE = "void RecomputeGuestTickScalar() {\n  auto frac = std::make_pair(guest_tick_frequency_, Clock::QueryHostTickFrequency());\n" + SDK.OLD + "\n  reduce_fraction(frac);\n  guest_tick_ratio_ = frac;\n}\n"

FIXTURE = "#include <algorithm>\n" + FIXTURE + "\n" + "\n\n".join(SDK.OLD_DURATION_FUNCTIONS)
CHRONO_FIXTURE = "// guest-to-host\n" + SDK.CHRONO_OLD_GUEST + ";\n// host-to-guest\n" + SDK.CHRONO_OLD_HOST + ";\n"

class SdkTransformTests(unittest.TestCase):
    def test_applies_once(self):
        result=SDK.transform(FIXTURE)
        self.assertIn(SDK.NEW, result)
        self.assertEqual(SDK.transform(result),result)
        self.assertEqual(result.count(SDK.MARKER),1)
    def test_required_exports_added_once(self):
        original="EXPORTS\n?ExistingSymbol@@YAXXZ\n"
        patched=SDK.transform_exports(original)
        for name in SDK.SPEED_EXPORTS: self.assertEqual(patched.count(name),1)
        self.assertTrue(patched.startswith(original))
        self.assertEqual(SDK.transform_exports(patched),patched)
    def test_clock_conversion_direction(self):
        new=SDK.transform_chrono(CHRONO_FIXTURE)
        self.assertIn(SDK.CHRONO_NEW_GUEST,new)
        self.assertIn(SDK.CHRONO_NEW_HOST,new)
        self.assertEqual(SDK.transform_chrono(new),new)
    def test_unknown_source_rejected(self):
        with self.assertRaises(ValueError): SDK.transform(FIXTURE.replace("frac.first *= 10;", "frac.first *= 100;"))
    def test_partial_patch_rejected(self):
        with self.assertRaises(ValueError): SDK.transform(FIXTURE + SDK.MARKER)
    def test_crlf_and_backup(self):
        with tempfile.TemporaryDirectory() as d:
            path=Path(d)/"src/core/clock.cpp"; path.parent.mkdir(parents=True)
            exports=Path(d)/"src/system/rexruntime.def"; exports.parent.mkdir(parents=True); exports.write_text("EXPORTS\n",encoding="utf-8")
            chrono=Path(d)/"include/rex/chrono/chrono.h"; chrono.parent.mkdir(parents=True)
            chrono.write_bytes(CHRONO_FIXTURE.replace("\n","\r\n").encode())
            original=FIXTURE.replace("\n","\r\n").encode()
            path.write_bytes(original)
            self.assertTrue(SDK.apply(Path(d)))
            self.assertEqual(path.with_suffix(".cpp.before-speed-v18.bak").read_bytes(),original)
            result=path.read_bytes(); self.assertFalse(SDK.apply(Path(d)))
            self.assertEqual(path.read_bytes(),result)
            self.assertNotIn(b"\n",result.replace(b"\r\n",b""))
    def test_refusal_does_not_touch_file(self):
        with tempfile.TemporaryDirectory() as d:
            path=Path(d)/"src/core/clock.cpp"; path.parent.mkdir(parents=True)
            path.write_bytes(b"unknown implementation")
            with self.assertRaises(ValueError): SDK.apply(Path(d))
            self.assertEqual(path.read_bytes(),b"unknown implementation")
            self.assertFalse(path.with_suffix(".cpp.before-speed-v18.bak").exists())

class IntegrationSourceTests(unittest.TestCase):
    def test_slider_has_twenty_steps(self):
        tree=ET.parse(ROOT/"launcher/Fable2.Launcher/MainWindow.xaml")
        x="{http://schemas.microsoft.com/winfx/2006/xaml}Name"
        slider=next(e for e in tree.iter() if e.attrib.get(x)=="GameSpeedSlider")
        self.assertEqual((slider.get("Minimum"),slider.get("Maximum"),slider.get("Value")),("1","20","10"))
        self.assertEqual(slider.get("TickFrequency"),"1")
        self.assertEqual(slider.get("IsSnapToTickEnabled"),"True")
    def test_default_template_matches_source(self):
        cpp=(ROOT/"src/core/fable2_config.cpp").read_text(encoding="utf-8")
        template=cpp.split('R"TOML_EOF(',1)[1].split(')TOML_EOF"',1)[0]
        self.assertEqual(template,(ROOT/"config/fable2_config.toml").read_text(encoding="utf-8"))
        self.assertIn("[gameplay]\n",template)
        self.assertIn("game_speed = 1.0",template)
    def test_native_hook_order(self):
        text=(ROOT/"src/core/fable_2_app.h").read_text(encoding="utf-8")
        start=text.index("void OnPostSetup()")
        self.assertIn("ApplyAfterRuntimeSetup();",text[start:start+180])
        start=text.index("void OnPostInitLogging()")
        section=text[start:]
        self.assertLess(section.index("config::Load("),section.index("PrepareBeforeRuntime();"))
        cmake=(ROOT/"CMakeLists.txt").read_text(encoding="utf-8")
        self.assertLess(cmake.index("rexglue_setup_target(fable_2"),cmake.index("src/core/game_speed_timebase.h"))
        for name in ("frame/ProcessGameFrame_82276C30.cpp","vblank/FrameLimiterWait_82242628.cpp"):
            t=(ROOT/"src/core/hotfunc"/name).read_text(encoding="utf-8")
            self.assertLess(t.index('#include "fable_2_pch.h"'),t.index('#include "game_speed_timebase.h"'))
    def test_build_deploy_are_gated(self):
        cmd=(ROOT/"BUILD-GAME-SPEED-LOCAL.cmd").read_text(encoding="utf-8")
        self.assertLess(cmd.index('call "%~dp0build.cmd" -release fable_2'),cmd.index('deploy-game-speed-runtime.ps1'))
        self.assertLess(cmd.index('BUILD-LAUNCHER-LOCAL.cmd" --build-only'),cmd.index('deploy-game-speed-runtime.ps1'))
        self.assertIn('pause >nul',cmd)
        launcher=(ROOT/"BUILD-LAUNCHER-LOCAL.cmd").read_text(encoding="utf-8")
        self.assertIn('pause >nul',launcher)
        script=(ROOT/"tools/deploy-game-speed-runtime.ps1").read_text(encoding="utf-8")
        self.assertIn('Assert-GameClosed',script)
        self.assertNotIn('Stop-Process',script)
        self.assertIn('[IO.File]::Replace',script)
        self.assertIn('Get-FileHash',script)

def native_compiler():
    selected = os.environ.get("FABLE2_CXX")
    if selected:
        compiler = shutil.which(selected)
        if not compiler:
            raise RuntimeError("Selected FABLE2_CXX is missing; rerun BUILD-GAME-SPEED-LOCAL.cmd")
        return compiler
    return shutil.which("clang++" if os.name == "nt" else "g++") or shutil.which("clang++")


def native_flags():
    return ["--target=x86_64-pc-windows-msvc", "-fuse-ld=lld", "-fno-color-diagnostics"] if os.name == "nt" else []


class NativeTests(unittest.TestCase):
    def compile_run(self, source, extra_includes=(), executable_args=()):
        compiler=native_compiler()
        if not compiler: self.skipTest("g++/clang++ not available; native checks not executed")
        with tempfile.TemporaryDirectory() as d:
            binary=Path(d)/("speed-test.exe" if os.name=="nt" else "speed-test")
            args=[compiler,*native_flags(),"-std=c++20","-Wall","-Wextra","-Werror","-I",str(ROOT/"src/core")]
            for inc in extra_includes: args += ["-I",str(inc)]
            args += [str(source),"-o",str(binary)]
            result=subprocess.run(args,capture_output=True,text=True,errors="replace")
            self.assertEqual(result.returncode,0,
                "Compile failed: " + subprocess.list2cmdline(args) + "\n" + result.stdout + result.stderr)
            result=subprocess.run([str(binary),*map(str,executable_args)],capture_output=True,text=True,errors="replace")
            self.assertEqual(result.returncode,0,"Native test execution failed:\n" + result.stdout + result.stderr)
            self.assertIn("passed",result.stdout)
    def test_native_normalization(self):
        self.compile_run(ROOT/"tests/native/test_game_speed_policy.cpp")
    def test_exact_sdk_ratios(self):
        # Compile the very statements installed by the SDK patch, not a Python reimplementation.
        with tempfile.TemporaryDirectory() as d:
            file=Path(d)/"ratio.cpp"
            file.write_text(r"""#include <cassert>
#include <cstdint>
#include <numeric>
#include <utility>
#include <cmath>
#include <iostream>
std::pair<uint64_t,uint64_t> ratio(uint64_t guest, uint64_t host, double guest_time_scalar_) {
  auto frac=std::make_pair(guest,host);
"""+SDK.NEW+r"""
  auto divisor=std::gcd(frac.first,frac.second);
  return {frac.first/divisor,frac.second/divisor};
}
int main() {
 for(uint64_t host: {10000000ULL,1000000000ULL,3000000000ULL})
 for(int n=1;n<=20;++n) {
  auto r=ratio(50000000ULL,host,n/10.0);
  assert(r.first*host*10 == r.second*50000000ULL*n);
 }
 assert(ratio(50000000ULL,10000000ULL,0.6)==std::make_pair(3ULL,1ULL));
 std::cout << "All 20 exact guest-clock ratios at 3 host frequencies passed\n";
}
""",encoding="utf-8")
            self.compile_run(file)
    def test_signed_timeouts_and_inverse_scaling(self):
        with tempfile.TemporaryDirectory() as d:
            source=Path(d)/"waits.cpp"
            source.write_text(r"""#include <algorithm>
#include <cmath>
#include <cstdint>
#include <limits>
#include <cassert>
#include <iostream>
#define REXCVAR_GET(name) no_scaling
bool no_scaling=false;
double guest_time_scalar_=1;
struct Clock {
static uint32_t ScaleGuestDurationMillis(uint32_t);
static int64_t ScaleGuestDurationFileTime(int64_t);
static void ScaleGuestDurationTimeval(int32_t*,int32_t*);
static uint64_t QueryHostSystemTime(){return 130000000000000000ULL;}
static uint64_t QueryGuestSystemTime(){return 120000000000000000ULL;}
};
"""+"\n".join(SDK.NEW_DURATION_FUNCTIONS)+r"""
int main(){
 for(int i=1;i<=20;++i){
  guest_time_scalar_=i/10.0;
  assert(Clock::ScaleGuestDurationMillis(0)==0);
  assert(Clock::ScaleGuestDurationMillis(0xffffffffU)==0xffffffffU);
  assert(Clock::ScaleGuestDurationMillis(3000)==static_cast<uint32_t>(std::ceil(3000/(i/10.0))));
  assert(Clock::ScaleGuestDurationFileTime(-10000000)<0);
  assert(std::abs(static_cast<double>(Clock::ScaleGuestDurationFileTime(-10000000)) + 10000000/(i/10.0))<=1.0);
  int32_t sec=1,usec=500000; Clock::ScaleGuestDurationTimeval(&sec,&usec);
  assert(usec>=0 && usec<1000000);
  assert(std::abs(static_cast<double>(sec)*1000000+usec-1500000/(i/10.0))<=1.0);
 }
 guest_time_scalar_=0.5;
 assert(Clock::ScaleGuestDurationMillis(100)==200);
 assert(Clock::ScaleGuestDurationMillis(0xfffffffeU)==0xfffffffeU);
 assert(Clock::ScaleGuestDurationFileTime(std::numeric_limits<int64_t>::min())==std::numeric_limits<int64_t>::min());
 assert(Clock::ScaleGuestDurationFileTime(120000000010000000LL)==130000000020000000LL);
 guest_time_scalar_=2;
 assert(Clock::ScaleGuestDurationMillis(100)==50);
 no_scaling=true;
 assert(Clock::ScaleGuestDurationFileTime(-10000000)==-10000000);
 assert(Clock::ScaleGuestDurationMillis(100)==100);
 std::cout<<"Inverse waits, signed FILETIME, absolute deadlines and overflow boundaries passed\n";
}
""",encoding="utf-8")
            self.compile_run(source)

    def test_actual_native_config_loader(self):
        # Compile the real config loader and bundled TOML parser; only UI/logging are stubbed.
        compiler=native_compiler()
        if not compiler: self.skipTest("native compiler not available")
        with tempfile.TemporaryDirectory() as d:
            d=Path(d); (d/"rex/logging").mkdir(parents=True)
            (d/"rex/logging/macros.h").write_text(r"""#pragma once
namespace speed_test_log { template<typename... T> void Log(T&&...) {} }
#define REXSYS_INFO(...) ::speed_test_log::Log(__VA_ARGS__)
#define REXSYS_WARN(...) ::speed_test_log::Log(__VA_ARGS__)
#define REXSYS_ERROR(...) ::speed_test_log::Log(__VA_ARGS__)
""",encoding="utf-8")
            (d/"rex/system.h").write_text(r"""#pragma once
#include <string>
namespace rex { enum class SimpleMessageBoxType { Error };
inline void ShowSimpleMessageBox(SimpleMessageBoxType, const std::string&) {} }
""",encoding="utf-8")
            test=d/"config.cpp"
            test.write_text(r"""#include "fable2_config.h"
#include <cassert>
#include <fstream>
#include <iostream>
#include <string>
int main(int argc,char** argv) {
 assert(argc==2); auto path=std::filesystem::path(argv[1]);
 assert(fable2::config::Load(path));
 assert(fable2::config::Get().game_speed==1.0);
 for(int i=1;i<=20;++i) {
  {std::ofstream o(path); o<<"[gameplay]\ngame_speed="<<i/10.0<<"\n[input]\nkeyboard_gamepad_map='E:A'\n[patches]\nhigh_tick_rate=true\n";}
  assert(fable2::config::Load(path));
  assert(fable2::config::Get().game_speed==i/10.0);
  assert(fable2::config::Get().keyboard_gamepad_map=="E:A");
  assert(fable2::config::Get().high_tick_rate);
 }
 for(auto bad: {"'text'","true","nan","inf","-inf"}) {
  {std::ofstream o(path); o<<"[gameplay]\ngame_speed="<<bad<<"\n";}
  assert(fable2::config::Load(path)); assert(fable2::config::Get().game_speed==1.0);
 }
 {std::ofstream o(path); o<<"[gameplay]\ngame_speed=0.64\n";}
 assert(fable2::config::Load(path)); assert(fable2::config::Get().game_speed==0.6);
 {std::ofstream o(path); o<<"[gameplay]\ngame_speed=9\n";}
 assert(fable2::config::Load(path)); assert(fable2::config::Get().game_speed==2.0);
 {std::ofstream o(path); o<<"[gameplay]\ngame_speed=-5\n";}
 assert(fable2::config::Load(path)); assert(fable2::config::Get().game_speed==0.1);
 std::cout<<"Actual native TOML loader: all speeds, types, normalization and input preservation passed\n";
}
""",encoding="utf-8")
            exe=d/("config.exe" if os.name=="nt" else "config")
            result=subprocess.run([compiler,*native_flags(),"-std=c++23","-Wall","-Wextra","-Werror",
                "-I",str(d),"-I",str(ROOT/"src/core"),"-I",str(ROOT/"thirdparty/tomlplusplus/include"),
                str(test),str(ROOT/"src/core/fable2_config.cpp"),"-o",str(exe)],capture_output=True,text=True,errors="replace")
            self.assertEqual(result.returncode,0,result.stdout + result.stderr)
            result=subprocess.run([str(exe),str(d/"game.toml")],capture_output=True,text=True,errors="replace")
            self.assertEqual(result.returncode,0,result.stdout + result.stderr)
            self.assertIn("passed",result.stdout)

    def test_startup_and_timebase(self):
        with tempfile.TemporaryDirectory() as d:
            stub=Path(d)
            (stub/"rex/chrono").mkdir(parents=True)
            (stub/"rex/chrono/clock.h").write_text(r"""#pragma once
#include <cstdint>
#include <utility>
namespace rex::chrono {
struct Clock {
inline static double scalar=1.0;
inline static bool bad_ratio=false;
inline static int calls=0;
inline static std::pair<uint64_t,uint64_t> ratio={5,1};
static void set_guest_time_scalar(double v) {++calls; scalar=v; ratio={static_cast<uint64_t>(v*10+0.5),2}; if(bad_ratio) ratio={25,8};}
static double guest_time_scalar(){return scalar;}
static std::pair<uint64_t,uint64_t> guest_tick_ratio(){return ratio;}
static uint64_t guest_tick_frequency(){return 50000000;}
static uint64_t QueryHostTickFrequency(){return 10000000;}
static uint64_t QueryGuestTickCount(){return 2222;}
};
}
""",encoding="utf-8")
            (stub/"rex/cvar.h").write_text(r"""#pragma once
namespace rex::cvar {
inline bool disabled=false, setter_works=true;
inline bool SetFlagByName(const char*,const char*){if(!setter_works) return false; disabled=false; return true;}
}
#define REXCVAR_GET(name) (::rex::cvar::disabled)
""",encoding="utf-8")
            (stub/"rex/logging.h").write_text('#pragma once\n#define REXSYS_INFO(...) ((void)0)\n#define REXSYS_ERROR(...) ((void)0)\n',encoding="utf-8")
            self.compile_run(ROOT/"tests/native/test_game_speed_runtime.cpp",[stub])

class BuildPreparationTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        sys.path.insert(0,str(ROOT/"tools"))
        try:
            spec=importlib.util.spec_from_file_location("speed_build",ROOT/"tools/prepare_game_speed_build.py")
            cls.module=importlib.util.module_from_spec(spec)
            spec.loader.exec_module(cls.module)
        finally: sys.path.pop(0)
    def fixture(self, directory):
        root=Path(directory)/"project"; root.mkdir()
        game=Path(directory)/"game"; game.mkdir()
        (game/"default.xex").write_bytes(b"synthetic XEX fixture, NOT a game")
        catalogue={"codeGroup":"fixture","versions":[{"compatible":True,"codeGroup":"fixture","hash":self.module.digest(game/"default.xex")}]}
        return root,game,catalogue
    def test_stage_local_game_xex_once(self):
        with tempfile.TemporaryDirectory() as d:
            root,game,catalogue=self.fixture(d)
            self.assertTrue(self.module.stage_xex(root,game,catalogue))
            self.assertEqual((root/"default.xex").read_bytes(),(game/"default.xex").read_bytes())
            self.assertFalse(self.module.stage_xex(root,game,catalogue))
    def test_unknown_existing_project_xex_not_replaced(self):
        with tempfile.TemporaryDirectory() as d:
            root,game,catalogue=self.fixture(d)
            (root/"default.xex").write_bytes(b"different private XEX")
            with self.assertRaises(ValueError): self.module.stage_xex(root,game,catalogue)
            self.assertEqual((root/"default.xex").read_bytes(),b"different private XEX")
    def test_unknown_game_xex_not_staged(self):
        with tempfile.TemporaryDirectory() as d:
            root,game,catalogue=self.fixture(d)
            (game/"default.xex").write_bytes(b"unrecognized game XEX")
            with self.assertRaises(ValueError): self.module.stage_xex(root,game,catalogue)
            self.assertFalse((root/"default.xex").exists())
    def test_existing_sdk_reused_without_network_or_mutation(self):
        with tempfile.TemporaryDirectory() as d:
            root=Path(d); source=root/"thirdparty/rexglue-sdk"; source.mkdir(parents=True)
            (source/"CMakeLists.txt").write_text("preserve my existing checkout",encoding="utf-8")
            self.assertFalse(self.module.ensure_sdk(root))
            self.assertEqual((source/"CMakeLists.txt").read_text(),"preserve my existing checkout")
    def test_partial_sdk_never_deleted(self):
        with tempfile.TemporaryDirectory() as d:
            root=Path(d); source=root/"thirdparty/rexglue-sdk"; source.mkdir(parents=True)
            (source/"private.txt").write_text("keep",encoding="utf-8")
            with self.assertRaises(ValueError): self.module.ensure_sdk(root)
            self.assertEqual((source/"private.txt").read_text(),"keep")

if __name__ == "__main__": unittest.main()
