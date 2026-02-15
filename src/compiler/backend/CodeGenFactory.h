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