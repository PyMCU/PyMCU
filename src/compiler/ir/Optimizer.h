#ifndef OPTIMIZER_H
#define OPTIMIZER_H

#include "Tacky.h"

class Optimizer {
public:
    static tacky::Program optimize(const tacky::Program& program);

private:
    static void optimize_function(tacky::Function& func);
    static void fold_constants(tacky::Function& func);
    static void eliminate_dead_code(tacky::Function& func);
};

#endif // OPTIMIZER_H
