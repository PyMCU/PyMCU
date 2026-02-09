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

    // --- Emission Helpers ---
    void emit(const std::string& instr) const;
    void emit(const std::string& instr, const std::string& operand) const;
    void emit_label(const std::string& label) const;

    // New Helpers to replace raw *out <<
    void emit_comment(const std::string& comment) const;
    void emit_raw(const std::string& text) const; // For directives like PROCESSOR, CODE, END
    void emit_equ(const std::string& name, int value) const;

    // --- Logic Helpers ---
    void load_into_w(const tacky::Val& val);
    void store_w_into(const tacky::Val& val);

    void compile_function(const tacky::Function& func);
    void compile_instruction(const tacky::Instruction& instr);

    // Specific instruction compilers (Cleaner than one giant switch)
    void compile_variant(const tacky::Return& arg);
    void compile_variant(const tacky::Unary& arg);
    void compile_variant(const tacky::Binary& arg);
    void compile_variant(const tacky::Copy& arg);
    void compile_variant(const tacky::Label& arg) const;
    void compile_variant(const tacky::Jump& arg) const;
    void compile_variant(const tacky::JumpIfZero& arg); // Added for completeness
};

#endif // PIC14CODEGEN_H