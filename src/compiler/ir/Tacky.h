#ifndef TACKY_H
#define TACKY_H

#pragma once
#include <string>
#include <variant>
#include <vector>
#include <memory>

namespace tacky {

    // --- Operand Types ---
    struct Constant { int value; };
    struct Variable { std::string name; };
    struct Temporary { std::string name; };

    // Represents a physical memory address (MMIO or Static Global)
    struct MemoryAddress { int address; };

    using Val = std::variant<Constant, Variable, Temporary, MemoryAddress>;

    enum class UnaryOp {
        Not,
        Neg,
        BitNot
    };

    enum class BinaryOp {
        Add, Sub, Mul, Div, Mod,
        Equal, NotEqual,
        LessThan, LessEqual,
        GreaterThan, GreaterEqual,
        BitAnd, BitOr, BitXor,
        LShift, RShift
    };

    // --- Instructions ---

    struct Return {
        Val value;
    };

    struct Unary {
        UnaryOp op;
        Val src;
        Val dst;
    };

    struct Binary {
        BinaryOp op;
        Val src1;
        Val src2;
        Val dst;
    };

    struct Copy {
        Val src;
        Val dst;
    };

    struct Jump {
        std::string target;
    };

    struct JumpIfZero {
        Val condition;
        std::string target;
    };

    struct JumpIfNotZero {
        Val condition;
        std::string target;
    };

    struct Label {
        std::string name;
    };

    struct Call {
        std::string function_name;
        std::vector<Val> args;
        Val dst;
    };

    struct BitSet {
        Val target;
        int bit;
    };

    struct BitClear {
        Val target;
        int bit;
    };

    struct BitCheck {
        Val source;
        int bit;
        Val dst;
    };

    // --- The Instruction Container ---
    using Instruction = std::variant<
        Return,
        Unary,
        Binary,
        Copy,
        Jump,
        JumpIfZero,
        JumpIfNotZero,
        Label,
        Call,
        BitSet,
        BitClear,
        BitCheck
    >;

    // --- Function Definition ---
    struct Function {
        std::string name;
        std::vector<std::string> params;
        std::vector<Instruction> body;
    };

    struct Program {
        std::vector<Function> functions;
    };

}

#endif //TACKY_H