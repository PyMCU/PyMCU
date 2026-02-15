#include "PIC14EStrategy.h"

#include <format>
#include <string>

void PIC14EStrategy::emit_preamble() {
  // Enhanced Preamble (PIC14E)
  codegen->emit_raw(std::format("\tLIST P={}", codegen->config.target_chip));
  std::string chip_short = codegen->config.target_chip;
  if (chip_short.starts_with("pic")) {
    chip_short = chip_short.substr(3);
  }
  codegen->emit_raw(std::format("#include <p{}.inc>", chip_short));

  // ErrorLevel suppression often useful for generated code
  codegen->emit_raw("\terrorlevel -302");

  codegen->emit_config_directives();
}

void PIC14EStrategy::emit_bank_select(int bank) {
  // Optimization: Track current_bsr to avoid redundant MOVLB
  // PIC14E linear data memory is mapped, but SFRs are banked (0-63).
  // Each bank is 128 bytes.

  if (current_bsr != bank) {
    codegen->emit("MOVLB", std::to_string(bank));
    current_bsr = bank;
  }
}

void PIC14EStrategy::emit_context_save() {
  // Hardware Context Save (Shadow Registers)
  // No instructions needed.
  codegen->emit_comment("Context Save (Hardware Automatic)");
}

void PIC14EStrategy::emit_context_restore() {
  // Hardware Context Restore (Shadow Registers)
  // No instructions needed (RETFIE restores).
  codegen->emit_comment("Context Restore (Hardware Automatic)");
}

void PIC14EStrategy::emit_interrupt_return() { codegen->emit("RETFIE"); }
