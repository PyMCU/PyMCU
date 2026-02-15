#include <gtest/gtest.h>

#include <sstream>

#include "DeviceConfig.h"
#include "backend/CodeGenFactory.h"
#include "backend/targets/pic18/PIC18CodeGen.h"
#include "ir/Tacky.h"

TEST(PIC18CodeGenTest, SimpleReturn) {
  DeviceConfig cfg{.chip = "pic18f4550"};
  cfg.chip = "pic18f45k50";
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
  EXPECT_NE(output.find("p18f45k50.inc"), std::string::npos);
}

TEST(PIC18CodeGenTest, MOVFF_Optimization) {
  DeviceConfig cfg{.chip = "pic18f4550"};
  cfg.chip = "pic18f45k50";
  PIC18CodeGen codegen(cfg);

  tacky::Program program;
  tacky::Function func;
  func.name = "main";

  // x = y (Copy)
  func.body.push_back(tacky::Copy{tacky::Variable{"y"}, tacky::Variable{"x"}});
  func.body.push_back(tacky::Return{std::monostate{}});
  program.functions.push_back(func);

  std::stringstream ss;
  codegen.compile(program, ss);

  std::string output = ss.str();
  // Should use MOVFF
  EXPECT_NE(output.find("MOVFF\ty, x"), std::string::npos);
}

TEST(PIC18CodeGenTest, Redundant_MOVFF_Removed) {
  DeviceConfig cfg{.chip = "pic18f4550"};
  cfg.chip = "pic18f45k50";
  PIC18CodeGen codegen(cfg);

  tacky::Program program;
  tacky::Function func;
  func.name = "main";

  // x = x (Redundant Copy)
  func.body.push_back(tacky::Copy{tacky::Variable{"x"}, tacky::Variable{"x"}});
  func.body.push_back(tacky::Return{std::monostate{}});
  program.functions.push_back(func);

  std::stringstream ss;
  codegen.compile(program, ss);

  std::string output = ss.str();
  // MOVFF x, x should be removed by peephole
  EXPECT_EQ(output.find("MOVFF\tx, x"), std::string::npos);
}

TEST(PIC18CodeGenTest, ArithmeticAndFactory) {
  DeviceConfig cfg{.chip = "pic18f4550"};
  cfg.chip = "pic18f45k50";
  auto codegen = CodeGenFactory::create("pic18f45k50", cfg);
  EXPECT_NE(dynamic_cast<PIC18CodeGen*>(codegen.get()), nullptr);

  tacky::Program program;
  tacky::Function func;
  func.name = "main";

  // a = 10 + b
  func.body.push_back(tacky::Binary{tacky::BinaryOp::Add, tacky::Constant{10},
                                    tacky::Variable{"b"},
                                    tacky::Variable{"a"}});
  func.body.push_back(tacky::Return{std::monostate{}});
  program.functions.push_back(func);

  std::stringstream ss;
  codegen->compile(program, ss);

  std::string output = ss.str();
  EXPECT_NE(output.find("MOVLW\t0x0A"), std::string::npos);
  EXPECT_NE(output.find("ADDWF\tb, W"), std::string::npos);
  EXPECT_NE(output.find("MOVWF\ta"), std::string::npos);
}

TEST(PIC18CodeGenTest, BankedAccess) {
  DeviceConfig cfg{.chip = "pic18f4550"};
  cfg.chip = "pic18f45k50";
  PIC18CodeGen codegen(cfg);

  tacky::Program program;
  tacky::Function func;
  func.name = "main";

  // Force many variables to exceed access bank (0x60)
  for (int i = 0; i < 100; ++i) {
    func.body.push_back(tacky::Copy{tacky::Constant{0},
                                    tacky::Variable{"v" + std::to_string(i)}});
  }
  // v99 should be at 0x60 + 99 = 0xC3 (still bank 0)
  // Let's add more to reach bank 1 (addr >= 0x100)
  for (int i = 100; i < 200; ++i) {
    func.body.push_back(tacky::Copy{tacky::Constant{0},
                                    tacky::Variable{"v" + std::to_string(i)}});
  }

  program.functions.push_back(func);

  std::stringstream ss;
  codegen.compile(program, ss);

  std::string output = ss.str();
  // v199 should be at 0x60 + 199 = 259 = 0x103
  EXPECT_NE(output.find("MOVLB\t1"), std::string::npos);
  EXPECT_NE(output.find("MOVWF\tv199, BANKED"), std::string::npos);
}

TEST(PIC18CodeGenTest, SubtractionCorrectness) {
  DeviceConfig cfg{.chip = "pic18f4550"};
  cfg.chip = "pic18f45k50";
  PIC18CodeGen codegen(cfg);

  tacky::Program program;
  tacky::Function func;
  func.name = "main";

  // a = b - c
  func.body.push_back(tacky::Binary{tacky::BinaryOp::Sub, tacky::Variable{"b"},
                                    tacky::Variable{"c"},
                                    tacky::Variable{"a"}});
  program.functions.push_back(func);

  std::stringstream ss;
  codegen.compile(program, ss);

  std::string output = ss.str();
  // Strategy: Load c into W, then SUBWF b, W (b - W -> W)
  size_t pos_c = output.find("MOVF\tc, W");
  size_t pos_sub = output.find("SUBWF\tb, W");
  size_t pos_a = output.find("MOVWF\ta");

  EXPECT_NE(pos_c, std::string::npos);
  EXPECT_NE(pos_sub, std::string::npos);
  EXPECT_NE(pos_a, std::string::npos);
  EXPECT_LT(pos_c, pos_sub);
  EXPECT_LT(pos_sub, pos_a);
}

TEST(PIC18CodeGenTest, Division) {
  DeviceConfig cfg{.chip = "pic18f4550"};
  cfg.chip = "pic18f45k50";
  PIC18CodeGen codegen(cfg);

  tacky::Program program;
  tacky::Function func;
  func.name = "main";

  // a = b / c
  func.body.push_back(tacky::Binary{tacky::BinaryOp::Div, tacky::Variable{"b"},
                                    tacky::Variable{"c"},
                                    tacky::Variable{"a"}});
  program.functions.push_back(func);

  std::stringstream ss;
  codegen.compile(program, ss);

  std::string output = ss.str();
  EXPECT_NE(output.find("div_loop"), std::string::npos);
  EXPECT_NE(output.find("SUBWF"), std::string::npos);
  EXPECT_NE(output.find("BN"), std::string::npos);
}
