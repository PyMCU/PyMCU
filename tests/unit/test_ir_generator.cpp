#include <gtest/gtest.h>
#include "ir/IRGenerator.h"
#include "frontend/Parser.h"
#include "frontend/Lexer.h"

TEST(IRGeneratorTest, SimpleReturn) {
    Lexer lexer("def main():\n    return 42");
    auto tokens = lexer.tokenize();
    Parser parser(tokens);
    auto ast = parser.parseProgram();

    IRGenerator ir_gen;
    auto ir = ir_gen.generate(*ast, {});

    ASSERT_EQ(ir.functions.size(), 1);
    EXPECT_EQ(ir.functions[0].name, "main");
    ASSERT_GE(ir.functions[0].body.size(), 1);

    auto ret_instr = std::get_if<tacky::Return>(&ir.functions[0].body[0]);
    ASSERT_NE(ret_instr, nullptr);
    auto val = std::get_if<tacky::Constant>(&ret_instr->value);
    ASSERT_NE(val, nullptr);
    EXPECT_EQ(val->value, 42);
}

TEST(IRGeneratorTest, ImplicitReturn) {
    Lexer lexer("def main():\n    return");
    auto tokens = lexer.tokenize();
    Parser parser(tokens);
    auto ast = parser.parseProgram();

    IRGenerator ir_gen;
    auto ir = ir_gen.generate(*ast, {});

    ASSERT_EQ(ir.functions.size(), 1);
    ASSERT_GE(ir.functions[0].body.size(), 1);

    auto ret_instr = std::get_if<tacky::Return>(&ir.functions[0].body[0]);
    ASSERT_NE(ret_instr, nullptr);
    EXPECT_TRUE(std::holds_alternative<std::monostate>(ret_instr->value));
}

TEST(IRGeneratorTest, MultipleFunctions) {
    Lexer lexer("def a():\n    return 1\ndef b():\n    return 2");
    auto tokens = lexer.tokenize();
    Parser parser(tokens);
    auto ast = parser.parseProgram();

    IRGenerator ir_gen;
    auto ir = ir_gen.generate(*ast, {});

    ASSERT_EQ(ir.functions.size(), 2);
    EXPECT_EQ(ir.functions[0].name, "a");
    EXPECT_EQ(ir.functions[1].name, "b");
}

TEST(IRGeneratorTest, IfStatement) {
    Lexer lexer("def f(x: int):\n    if x:\n        return 1\n    else:\n        return 2");
    auto tokens = lexer.tokenize();
    Parser parser(tokens);
    auto ast = parser.parseProgram();

    IRGenerator ir_gen;
    auto ir = ir_gen.generate(*ast, {});

    auto& body = ir.functions[0].body;
    // Expect: JumpIfZero, Return(1), Jump, Label, Return(2), Label
    // (Note: visitIf emits Label(else), then else_branch, then Label(end))
    
    bool found_jump_if_zero = false;
    bool found_labels = false;
    for (const auto& inst : body) {
        if (std::holds_alternative<tacky::JumpIfZero>(inst)) found_jump_if_zero = true;
        if (std::holds_alternative<tacky::Label>(inst)) found_labels = true;
    }
    EXPECT_TRUE(found_jump_if_zero);
    EXPECT_TRUE(found_labels);
}

TEST(IRGeneratorTest, WhileStatement) {
    Lexer lexer("def f():\n    while 1:\n        pass");
    auto tokens = lexer.tokenize();
    Parser parser(tokens);
    auto ast = parser.parseProgram();

    IRGenerator ir_gen;
    auto ir = ir_gen.generate(*ast, {});

    auto& body = ir.functions[0].body;
    // Expect: Label, Constant(1), JumpIfZero, Jump, Label
    bool found_jump = false;
    int labels = 0;
    for (const auto& inst : body) {
        if (std::holds_alternative<tacky::Jump>(inst)) found_jump = true;
        if (std::holds_alternative<tacky::Label>(inst)) labels++;
    }
    EXPECT_TRUE(found_jump);
    EXPECT_GE(labels, 2);
}

