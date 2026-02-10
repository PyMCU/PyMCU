#include <gtest/gtest.h>
#include <sstream>
#include "backend/targets/pic18/PIC18CodeGen.h"
#include "backend/CodeGenFactory.h"
#include "ir/Tacky.h"
#include "DeviceConfig.h"

TEST(PIC18CodeGenTest, SimpleReturn) {
    DeviceConfig cfg;
    cfg.chip = "18f45k50";
    PIC18CodeGen codegen(cfg);

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
    EXPECT_NE(output.find("RETURN"), std::string::npos);
    EXPECT_NE(output.find("P18F45K50.INC"), std::string::npos);
}

TEST(PIC18CodeGenTest, MOVFF_Optimization) {
    DeviceConfig cfg;
    cfg.chip = "18f45k50";
    PIC18CodeGen codegen(cfg);

    tacky::Program program;
    tacky::Function func;
    func.name = "main";
    
    // x = y (Copy)
    func.body.push_back(tacky::Copy{
        tacky::Variable{"y"},
        tacky::Variable{"x"}
    });
    func.body.push_back(tacky::Return{std::monostate{}});
    program.functions.push_back(func);

    std::stringstream ss;
    codegen.compile(program, ss);

    std::string output = ss.str();
    // Should use MOVFF
    EXPECT_NE(output.find("MOVFF\ty, x"), std::string::npos);
}

TEST(PIC18CodeGenTest, Redundant_MOVFF_Removed) {
    DeviceConfig cfg;
    cfg.chip = "18f45k50";
    PIC18CodeGen codegen(cfg);

    tacky::Program program;
    tacky::Function func;
    func.name = "main";
    
    // x = x (Redundant Copy)
    func.body.push_back(tacky::Copy{
        tacky::Variable{"x"},
        tacky::Variable{"x"}
    });
    func.body.push_back(tacky::Return{std::monostate{}});
    program.functions.push_back(func);

    std::stringstream ss;
    codegen.compile(program, ss);

    std::string output = ss.str();
    // MOVFF x, x should be removed by peephole
    EXPECT_EQ(output.find("MOVFF\tx, x"), std::string::npos);
}

TEST(PIC18CodeGenTest, ArithmeticAndFactory) {
    DeviceConfig cfg;
    cfg.chip = "pic18f45k50";
    auto codegen = CodeGenFactory::create("pic18f45k50", cfg);
    EXPECT_NE(dynamic_cast<PIC18CodeGen*>(codegen.get()), nullptr);

    tacky::Program program;
    tacky::Function func;
    func.name = "main";
    
    // a = 10 + b
    func.body.push_back(tacky::Binary{
        tacky::BinaryOp::Add,
        tacky::Constant{10},
        tacky::Variable{"b"},
        tacky::Variable{"a"}
    });
    func.body.push_back(tacky::Return{std::monostate{}});
    program.functions.push_back(func);

    std::stringstream ss;
    codegen->compile(program, ss);

    std::string output = ss.str();
    EXPECT_NE(output.find("MOVLW\t0x0A"), std::string::npos);
    EXPECT_NE(output.find("ADDWF\tb, W, ACCESS"), std::string::npos);
    EXPECT_NE(output.find("MOVWF\ta, ACCESS"), std::string::npos);
}
