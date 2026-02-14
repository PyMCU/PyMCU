#ifndef CODEGENFACTORY_H
#define CODEGENFACTORY_H

#include <memory>
#include <stdexcept>

#include "CodeGen.h"
#include "targets/pic14/PIC14CodeGen.h"
#include "targets/pic18/PIC18CodeGen.h"
#include "targets/pic12/PIC12CodeGen.h"
#include "targets/avr/AVRCodeGen.h"
#include "targets/riscv/RISCVCodeGen.h"
#include "targets/pio/PIOCodeGen.h"
#include "DeviceConfig.h"

class CodeGenFactory {
public:
    static std::unique_ptr<CodeGen> create(const std::string &arch, const DeviceConfig &config) {
        if (arch == "pic12" || arch == "baseline" || arch.starts_with("pic10f") || arch.starts_with("pic12f")) {
            return std::make_unique<PIC12CodeGen>(config);
        }
        if (arch == "pic14" || arch == "midrange" || arch.starts_with("pic16f")) {
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

#endif //CODEGENFACTORY_H