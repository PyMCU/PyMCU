#include <gtest/gtest.h>
#include "backend/targets/pic14/PIC14CodeGen.h"
#include <sstream>

TEST(PIC14CodeGenTest, SimpleReturn) {
    tacky::Program program;
    tacky::Function func;
    func.name = "main";
    func.body.emplace_back(tacky::Return{tacky::Constant{42}});
    program.functions.push_back(func);

    PIC14CodeGen codegen;
    std::stringstream ss;
    codegen.compile(program, ss);

    std::string asm_code = ss.str();
    EXPECT_NE(asm_code.find("MOVLW\t0x2A"), std::string::npos);
    EXPECT_NE(asm_code.find("RETURN"), std::string::npos);
    EXPECT_NE(asm_code.find("main"), std::string::npos);
}

TEST(PIC14CodeGenTest, MultipleFunctions) {
    tacky::Program program;
    
    tacky::Function func1;
    func1.name = "f1";
    func1.body.emplace_back(tacky::Return{tacky::Constant{1}});
    program.functions.push_back(func1);

    tacky::Function func2;
    func2.name = "f2";
    func2.body.emplace_back(tacky::Return{tacky::Constant{2}});
    program.functions.push_back(func2);

    PIC14CodeGen codegen;
    std::stringstream ss;
    codegen.compile(program, ss);

    std::string asm_code = ss.str();
    EXPECT_NE(asm_code.find("f1"), std::string::npos);
    EXPECT_NE(asm_code.find("f2"), std::string::npos);
}
