#pragma once
#include <cstdint>
#include <rex/types.h>
namespace rex {
using X_STATUS = uint32_t;
using X_RESULT = uint32_t;
}
#define X_STATUS_SUCCESS ((X_STATUS)0)
#define X_ERROR_SUCCESS ((X_RESULT)0)
#define X_ERROR_DEVICE_NOT_CONNECTED ((X_RESULT)0x48F)
#define X_ERROR_EMPTY ((X_RESULT)0x10D2)
namespace rex::input { using rex::X_STATUS; using rex::X_RESULT; }
