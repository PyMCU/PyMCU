#include <gtest/gtest.h>

#include <sstream>

#include "DeviceConfig.h"
#include "backend/CodeGenFactory.h"
#include "backend/targets/riscv/RISCVCodeGen.h"
#include "ir/Tacky.h"

TEST(RISCVCodeGenTest, SimpleReturn) {
  DeviceConfig cfg{.chip = "riscv32"};
  cfg.chip = "ch32v003";
  RISCVCodeGen codegen(cfg);

  tacky::Program program;
  tacky::Function func;
  func.name = "my_func";

  // return 42
  func.body.push_back(tacky::Return{tacky::Constant{42}});
  program.functions.push_back(func);

  std::stringstream ss;
  codegen.compile(program, ss);

  std::string output = ss.str();
  EXPECT_NE(output.find("li\ta0, 42"), std::string::npos);
  EXPECT_NE(output.find("ret"), std::string::npos);
}

TEST(RISCVCodeGenTest, MainInfiniteLoop) {
  DeviceConfig cfg{.chip = "riscv32"};
  cfg.chip = "ch32v003";
  RISCVCodeGen codegen(cfg);

  tacky::Program program;
  tacky::Function func;
  func.name = "main";

  func.body.push_back(tacky::Return{tacky::Constant{0}});
  program.functions.push_back(func);

  std::stringstream ss;
  codegen.compile(program, ss);

  std::string output = ss.str();
  // main should not have ret, but an infinite loop
  EXPECT_EQ(output.find("ret"), std::string::npos);
  EXPECT_NE(output.find("j\tend_loop"), std::string::npos);
  EXPECT_NE(output.find("li\tsp, 0x20000800"), std::string::npos);
}

TEST(RISCVCodeGenTest, NestedCallPrologue) {
  DeviceConfig cfg{.chip = "riscv32"};
  cfg.chip = "ch32v003";
  RISCVCodeGen codegen(cfg);

  tacky::Program program;
  tacky::Function func;
  func.name = "caller";

  // call other_func
  func.body.push_back(tacky::Call{"other_func", {}, std::monostate{}});
  func.body.push_back(tacky::Return{std::monostate{}});
  program.functions.push_back(func);

  std::stringstream ss;
  codegen.compile(program, ss);

  std::string output = ss.str();
  // caller is not leaf, should save/restore ra
  EXPECT_NE(output.find("sw\tra,"), std::string::npos);
  EXPECT_NE(output.find("lw\tra,"), std::string::npos);
}

TEST(RISCVCodeGenTest, SoftwareMul) {
  DeviceConfig cfg{.chip = "riscv32"};
  cfg.chip = "ch32v003";
  RISCVCodeGen codegen(cfg);

  tacky::Program program;
  tacky::Function func;
  func.name = "main";

  // a = b * c
  func.body.push_back(tacky::Binary{tacky::BinaryOp::Mul, tacky::Variable{"b"},
                                    tacky::Variable{"c"},
                                    tacky::Variable{"a"}});
  program.functions.push_back(func);

  std::stringstream ss;
  codegen.compile(program, ss);

  std::string output = ss.str();
  // Should NOT use mul instruction
  EXPECT_EQ(output.find("\tmul\t"), std::string::npos);
  // Should use call __mulsi3
  EXPECT_NE(output.find("call\t__mulsi3"), std::string::npos);
}

TEST(RISCVCodeGenTest, BinaryOps) {
  DeviceConfig cfg{.chip = "riscv32"};
  cfg.chip = "ch32v003";
  RISCVCodeGen codegen(cfg);

  tacky::Program program;
  tacky::Function func;
  func.name = "main";

  // a = 10 + 20
  func.body.push_back(tacky::Binary{tacky::BinaryOp::Add, tacky::Constant{10},
                                    tacky::Constant{20}, tacky::Variable{"a"}});
  func.body.push_back(tacky::Return{std::monostate{}});
  program.functions.push_back(func);

  std::stringstream ss;
  codegen.compile(program, ss);

  std::string output = ss.str();
  // Optimization: addi t0, zero, 10 followed by addi t0, t0, 20
  // Actually our load_into_reg for constant is li
  // And BinaryOp::Add with constant src2 uses addi if fits
  EXPECT_NE(output.find("li\tt0, 10"), std::string::npos);
  EXPECT_NE(output.find("addi\tt0, t0, 20"), std::string::npos);
  EXPECT_NE(output.find("sw\tt0, -12(s0)"), std::string::npos);
}

TEST(RISCVCodeGenTest, SubtractionOptimization) {
  DeviceConfig cfg{.chip = "riscv32"};
  cfg.chip = "ch32v003";
  RISCVCodeGen codegen(cfg);

  tacky::Program program;
  tacky::Function func;
  func.name = "main";

  // a = b - 10
  func.body.push_back(tacky::Binary{tacky::BinaryOp::Sub, tacky::Variable{"b"},
                                    tacky::Constant{10}, tacky::Variable{"a"}});
  func.body.push_back(tacky::Return{std::monostate{}});
  program.functions.push_back(func);

  std::stringstream ss;
  codegen.compile(program, ss);

  std::string output = ss.str();
  // Should use addi with -10
  EXPECT_NE(output.find("addi\tt0, t0, -10"), std::string::npos);
}

TEST(RISCVCodeGenTest, BitManipulation) {
  DeviceConfig cfg{.chip = "riscv32"};
  cfg.chip = "ch32v003";
  RISCVCodeGen codegen(cfg);

  tacky::Program program;
  tacky::Function func;
  func.name = "main";

  // Set bit 5 of variable 'x'
  func.body.push_back(tacky::BitSet{tacky::Variable{"x"}, 5});
  func.body.push_back(tacky::Return{std::monostate{}});
  program.functions.push_back(func);

  std::stringstream ss;
  codegen.compile(program, ss);

  std::string output = ss.str();
  // x is at 0(s0), bit 5 is 1<<5 = 32
  EXPECT_NE(output.find("li\tt1, 32"), std::string::npos);
  EXPECT_NE(output.find("or\tt0, t0, t1"), std::string::npos);
}

TEST(RISCVCodeGenTest, FactorySupport) {
  DeviceConfig cfg{.chip = "riscv32"};
  cfg.chip = "ch32v003";
  auto codegen = CodeGenFactory::create("ch32v003", cfg);
  EXPECT_NE(dynamic_cast<RISCVCodeGen *>(codegen.get()), nullptr);
}