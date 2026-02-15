#include <gtest/gtest.h>

#include <sstream>

#include "DeviceConfig.h"
#include "backend/targets/pic14/PIC14CodeGen.h"

TEST(PIC14CodeGenTest, SimpleReturn) {
  tacky::Program program;
  tacky::Function func;
  func.name = "main";
  func.body.emplace_back(tacky::Return{tacky::Constant{42}});
  program.functions.push_back(func);

  DeviceConfig config{.chip = "pic16f84a"};
  PIC14CodeGen codegen(config);
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

  DeviceConfig config{.chip = "pic16f84a"};
  PIC14CodeGen codegen(config);
  std::stringstream ss;
  codegen.compile(program, ss);

  std::string asm_code = ss.str();
  EXPECT_NE(asm_code.find("f1"), std::string::npos);
  EXPECT_NE(asm_code.find("f2"), std::string::npos);
}

TEST(PIC14CodeGenTest, ControlFlow) {
  tacky::Program program;
  tacky::Function func;
  func.name = "f";
  // Loop: L1 -> BSF -> GOTO L1
  func.body.emplace_back(tacky::Label{"L1"});
  func.body.emplace_back(tacky::BitSet{tacky::MemoryAddress{0x05}, 0});
  func.body.emplace_back(tacky::Jump{"L1"});
  program.functions.push_back(func);

  DeviceConfig config{.chip = "pic16f84a"};
  PIC14CodeGen codegen(config);
  std::stringstream ss;
  codegen.compile(program, ss);

  std::string asm_code = ss.str();
  EXPECT_NE(asm_code.find("GOTO"), std::string::npos);
  EXPECT_NE(asm_code.find("L1"), std::string::npos);
}

TEST(PIC14CodeGenTest, UnaryOps) {
  tacky::Program program;
  tacky::Function func;
  func.name = "f";
  func.body.emplace_back(tacky::Unary{tacky::UnaryOp::Neg, tacky::Constant{5},
                                      tacky::Variable{"x"}});
  program.functions.push_back(func);

  DeviceConfig config{.chip = "pic16f84a"};
  PIC14CodeGen codegen(config);
  std::stringstream ss;
  codegen.compile(program, ss);

  std::string asm_code = ss.str();
  EXPECT_NE(asm_code.find("SUBLW\t0"), std::string::npos);
}

TEST(PIC14CodeGenTest, BinaryOps) {
  tacky::Program program;
  tacky::Function func;
  func.name = "f";
  func.body.emplace_back(tacky::Binary{tacky::BinaryOp::Add, tacky::Constant{1},
                                       tacky::Constant{2},
                                       tacky::Variable{"x"}});
  program.functions.push_back(func);

  DeviceConfig config{.chip = "pic16f84a"};
  PIC14CodeGen codegen(config);
  std::stringstream ss;
  codegen.compile(program, ss);

  std::string asm_code = ss.str();
  EXPECT_NE(asm_code.find("ADDLW\t0x02"), std::string::npos);
}

TEST(PIC14CodeGenTest, ComparisonOps) {
  tacky::Program program;
  tacky::Function func;
  func.name = "f";
  // x = (1 == 1)
  func.body.emplace_back(tacky::Binary{tacky::BinaryOp::Equal,
                                       tacky::Constant{1}, tacky::Constant{1},
                                       tacky::Variable{"x"}});
  program.functions.push_back(func);

  DeviceConfig config{.chip = "pic16f84a"};
  PIC14CodeGen codegen(config);
  std::stringstream ss;
  codegen.compile(program, ss);

  std::string asm_code = ss.str();
  // For Equal, we want dst=1 if Z=1.
  // CLRF x
  // BTFSC STATUS, 2 (Skip if Z=0)
  // INCF x, F
  EXPECT_NE(asm_code.find("BTFSC\tSTATUS, 2"), std::string::npos);
}

TEST(PIC14CodeGenTest, BitManipulation) {
  tacky::Program program;
  tacky::Function func;
  func.name = "f";
  func.body.emplace_back(
      tacky::BitSet{tacky::MemoryAddress{0x05}, 0});  // PORTA bit 0
  func.body.emplace_back(tacky::BitClear{tacky::MemoryAddress{0x05}, 1});
  program.functions.push_back(func);

  DeviceConfig config{.chip = "pic16f84a"};
  PIC14CodeGen codegen(config);
  std::stringstream ss;
  codegen.compile(program, ss);

  std::string asm_code = ss.str();
  EXPECT_NE(asm_code.find("BSF\t0x05, 0"), std::string::npos);
  EXPECT_NE(asm_code.find("BCF\t0x05, 1"), std::string::npos);
}