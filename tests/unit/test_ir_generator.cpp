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
    auto ir = ir_gen.generate(*ast);

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
    auto ir = ir_gen.generate(*ast);

    ASSERT_EQ(ir.functions.size(), 1);
    ASSERT_GE(ir.functions[0].body.size(), 1);

    auto ret_instr = std::get_if<tacky::Return>(&ir.functions[0].body[0]);
    ASSERT_NE(ret_instr, nullptr);
    auto val = std::get_if<tacky::Constant>(&ret_instr->value);
    ASSERT_NE(val, nullptr);
    EXPECT_EQ(val->value, 0);
}

TEST(IRGeneratorTest, MultipleFunctions) {
    Lexer lexer("def a():\n    return 1\ndef b():\n    return 2");
    auto tokens = lexer.tokenize();
    Parser parser(tokens);
    auto ast = parser.parseProgram();

    IRGenerator ir_gen;
    auto ir = ir_gen.generate(*ast);

    ASSERT_EQ(ir.functions.size(), 2);
    EXPECT_EQ(ir.functions[0].name, "a");
    EXPECT_EQ(ir.functions[1].name, "b");
}
