#include <gtest/gtest.h>
#include "ir/IRGenerator.h"
#include "frontend/Parser.h"
#include "frontend/Lexer.h"
#include "backend/targets/pic14/PIC14CodeGen.h"
#include <sstream>

TEST(ArgumentsTest, CallWithArguments) {
    Lexer lexer("def add(a: int, b: int) -> int:\n    return a + b\n\ndef main():\n    x: int = add(1, 2)");
    auto tokens = lexer.tokenize();
    Parser parser(tokens);
    auto ast = parser.parseProgram();

    IRGenerator ir_gen;
    auto ir = ir_gen.generate(*ast, {});

    // Check IR for 'main'
    ASSERT_EQ(ir.functions.size(), 2);
    auto* main_func = (ir.functions[0].name == "main") ? &ir.functions[0] : &ir.functions[1];
    auto* add_func = (ir.functions[0].name == "add") ? &ir.functions[0] : &ir.functions[1];

    EXPECT_EQ(add_func->params.size(), 2);
    EXPECT_EQ(add_func->params[0], "add.a");
    EXPECT_EQ(add_func->params[1], "add.b");

    // Check main body for argument passing
    bool found_copy_a = false;
    bool found_copy_b = false;
    bool found_call = false;

    for (const auto& inst : main_func->body) {
        if (auto* copy = std::get_if<tacky::Copy>(&inst)) {
            if (auto* dst = std::get_if<tacky::Variable>(&copy->dst)) {
                if (dst->name.find("add.a") != std::string::npos) found_copy_a = true;
                if (dst->name.find("add.b") != std::string::npos) found_copy_b = true;
            }
        }
        if (auto* call = std::get_if<tacky::Call>(&inst)) {
            if (call->function_name == "add") found_call = true;
        }
    }

    EXPECT_TRUE(found_copy_a);
    EXPECT_TRUE(found_copy_b);
    EXPECT_TRUE(found_call);
}

TEST(ArgumentsTest, StackLayoutWithArguments) {
    Lexer lexer("def add(a: int, b: int) -> int:\n    return a + b\n\ndef main():\n    x: int = add(1, 2)");
    auto tokens = lexer.tokenize();
    Parser parser(tokens);
    auto ast = parser.parseProgram();

    IRGenerator ir_gen;
    auto ir = ir_gen.generate(*ast, {});

    PIC14CodeGen codegen(DeviceConfig{"pic16f84a", 4000000});
    std::stringstream ss;
    codegen.compile(ir, ss);
    std::string asm_code = ss.str();

    // Check that add.a and add.b are EQU to some offsets from _stack_base
    EXPECT_TRUE(asm_code.find("add.a EQU _stack_base +") != std::string::npos);
    EXPECT_TRUE(asm_code.find("add.b EQU _stack_base +") != std::string::npos);
    EXPECT_TRUE(asm_code.find("main.x EQU _stack_base +") != std::string::npos);

    // Verify calling sequence in main
    // It should look something like:
    // MOVLW 0x01
    // MOVWF add.a
    // MOVLW 0x02
    // MOVWF add.b
    // CALL add
    
    EXPECT_TRUE(asm_code.find("MOVLW\t0x01") != std::string::npos);
    EXPECT_TRUE(asm_code.find("MOVWF\tadd.a") != std::string::npos);
    EXPECT_TRUE(asm_code.find("MOVLW\t0x02") != std::string::npos);
    EXPECT_TRUE(asm_code.find("MOVWF\tadd.b") != std::string::npos);
    EXPECT_TRUE(asm_code.find("CALL\tadd") != std::string::npos);
}
