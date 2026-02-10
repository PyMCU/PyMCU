#include "PIC18CodeGen.h"
#include <format>
#include <utility>
#include <variant>
#include <iostream>

#include "backend/analysis/StackAllocator.h"

PIC18CodeGen::PIC18CodeGen(DeviceConfig  cfg) : config(std::move(cfg)), out(nullptr) {
    label_counter = 0;
    ram_head = 0x60; // Start after Access Bank
}

std::string PIC18CodeGen::resolve_address(const tacky::Val& val) {
    if (const auto c = std::get_if<tacky::Constant>(&val)) {
        return std::format("0x{:02X}", c->value & 0xFF);
    }
    if (const auto mem = std::get_if<tacky::MemoryAddress>(&val)) {
        return std::format("0x{:02X}", mem->address);
    }

    std::string name;
    if (const auto v = std::get_if<tacky::Variable>(&val)) name = v->name;
    else if (const auto t = std::get_if<tacky::Temporary>(&val)) name = t->name;

    if (stack_layout.contains(name)) {
        return name;
    }

    return get_or_alloc_variable(name);
}

std::string PIC18CodeGen::get_or_alloc_variable(const std::string& name) {
    if (!symbol_table.contains(name)) {
        symbol_table[name] = ram_head++;
        emit_raw(std::format("{} EQU 0x{:03X}", name, symbol_table[name]));
    }
    return name;
}

int PIC18CodeGen::get_address(const std::string& operand) {
    if (operand.empty()) return -1;
    if (operand.size() > 2 && operand.substr(0, 2) == "0x") {
        try {
            return std::stoi(operand, nullptr, 16);
        } catch (...) { return -1; }
    }
    
    // Check in symbol table (globals)
    if (symbol_table.contains(operand)) {
        return symbol_table[operand];
    }
    
    // Check stack layout (locals/temporaries)
    if (stack_layout.contains(operand)) {
        // Stack base is 0x60 in our implementation
        return 0x60 + stack_layout[operand];
    }

    return -1;
}

void PIC18CodeGen::select_bank(const std::string& operand) {
    int addr = get_address(operand);
    if (addr == -1) return;

    // Access Bank check (0x00 - 0x5F and 0xF60 - 0xFFF)
    if (addr <= 0x5F || addr >= 0xF60) return;

    const int new_bank = (addr >> 8) & 0x0F;
    if (current_bank == new_bank) return;

    emit("MOVLB", std::format("{}", new_bank));
    current_bank = new_bank;
}

std::string PIC18CodeGen::get_access_mode(const std::string& operand) {
    int addr = get_address(operand);
    if (addr == -1) return "ACCESS"; // Default for registers like WREG, STATUS
    
    // Access Bank check (0x00 - 0x5F and 0xF60 - 0xFFF)
    if (addr <= 0x5F || addr >= 0xF60) return "ACCESS";

    return "BANKED";
}

void PIC18CodeGen::emit(const std::string& mnemonic) const { 
    const_cast<PIC18CodeGen*>(this)->assembly.push_back(PIC18AsmLine::Instruction(mnemonic)); 
}
void PIC18CodeGen::emit(const std::string& mnemonic, const std::string& op1) const { 
    const_cast<PIC18CodeGen*>(this)->assembly.push_back(PIC18AsmLine::Instruction(mnemonic, op1)); 
}
void PIC18CodeGen::emit(const std::string& mnemonic, const std::string& op1, const std::string& op2) const { 
    const_cast<PIC18CodeGen*>(this)->assembly.push_back(PIC18AsmLine::Instruction(mnemonic, op1, op2)); 
}
void PIC18CodeGen::emit(const std::string& mnemonic, const std::string& op1, const std::string& op2, const std::string& op3) const { 
    const_cast<PIC18CodeGen*>(this)->assembly.push_back(PIC18AsmLine::Instruction(mnemonic, op1, op2, op3)); 
}

void PIC18CodeGen::emit_label(const std::string& label) const { 
    const_cast<PIC18CodeGen*>(this)->assembly.push_back(PIC18AsmLine::Label(label)); 
}
void PIC18CodeGen::emit_comment(const std::string& comment) const { 
    const_cast<PIC18CodeGen*>(this)->assembly.push_back(PIC18AsmLine::Comment(comment)); 
}
void PIC18CodeGen::emit_raw(const std::string& text) const { 
    const_cast<PIC18CodeGen*>(this)->assembly.push_back(PIC18AsmLine::Raw(text)); 
}

