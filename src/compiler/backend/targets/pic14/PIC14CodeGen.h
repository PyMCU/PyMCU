#ifndef PIC14CODEGEN_H
#define PIC14CODEGEN_H

#pragma once
#include "../../CodeGen.h"
#include <map>
#include <string>

class PIC14CodeGen : public CodeGen {
public:
    void compile(const tacky::Program& program, std::ostream& os) override;

private:
    std::ostream* out;
    std::map<std::string, int> symbol_table;
    int ram_head = 0x0C;

    int get_or_alloc_variable(const std::string& name);

    void emit(const std::string& instr);
    void emit(const std::string& instr, const std::string& operand);
    void emit_label(const std::string& label);

    void load_into_w(const tacky::Val& val);
    void store_w_into(const tacky::Val& val);

    void compile_function(const tacky::Function& func);
    void compile_instruction(const tacky::Instruction& instr);
};

#endif // PIC14CODEGEN_H