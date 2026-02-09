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
    struct Variable { std::string name; }; // User Variables (ej: "counter")
    struct Temporary { std::string name; }; // Compiler Variables (ej: "tmp.1")

    // A value can be any of these 3
    using Val = std::variant<Constant, Variable, Temporary>;

    // --- Instruction Types ---

    enum class UnaryOp { Not, Neg, Complement };
    enum class BinaryOp { Add, Sub, Mul, Div, LessThan, GreaterThan, Equal };

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

    struct Label {
        std::string name;
    };

    // --- The Instruction Container ---
    // An instruction is a variant of any of the preceding structures
    using Instruction = std::variant<Return, Unary, Binary, Copy, Jump, JumpIfZero, Label>;

    // --- Function Definition ---
    struct Function {
        std::string name;
        std::vector<Instruction> body; // Flat list, no longer a tree
    };

    struct Program {
        std::vector<Function> functions;
    };

}

#endif //TACKY_H