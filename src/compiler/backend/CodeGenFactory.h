/*
 * -----------------------------------------------------------------------------
 * PyMCU Compiler (pymcuc)
 * Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
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

#ifndef CODEGENFACTORY_H
#define CODEGENFACTORY_H

#include <memory>
#include <stdexcept>

#include "../common/DeviceConfig.h"
#include "CodeGen.h"
#include "targets/avr/AVRCodeGen.h"
#include "targets/pic12/PIC12CodeGen.h"
#include "targets/pic14/PIC14CodeGen.h"
#include "targets/pic18/PIC18CodeGen.h"
#include "targets/pio/PIOCodeGen.h"
#include "targets/riscv/RISCVCodeGen.h"

class CodeGenFactory {
 public:
  static std::unique_ptr<CodeGen> create(const std::string &arch,
                                         const DeviceConfig &config) {
    if (arch == "pic12" || arch == "baseline" || arch.starts_with("pic10f") ||
        arch.starts_with("pic12f")) {
      return std::make_unique<PIC12CodeGen>(config);
    }
    if (arch == "pic14" || arch == "pic14e" || arch == "midrange" ||
        arch.starts_with("pic16f")) {
      return std::make_unique<PIC14CodeGen>(config);
    }
    if (arch == "pic18" || arch == "advanced" || arch.starts_with("pic18f")) {
      return std::make_unique<PIC18CodeGen>(config);
    }
    if (arch == "avr" || arch == "avr8" || arch == "atmega328p") {
      return std::make_unique<AVRCodeGen>(config);
    }
    if (arch == "riscv" || arch == "rv32ec" || arch.starts_with("ch32v")) {
      return std::make_unique<RISCVCodeGen>(config);
    }
    if (arch == "pio" || arch == "rp2040-pio") {
      return std::make_unique<PIOCodeGen>(config);
    }

    throw std::runtime_error("Unknown architecture: " + arch);
  }
};

#endif  // CODEGENFACTORY_H