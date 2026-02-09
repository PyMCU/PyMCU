#ifndef PIC14CODEGEN_H
#define PIC14CODEGEN_H

#pragma once
#include <map>
#include <string>
#include <utility>
#include <vector>

#include "DeviceConfig.h"
#include "../../CodeGen.h"

class PIC14CodeGen : public CodeGen {
public:
    explicit PIC14CodeGen(DeviceConfig  cfg);
    void compile(const tacky::Program& program, std::ostream& os) override;

private:
    DeviceConfig config;
    std::ostream* out;
    std::map<std::string, int> symbol_table;
    int ram_head;

    // --- Memory & Bank Management ---
    int get_or_alloc_variable(const std::string& name);
    int resolve_address(const tacky::Val& val);
    void select_bank(int address);
    int current_bank = -1;

    // --- Emission Helpers ---
    void emit(const std::string& mnemonic) const;
    void emit(const std::string& mnemonic, const std::string& op1) const;
    void emit(const std::string& mnemonic, const std::string& op1, const std::string& op2) const;

    void emit_label(const std::string& label) const;
    void emit_comment(const std::string& comment) const;
    void emit_raw(const std::string& text) const;
    void emit_config_directives();

    // --- Logic Helpers (The "Glue") ---
    void load_into_w(const tacky::Val& val);
    void store_w_into(const tacky::Val& val);

    // --- Compilation Dispatchers ---

    void compile_function(const tacky::Function& func);
    void compile_instruction(const tacky::Instruction& instr);

    // --- Instruction Visitors (Implementation) ---

    // Control Flow
    void compile_variant(const tacky::Return& arg);
    void compile_variant(const tacky::Jump& arg) const;
    void compile_variant(const tacky::JumpIfZero& arg);
    void compile_variant(const tacky::JumpIfNotZero& arg);
    void compile_variant(const tacky::Label& arg) const;
    void compile_variant(const tacky::Call& arg);

    // Data Movement & Arithmetic
    void compile_variant(const tacky::Copy& arg);
    void compile_variant(const tacky::Unary& arg);
    void compile_variant(const tacky::Binary& arg);

    // Bit Manipulation (Hardware Access)
    void compile_variant(const tacky::BitSet& arg);
    void compile_variant(const tacky::BitClear& arg);
    void compile_variant(const tacky::BitCheck& arg);
};

#endif // PIC14CODEGEN_H