TEST(IRGeneratorTest, BinaryOps) {
    Lexer lexer("def f(a: int, b: int):\n    return a + b");
    auto tokens = lexer.tokenize();
    Parser parser(tokens);
    auto ast = parser.parseProgram();

    IRGenerator ir_gen;
    auto ir = ir_gen.generate(*ast, {});

    auto& body = ir.functions[0].body;
    bool found_binary = false;
    for (const auto& inst : body) {
        if (auto b = std::get_if<tacky::Binary>(&inst)) {
            EXPECT_EQ(b->op, tacky::BinaryOp::Add);
            found_binary = true;
        }
    }
    EXPECT_TRUE(found_binary);
}

TEST(IRGeneratorTest, BitManipulation) {
    Lexer lexer("def f(port: ptr):\n    port[0] = 1\n    return port[1]");
    auto tokens = lexer.tokenize();
    Parser parser(tokens);
    auto ast = parser.parseProgram();

    IRGenerator ir_gen;
    auto ir = ir_gen.generate(*ast, {});

    auto& body = ir.functions[0].body;
    bool found_set = false;
    bool found_check = false;
    for (const auto& inst : body) {
        if (std::holds_alternative<tacky::BitSet>(inst)) found_set = true;
        if (std::holds_alternative<tacky::BitCheck>(inst)) found_check = true;
    }
    EXPECT_TRUE(found_set);
    EXPECT_TRUE(found_check);
}

TEST(IRGeneratorTest, NoneReturnCall) {
    Lexer lexer("def void_func():\n    pass\ndef main():\n    void_func()");
    auto tokens = lexer.tokenize();
    Parser parser(tokens);
    auto ast = parser.parseProgram();

    IRGenerator ir_gen;
    auto ir = ir_gen.generate(*ast, {});

    ASSERT_EQ(ir.functions.size(), 2);
    auto& main_body = ir.functions[1].body;
    
    bool found_call = false;
    for (const auto& inst : main_body) {
        if (auto call = std::get_if<tacky::Call>(&inst)) {
            EXPECT_EQ(call->function_name, "void_func");
            EXPECT_TRUE(std::holds_alternative<std::monostate>(call->dst));
            found_call = true;
        }
    }
    EXPECT_TRUE(found_call);
}

TEST(IRGeneratorTest, IntReturnCall) {
    Lexer lexer("def int_func() -> int:\n    return 42\ndef main():\n    x = int_func()");
    auto tokens = lexer.tokenize();
    Parser parser(tokens);
    auto ast = parser.parseProgram();

    IRGenerator ir_gen;
    auto ir = ir_gen.generate(*ast, {});

    ASSERT_EQ(ir.functions.size(), 2);
    auto& main_body = ir.functions[1].body;

    bool found_call_with_dest = false;
    for (const auto& inst : main_body) {
        if (auto call = std::get_if<tacky::Call>(&inst)) {
            EXPECT_EQ(call->function_name, "int_func");
            EXPECT_FALSE(std::holds_alternative<std::monostate>(call->dst));
            found_call_with_dest = true;
        }
    }
    EXPECT_TRUE(found_call_with_dest);
}

TEST(IRGeneratorTest, ContinueStatement) {
    Lexer lexer("def main():\n    while 1:\n        continue");
    auto tokens = lexer.tokenize();
    Parser parser(tokens);
    auto ast = parser.parseProgram();

    IRGenerator ir_gen;
    // This should not throw "Unknown Statement type"
    EXPECT_NO_THROW(ir_gen.generate(*ast, {}));
}

TEST(IRGeneratorTest, BreakStatement) {
    Lexer lexer("def main():\n    while 1:\n        break");
    auto tokens = lexer.tokenize();
    Parser parser(tokens);
    auto ast = parser.parseProgram();

    IRGenerator ir_gen;
    EXPECT_NO_THROW(ir_gen.generate(*ast, {}));
}
