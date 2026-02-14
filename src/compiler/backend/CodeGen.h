#ifndef CODEGEN_H
#define CODEGEN_H

#pragma once
#include "ir/Tacky.h"
#include <iostream>

class CodeGen {
public:
  virtual ~CodeGen() = default;
  virtual void compile(const tacky::Program &program, std::ostream &os) = 0;

  // Interrupt Support
  virtual void emit_context_save() = 0;
  virtual void emit_context_restore() = 0;
  virtual void emit_interrupt_return() = 0;
};

#endif // CODEGEN_H