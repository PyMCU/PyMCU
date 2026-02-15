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

#ifndef PIOPEEPHOLE_H
#define PIOPEEPHOLE_H

#include <string>
#include <vector>

struct PIOAsmLine {
  enum Type { INSTRUCTION, LABEL, COMMENT, RAW, EMPTY };

  Type type;
  std::string label;
  std::string mnemonic;
  std::string op1;
  std::string op2;
  std::string op3;
  std::string content;
  int delay = 0;

  static PIOAsmLine Instruction(std::string m, std::string o1 = "",
                                std::string o2 = "", std::string o3 = "") {
    return {INSTRUCTION, "", m, o1, o2, o3, "", 0};
  }

  static PIOAsmLine Label(std::string l) {
    return {LABEL, l, "", "", "", "", "", 0};
  }

  static PIOAsmLine Comment(std::string c) {
    return {COMMENT, "", "", "", "", "", c, 0};
  }

  static PIOAsmLine Raw(std::string r) {
    return {RAW, "", "", "", "", "", r, 0};
  }

  static PIOAsmLine Empty() { return {EMPTY, "", "", "", "", "", "", 0}; }

  std::string to_string() const;
};

class PIOPeephole {
 public:
  static std::vector<PIOAsmLine> optimize(const std::vector<PIOAsmLine> &lines);
};

#endif  // PIOPEEPHOLE_H