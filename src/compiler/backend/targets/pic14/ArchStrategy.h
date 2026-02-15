#ifndef ARCHSTRATEGY_H
#define ARCHSTRATEGY_H

#include <string>
#include <vector>

// Abstract Strategy for Architecture-Specific Code Emission
class ArchStrategy {
 public:
  virtual ~ArchStrategy() = default;

  // Preamble
  // Emits global directives like LIST, INCLUDE, CONFIG
  virtual void emit_preamble() = 0;

  // Banking
  // Emits instructions to select the bank for 'addr'
  virtual void emit_bank_select(int bank) = 0;

  // Interrupt Service Routine (ISR)
  virtual void emit_context_save() = 0;
  virtual void emit_context_restore() = 0;
  virtual void emit_interrupt_return() = 0;
};

#endif  // ARCHSTRATEGY_H