void PIC18CodeGen::compile(const tacky::Program& program, std::ostream& os) {
    out = &os;
    assembly.clear();
    current_bank = -1;

    StackAllocator allocator;
    auto [offsets, total_size] = allocator.allocate(program);
    this->stack_layout = offsets;

    std::string chip_upper = config.chip;
    std::ranges::transform(chip_upper, chip_upper.begin(), ::toupper);
    emit_raw(std::format("\t#include <P{}.INC>", chip_upper));
    emit_config_directives();

    emit_raw("_stack_base EQU 0x060");
    for (const auto& [name, offset] : stack_layout) {
        emit_raw(std::format("{} EQU _stack_base + 0x{:03X}", name, offset));
    }

    emit_raw("RES_VECT  CODE    0x0000");
    emit("GOTO", "main");

    for (const auto& func : program.functions) {
        compile_function(func);
    }

    // Run Peephole Optimization
    auto optimized = PIC18Peephole::optimize(assembly);

    for (const auto& line : optimized) {
        os << line.to_string() << "\n";
    }
    
    os << "\tEND\n";
}

void PIC18CodeGen::emit_config_directives() {
    for (const auto& [key, val] : config.fuses) {
        emit_raw(std::format("\tCONFIG {} = {}", key, val));
    }
}

void PIC18CodeGen::compile_function(const tacky::Function& func) {
    emit_label(func.name);
    
    for (const auto& instr : func.body) {
        compile_instruction(instr);
    }
}

void PIC18CodeGen::load_into_w(const tacky::Val& val) {
    if (const auto c = std::get_if<tacky::Constant>(&val)) {
        emit("MOVLW", std::format("0x{:02X}", c->value & 0xFF));
    } else {
        std::string addr = resolve_address(val);
        select_bank(addr);
        emit("MOVF", addr, "W", get_access_mode(addr));
    }
}

void PIC18CodeGen::store_w_into(const tacky::Val& val) {
    std::string addr = resolve_address(val);
    select_bank(addr);
    emit("MOVWF", addr, get_access_mode(addr));
}

void PIC18CodeGen::compile_instruction(const tacky::Instruction& instr) {
    std::visit([this](auto&& arg) { compile_variant(arg); }, instr);
}

void PIC18CodeGen::compile_variant(const tacky::Return& arg) {
    load_into_w(arg.value);
    emit("RETURN");
}

void PIC18CodeGen::compile_variant(const tacky::Jump& arg) const {
    emit("BRA", arg.target);
}

void PIC18CodeGen::compile_variant(const tacky::JumpIfZero& arg) {
    load_into_w(arg.condition);
    emit("ANDLW", "0xFF"); // Set Z flag
    emit("BZ", arg.target);
}

void PIC18CodeGen::compile_variant(const tacky::JumpIfNotZero& arg) {
    load_into_w(arg.condition);
    emit("ANDLW", "0xFF"); // Set Z flag
    emit("BNZ", arg.target);
}

void PIC18CodeGen::compile_variant(const tacky::Label& arg) const {
    emit_label(arg.name);
}

void PIC18CodeGen::compile_variant(const tacky::Call& arg) {
    emit("CALL", arg.function_name);
    store_w_into(arg.dst);
}

void PIC18CodeGen::compile_variant(const tacky::Copy& arg) {
    if (std::holds_alternative<tacky::Constant>(arg.src)) {
        load_into_w(arg.src);
        store_w_into(arg.dst);
    } else {
        std::string src = resolve_address(arg.src);
        std::string dst = resolve_address(arg.dst);
        // PIC18 has MOVFF for memory to memory
        emit("MOVFF", src, dst);
    }
}

void PIC18CodeGen::compile_variant(const tacky::Unary& arg) {
    load_into_w(arg.src);
    if (arg.op == tacky::UnaryOp::Neg) {
        emit("NEGF", "WREG");
    } else if (arg.op == tacky::UnaryOp::Not) {
        emit("COMF", "WREG", "W");
    } else if (arg.op == tacky::UnaryOp::BitNot) {
        emit("COMF", "WREG", "W");
    }
    store_w_into(arg.dst);
}

