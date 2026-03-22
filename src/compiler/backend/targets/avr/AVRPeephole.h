/*
 * -----------------------------------------------------------------------------
 * Whipsnake Compiler (whipc)
 * Copyright (C) 2026 Ivan Montiel Cardona and the Whipsnake Project Authors
 *
 * SPDX-License-Identifier: MIT
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction, including without limitation the rights
 * to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
 * copies of the Software, and to permit persons to whom the Software is
 * furnished to do so, subject to the following conditions:
 *
 * The above copyright notice and this permission notice shall be included in
 * all copies or substantial portions of the Software.
 *
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 * IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 * FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 * AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 * LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
 * OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
 * THE SOFTWARE.
 * -----------------------------------------------------------------------------
 * SAFETY WARNING / HIGH RISK ACTIVITIES:
 * THE SOFTWARE IS NOT DESIGNED, MANUFACTURED, OR INTENDED FOR USE IN HAZARDOUS
 * ENVIRONMENTS REQUIRING FAIL-SAFE PERFORMANCE, SUCH AS IN THE OPERATION OF
 * NUCLEAR FACILITIES, AIRCRAFT NAVIGATION OR COMMUNICATION SYSTEMS, AIR
 * TRAFFIC CONTROL, DIRECT LIFE SUPPORT MACHINES, OR WEAPONS SYSTEMS.
 * -----------------------------------------------------------------------------
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