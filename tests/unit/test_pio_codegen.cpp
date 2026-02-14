#include <gtest/gtest.h>
#include <sstream>
#include "backend/targets/pio/PIOCodeGen.h"
#include "backend/CodeGenFactory.h"
#include "ir/Tacky.h"
#include "DeviceConfig.h"
#include "ir/IRGenerator.h"
#include "frontend/Parser.h"
#include "frontend/Lexer.h"

TEST(PIOCodeGenTest, SimpleMove) {
    DeviceConfig cfg;
    cfg.chip = "test_pio";
    PIOCodeGen codegen(cfg);

    tacky::Program program;
    tacky::Function func;
    func.name = "main";

    // x = 5
    func.body.push_back(tacky::Copy{
        tacky::Constant{5},
        tacky::Variable{"x"}
    });
    program.functions.push_back(func);

    std::stringstream ss;
    codegen.compile(program, ss);

    std::string output = ss.str();
    EXPECT_NE(output.find("SET X, 5"), std::string::npos);
}

TEST(PIOCodeGenTest, JumpIfZero) {
    DeviceConfig cfg;
    cfg.chip = "test_pio";
    PIOCodeGen codegen(cfg);

    tacky::Program program;
    tacky::Function func;
    func.name = "main";

    // if x == 0 goto label
    func.body.push_back(tacky::JumpIfZero{
        tacky::Variable{"x"},
        "mylabel"
    });
    program.functions.push_back(func);

    std::stringstream ss;
    codegen.compile(program, ss);

    std::string output = ss.str();
    EXPECT_NE(output.find("JMP !X, mylabel"), std::string::npos);
}

TEST(PIOCodeGenTest, BitNot) {
    DeviceConfig cfg;
    cfg.chip = "test_pio";
    PIOCodeGen codegen(cfg);

    tacky::Program program;
    tacky::Function func;
    func.name = "main";

    // y = ~x
    func.body.push_back(tacky::Unary{
        tacky::UnaryOp::BitNot,
        tacky::Variable{"x"},
        tacky::Variable{"y"}
    });
    program.functions.push_back(func);

    std::stringstream ss;
    codegen.compile(program, ss);

    std::string output = ss.str();
    EXPECT_NE(output.find("MOV Y, !X"), std::string::npos);
}

TEST(PIOCodeGenTest, DecrementOnly) {
    DeviceConfig cfg;
    cfg.chip = "test_pio";
    PIOCodeGen codegen(cfg);

    tacky::Program program;
    tacky::Function func;
    func.name = "main";

    // x = x - 1
    func.body.push_back(tacky::Binary{
        tacky::BinaryOp::Sub,
        tacky::Variable{"x"},
        tacky::Constant{1},
        tacky::Variable{"x"}
    });
    program.functions.push_back(func);

    std::stringstream ss;
    codegen.compile(program, ss);

    std::string output = ss.str();
    EXPECT_NE(output.find("JMP X--"), std::string::npos);
}

TEST(PIOCodeGenTest, Intrinsics) {
    DeviceConfig cfg;
    cfg.chip = "test_pio";
    PIOCodeGen codegen(cfg);

    tacky::Program program;
    tacky::Function func;
    func.name = "main";

    // __pio_pull()
    func.body.push_back(tacky::Call{"__pio_pull", {}, std::monostate{}});
    // __pio_wait(1, PIN, 0)
    func.body.push_back(tacky::Call{
        "__pio_wait", {tacky::Constant{1}, tacky::MemoryAddress{1}, tacky::Constant{0}}, std::monostate{}
    });
    // delay(5) on previous instruction
    func.body.push_back(tacky::Call{"delay", {tacky::Constant{5}}, std::monostate{}});

    program.functions.push_back(func);

    std::stringstream ss;
    codegen.compile(program, ss);

    std::string output = ss.str();
    EXPECT_NE(output.find("PULL BLOCK"), std::string::npos);
    EXPECT_NE(output.find("WAIT 1, PIN, 0 [5]"), std::string::npos);
}

TEST(PIOCodeGenTest, UnsupportedOpsThrow) {
    DeviceConfig cfg;
    cfg.chip = "test_pio";
    PIOCodeGen codegen(cfg);

    tacky::Program program;
    tacky::Function func;
    func.name = "main";

    // x = x + 1 (Add is not supported)
    func.body.push_back(tacky::Binary{
        tacky::BinaryOp::Add,
        tacky::Variable{"x"},
        tacky::Constant{1},
        tacky::Variable{"x"}
    });
    program.functions.push_back(func);

    std::stringstream ss;
    EXPECT_THROW(codegen.compile(program, ss), std::runtime_error);
}

TEST(PIOCodeGenTest, FullMapping) {
    std::string src = R"(
def ws2812():
    x = 0 
    while True:
        pull()             
        out(PINS, 1)       
        delay(2)           
        wait(1, PIN, 0)
        pull(NOBLOCK)
)";

    // Simulating pio.py
    std::string pio_py = R"(
PINS = PIORegister(0)
PIN = PIORegister(1)
NOBLOCK = 0
)";

    Lexer l1(src);
    auto ast = Parser(l1.tokenize()).parseProgram();

    Lexer l2(pio_py);
    auto pio_ast = Parser(l2.tokenize()).parseProgram();

    IRGenerator irGen;
    auto ir = irGen.generate(*ast, {pio_ast.get()});

    DeviceConfig cfg;
    cfg.chip = "ws2812";
    PIOCodeGen codegen(cfg);

    std::stringstream ss;
    codegen.compile(ir, ss);
    std::string output = ss.str();

    EXPECT_NE(output.find("PULL BLOCK"), std::string::npos);
    EXPECT_NE(output.find("OUT PINS, 1"), std::string::npos);
    EXPECT_NE(output.find("WAIT 1, PIN, 0"), std::string::npos);
    EXPECT_NE(output.find("PULL NOBLOCK"), std::string::npos);
    EXPECT_NE(output.find("[2]"), std::string::npos);
}