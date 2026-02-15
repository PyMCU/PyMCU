#ifndef PIC14STRATEGY_H
#define PIC14STRATEGY_H

#include "ArchStrategy.h"
#include "PIC14CodeGen.h"

// Legacy PIC14 Architecture (e.g. PIC16F84A, PIC16F877A)
class PIC14Strategy : public ArchStrategy {
 public:
  explicit PIC14Strategy(PIC14CodeGen* codegen) : codegen(codegen) {}

  void emit_preamble() override {
    // Legacy Preamble
    codegen->emit_raw(std::format("\tLIST P={}", codegen->config.target_chip));
    std::string chip_short = codegen->config.target_chip;
    if (chip_short.starts_with("pic")) {
      chip_short = chip_short.substr(3);
    }
    codegen->emit_raw(std::format("#include <p{}.inc>", chip_short));
    codegen->emit_config_directives();
  }

  void emit_bank_select(int bank) override {
    // Legacy BCF/BSF method
    if (bank & 1)
      codegen->emit("BSF", "STATUS", "5");
    else
      codegen->emit("BCF", "STATUS", "5");  // RP0
    if (bank & 2)
      codegen->emit("BSF", "STATUS", "6");
    else
      codegen->emit("BCF", "STATUS", "6");  // RP1
  }

  void emit_context_save() override {
    codegen->emit_comment("Context Save (Manual)");
    codegen->emit("MOVWF", "W_TEMP");
    codegen->emit("SWAPF", "STATUS", "W");
    codegen->emit("MOVWF", "STATUS_TEMP");
    // Force Bank 0 (Manual BCFs because we don't track bank in ISR entry yet)
    codegen->emit("BCF", "STATUS", "5");
    codegen->emit("BCF", "STATUS", "6");
  }

  void emit_context_restore() override {
    codegen->emit_comment("Context Restore (Manual)");
    codegen->emit("SWAPF", "STATUS_TEMP", "W");
    codegen->emit("MOVWF", "STATUS");
    codegen->emit("SWAPF", "W_TEMP", "F");
    codegen->emit("SWAPF", "W_TEMP", "W");
  }

  void emit_interrupt_return() override { codegen->emit("RETFIE"); }

 private:
  PIC14CodeGen* codegen;
};

#endif  // PIC14STRATEGY_H
