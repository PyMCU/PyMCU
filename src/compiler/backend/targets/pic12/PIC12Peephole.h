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

#ifndef PIC12PEEPHOLE_H
#define PIC12PEEPHOLE_H

#include <vector>

#include "PIC12CodeGen.h"

class PIC12Peephole {
 public:
  static std::vector<PIC12AsmLine> optimize(
      const std::vector<PIC12AsmLine> &lines);
};

#endif  // PIC12PEEPHOLE_H