#ifndef PIC12PEEPHOLE_H
#define PIC12PEEPHOLE_H

#include <vector>
#include "PIC12CodeGen.h"

class PIC12Peephole {
public:
    static std::vector<PIC12AsmLine> optimize(const std::vector<PIC12AsmLine>& lines);
};

#endif // PIC12PEEPHOLE_H
