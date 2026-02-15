#ifndef PIC14ESTRATEGY_H
#define PIC14ESTRATEGY_H

#include <variant>

#include "ArchStrategy.h"
#include "PIC14CodeGen.h"

// Enhanced PIC14E Architecture (e.g. PIC16F1xxxx)
class PIC14EStrategy : public ArchStrategy {
 public:
  explicit PIC14EStrategy(PIC14CodeGen* codegen) : codegen(codegen) {}

  void emit_preamble() override;
  void emit_bank_select(int bank) override;
  void emit_context_save() override;
  void emit_context_restore() override;
  void emit_interrupt_return() override;

 private:
  PIC14CodeGen* codegen;
  int current_bsr = -1;
};

#endif  // PIC14ESTRATEGY_H
