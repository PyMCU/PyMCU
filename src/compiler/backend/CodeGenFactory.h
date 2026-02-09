#ifndef CODEGENFACTORY_H
#define CODEGENFACTORY_H

#include "CodeGen.h"
#include "targets/pic14/PIC14CodeGen.h"
// #include "targets/avr8/AVRCodeGen.h"

#include <memory>
#include <stdexcept>

class CodeGenFactory {
public:
    static std::unique_ptr<CodeGen> create(const std::string& arch) {
        if (arch == "pic14" || arch == "midrange") {
            return std::make_unique<PIC14CodeGen>();
        }
        // else if (arch == "avr8") ...

        throw std::runtime_error("Unknown architecture: " + arch);
    }
};

#endif //CODEGENFACTORY_H