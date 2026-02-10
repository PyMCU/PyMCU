#include <gtest/gtest.h>
#include <sstream>
#include "backend/targets/pic12/PIC12CodeGen.h"
#include "backend/CodeGenFactory.h"
#include "ir/Tacky.h"
#include "DeviceConfig.h"

TEST(PIC12CodeGenTest, SimpleReturn) {
    DeviceConfig cfg;
    cfg.chip = "10f200";
    PIC12CodeGen codegen(cfg);

    tacky::Program program;
    tacky::Function func;
    func.name = "main";
    
    // return 42
    func.body.push_back(tacky::Return{tacky::Constant{42}});
    program.functions.push_back(func);

    std::stringstream ss;
    codegen.compile(program, ss);

    std::string output = ss.str();
    EXPECT_NE(output.find("MOVLW\t0x2A"), std::string::npos);
    EXPECT_NE(output.find("RETLW\t0"), std::string::npos);
}

TEST(PIC12CodeGenTest, NoAddLW_Optimization) {
    DeviceConfig cfg;
    cfg.chip = "10f200";
    PIC12CodeGen codegen(cfg);

    tacky::Program program;
    tacky::Function func;
    func.name = "main";
    
    // a = b + 10
    func.body.push_back(tacky::Binary{
        tacky::BinaryOp::Add,
        tacky::Variable{"b"},
        tacky::Constant{10},
        tacky::Variable{"a"}
    });
    func.body.push_back(tacky::Return{std::monostate{}});
    program.functions.push_back(func);

    std::stringstream ss;
    codegen.compile(program, ss);

    std::string output = ss.str();
    // Should NOT have ADDLW (PIC12 baseline doesn't have it)
    EXPECT_EQ(output.find("ADDLW"), std::string::npos);
    // Should use MOVLW and ADDWF
    EXPECT_NE(output.find("MOVLW\t0x0A"), std::string::npos);
    EXPECT_NE(output.find("ADDWF"), std::string::npos);
}

TEST(PIC12CodeGenTest, FactorySupport) {
    DeviceConfig cfg;
    cfg.chip = "pic10f200";
    auto codegen = CodeGenFactory::create("pic10f200", cfg);
    EXPECT_NE(dynamic_cast<PIC12CodeGen*>(codegen.get()), nullptr);
}

TEST(PIC12CodeGenTest, TRISInstruction) {
    DeviceConfig cfg;
    cfg.chip = "10f200";
    PIC12CodeGen codegen(cfg);

    tacky::Program program;
    tacky::Function func;
    func.name = "main";
    
    // TRISGPIO (0x86) = 0
    func.body.push_back(tacky::Copy{
        tacky::Constant{0},
        tacky::MemoryAddress{0x86}
    });
    program.functions.push_back(func);

    std::stringstream ss;
    codegen.compile(program, ss);

    std::string output = ss.str();
    EXPECT_NE(output.find("TRIS\t6"), std::string::npos);
    // Should also see shadow variable allocation
    EXPECT_NE(output.find("__tris_shadow_6"), std::string::npos);
}

TEST(PIC12CodeGenTest, NegationNoHardcoded) {
    DeviceConfig cfg;
    cfg.chip = "10f200";
    PIC12CodeGen codegen(cfg);

    tacky::Program program;
    tacky::Function func;
    func.name = "main";
    
    // x = -y
    func.body.push_back(tacky::Unary{
        tacky::UnaryOp::Neg,
        tacky::Variable{"y"},
        tacky::Variable{"x"}
    });
    program.functions.push_back(func);

    std::stringstream ss;
    codegen.compile(program, ss);

    std::string output = ss.str();
    // 0x07 was a hardcoded address in Unary::Neg, should be gone
    EXPECT_EQ(output.find("MOVWF\t0x07"), std::string::npos);
    EXPECT_NE(output.find("__neg_temp"), std::string::npos);
}
