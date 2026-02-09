#ifndef CODEGEN_H
#define CODEGEN_H

#pragma once
#include <iostream>
#include "ir/Tacky.h"

class CodeGen {
public:
    virtual ~CodeGen() = default;
    virtual void compile(const tacky::Program& program, std::ostream& os) = 0;
};

#endif // CODEGEN_H