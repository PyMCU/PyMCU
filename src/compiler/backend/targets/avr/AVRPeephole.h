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

#ifndef AVRPEEPHOLE_H
#define AVRPEEPHOLE_H

#include <string>
#include <vector>

struct AVRAsmLine {
  enum Type { INSTRUCTION, LABEL, COMMENT, RAW, EMPTY };

  Type type;
  std::string label;
  std::string mnemonic;
  std::string op1;
  std::string op2;
  std::string content;

  static AVRAsmLine Instruction(std::string m, std::string o1 = "",
                                std::string o2 = "") {
    return {INSTRUCTION, "", m, o1, o2, ""};
  }

  static AVRAsmLine Label(std::string l) { return {LABEL, l, "", "", "", ""}; }

  static AVRAsmLine Comment(std::string c) {
    return {COMMENT, "", "", "", "", c};
  }

  static AVRAsmLine Raw(std::string r) { return {RAW, "", "", "", "", r}; }

  static AVRAsmLine Empty() { return {EMPTY, "", "", "", "", ""}; }

  std::string to_string() const;
};

class AVRPeephole {
 public:
  static std::vector<AVRAsmLine> optimize(const std::vector<AVRAsmLine> &lines);
};

#endif  // AVRPEEPHOLE_H