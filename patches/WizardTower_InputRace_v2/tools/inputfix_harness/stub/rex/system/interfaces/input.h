#pragma once
#include <rex/kernel.h>
namespace rex::system {
class IInputSystem {
 public:
  virtual ~IInputSystem() = default;
  virtual X_STATUS Setup() = 0;
  virtual void Shutdown() = 0;
};
}
