/*
 * Copyright (c) 2024 Begeistert and/or its affiliates.
 *
 * This file is part of PyMCU.
 *
 * PyMCU is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * PyMCU is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with PyMCU.  If not, see <https://www.gnu.org/licenses/>.
 */

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

  // Invalidates internal bank tracking (e.g. after CALL)
  virtual void invalidate_bank() {};

  // Interrupt Service Routine (ISR)
  virtual void emit_context_save() = 0;
  virtual void emit_context_restore() = 0;
  virtual void emit_interrupt_return() = 0;
};

#endif  // ARCHSTRATEGY_H
