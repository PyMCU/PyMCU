#ifndef CODEGENFACTORY_H
#define CODEGENFACTORY_H

#include <memory>
#include <stdexcept>

#include "CodeGen.h"
#include "targets/pic14/PIC14CodeGen.h"
// #include "targets/avr8/AVRCodeGen.h"
#include "DeviceConfig.h"

class CodeGenFactory {
public:
    static std::unique_ptr<CodeGen> create(const std::string& arch, const DeviceConfig& config) {
        if (arch == "pic14" || arch == "midrange") {
            return std::make_unique<PIC14CodeGen>(config);
        }
        // else if (arch == "avr8") ...

        throw std::runtime_error("Unknown architecture: " + arch);
    }
};

#endif //CODEGENFACTORY_H