void PIC18CodeGen::compile_variant(const tacky::Binary& arg) {
    // Optimization for Mul if one operand is literal
    if (arg.op == tacky::BinaryOp::Mul) {
        if (const auto c1 = std::get_if<tacky::Constant>(&arg.src1)) {
            load_into_w(arg.src2);
            emit("MULWF", "WREG"); // This assumes src1 value is in W
            // Wait, load_into_w(arg.src2) loads src2 into W.
            // Then we need src1 constant in W for MULWF? No, MULWF multiplies f * W.
            // If we want W * constant, we need to load constant into W and then MULWF f.
        }
    }
    // Rewriting Binary to be more robust
    load_into_w(arg.src1);
    std::string right = resolve_address(arg.src2);
    
    switch (arg.op) {
        case tacky::BinaryOp::Add:
            if (std::holds_alternative<tacky::Constant>(arg.src2)) {
                emit("ADDLW", right);
            } else {
                select_bank(right);
                emit("ADDWF", right, "W", get_access_mode(right));
            }
            break;
        case tacky::BinaryOp::Sub:
            if (std::holds_alternative<tacky::Constant>(arg.src2)) {
                // W = src1 - constant
                emit("ADDLW", std::format("(0x100 - ({})) & 0xFF", right));
            } else {
                // W = src1 - src2
                // PIC SUBWF f, d: f - W -> d
                // We have src1 in W. We need src1 - src2.
                // If we do SUBWF src2, W: src2 - src1 -> W. This is -(src1 - src2).
                // Correct way:
                // load src1 into W
                // SUBWF src2, W -> src2 - src1 in W
                // then Negate W. Or swap:
                // load src2 into W
                // SUBWF src1, W -> src1 - src2 in W
                load_into_w(arg.src2);
                std::string left = resolve_address(arg.src1);
                select_bank(left);
                emit("SUBWF", left, "W", get_access_mode(left));
            }
            break;
        case tacky::BinaryOp::Mul:
            if (std::holds_alternative<tacky::Constant>(arg.src2)) {
                // W already has src1
                emit("MULWF", "WREG"); // Invalid, MULWF needs a file register
                // Actually we should just use the f * W form
                std::string left_addr = resolve_address(arg.src1);
                emit("MOVLW", right);
                select_bank(left_addr);
                emit("MULWF", left_addr, get_access_mode(left_addr));
            } else {
                // W has src1
                select_bank(right);
                emit("MULWF", right, get_access_mode(right));
            }
            emit("MOVF", "PRODL", "W", "ACCESS");
            break;
        default:
            break;
    }
    store_w_into(arg.dst);
}

void PIC18CodeGen::compile_variant(const tacky::BitSet& arg) {
    std::string addr = resolve_address(arg.target);
    select_bank(addr);
    emit("BSF", addr, std::format("{}", arg.bit), get_access_mode(addr));
}

void PIC18CodeGen::compile_variant(const tacky::BitClear& arg) {
    std::string addr = resolve_address(arg.target);
    select_bank(addr);
    emit("BCF", addr, std::format("{}", arg.bit), get_access_mode(addr));
}

void PIC18CodeGen::compile_variant(const tacky::BitCheck& arg) {
    std::string addr = resolve_address(arg.source);
    select_bank(addr);
    emit("CLRF", "WREG");
    emit("BTFSC", addr, std::format("{}", arg.bit), get_access_mode(addr));
    emit("MOVLW", "1");
    store_w_into(arg.dst);
}

void PIC18CodeGen::compile_variant(const tacky::BitWrite& arg) {
    std::string addr = resolve_address(arg.target);
    select_bank(addr);
    load_into_w(arg.src);
    emit("TSTFSZ", "WREG");
    emit("BRA", make_label("set_bit"));
    emit("BCF", addr, std::format("{}", arg.bit), get_access_mode(addr));
    emit("BRA", make_label("end_bit_write"));
    emit_label(make_label("set_bit"));
    emit("BSF", addr, std::format("{}", arg.bit), get_access_mode(addr));
    emit_label(make_label("end_bit_write"));
}
