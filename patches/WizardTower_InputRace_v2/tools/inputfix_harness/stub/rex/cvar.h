#pragma once
#include <string>
#include <initializer_list>
struct HarnessCvarS { std::string v; HarnessCvarS& allowed(std::initializer_list<const char*>) { return *this; } };
struct HarnessCvarB { bool v; };
#define REXCVAR_DEFINE_STRING(name, def, cat, desc) static HarnessCvarS cvar_##name{def}; static HarnessCvarS& cvar_ref_##name = cvar_##name
#define REXCVAR_DEFINE_BOOL(name, def, cat, desc) static HarnessCvarB cvar_##name{def}
#define REXCVAR_DECLARE(type, name)
#define REXCVAR_GET(name) (cvar_##name.v)
