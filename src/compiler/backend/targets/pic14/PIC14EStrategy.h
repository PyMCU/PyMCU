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
