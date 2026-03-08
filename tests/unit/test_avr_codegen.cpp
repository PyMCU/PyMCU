#include <gtest/gtest.h>

#include <sstream>

#include "backend/targets/avr/AVRCodeGen.h"
#include "ir/Tacky.h"

TEST(AVRCodeGenTest, SimpleAddition) {
  DeviceConfig config{.chip = "atmega328p"};
  config.chip = "atmega328p";
  AVRCodeGen codegen(config);

  tacky::Program program;
  tacky::Function main_func;
  main_func.name = "main";

  // a = 1 + 2
  tacky::Binary add_instr;
  add_instr.op = tacky::BinaryOp::Add;
  add_instr.src1 = tacky::Constant{1};
  add_instr.src2 = tacky::Constant{2};
  add_instr.dst = tacky::Variable{"a"};

  main_func.body.push_back(add_instr);
  main_func.body.push_back(tacky::Return{tacky::Constant{0}});

  program.functions.push_back(main_func);

  std::stringstream ss;
  codegen.compile(program, ss);

  std::string output = ss.str();
  EXPECT_NE(output.find("LDI\tR24, 1"), std::string::npos);
  EXPECT_NE(output.find("SUBI\tR24, 254"), std::string::npos);
  // Variable "a" is greedy-allocated to R4, so result is stored via MOV not STD
  EXPECT_NE(output.find("MOV\tR4, R24"), std::string::npos);
}

TEST(AVRCodeGenTest, IOOptimization) {
  DeviceConfig config{.chip = "atmega328p"};
  config.chip = "atmega328p";
  AVRCodeGen codegen(config);

  tacky::Program program;
  tacky::Function main_func;
  main_func.name = "main";

  // PORTB (0x25) = 1
  main_func.body.push_back(
      tacky::Copy{tacky::Constant{1}, tacky::MemoryAddress{0x25}});
  // DDRB (0x24) bit 0 = 1
  main_func.body.push_back(tacky::BitSet{tacky::MemoryAddress{0x24}, 0});

  program.functions.push_back(main_func);

  std::stringstream ss;
  codegen.compile(program, ss);

  std::string output = ss.str();
  // 0x25 (data) -> 0x05 (IO)
  EXPECT_NE(output.find("OUT\t0x05, R24"), std::string::npos);
  // 0x24 (data) -> 0x04 (IO). SBI 0x04, 0
  EXPECT_NE(output.find("SBI\t0x04, 0"), std::string::npos);
}

TEST(AVRCodeGenTest, ImmediateArithmetic) {
  DeviceConfig config{.chip = "atmega328p"};
  config.chip = "atmega328p";
  AVRCodeGen codegen(config);

  tacky::Program program;
  tacky::Function main_func;
  main_func.name = "main";

  // x = x & 0xF0
  tacky::Binary and_instr;
  and_instr.op = tacky::BinaryOp::BitAnd;
  and_instr.src1 = tacky::Variable{"x"};
  and_instr.src2 = tacky::Constant{0xF0};
  and_instr.dst = tacky::Variable{"x"};

  main_func.body.push_back(and_instr);
  program.functions.push_back(main_func);

  std::stringstream ss;
  codegen.compile(program, ss);

  std::string output = ss.str();
  EXPECT_NE(output.find("ANDI\tR24, 240"), std::string::npos);
}

TEST(AVRCodeGenTest, PeepholeRedundantLDI) {
  DeviceConfig config{.chip = "atmega328p"};
  config.chip = "atmega328p";
  AVRCodeGen codegen(config);

  tacky::Program program;
  tacky::Function main_func;
  main_func.name = "main";

  // a = 1
  // b = 1
  main_func.body.push_back(
      tacky::Copy{tacky::Constant{1}, tacky::Variable{"a"}});
  main_func.body.push_back(
      tacky::Copy{tacky::Constant{1}, tacky::Variable{"b"}});
  main_func.body.push_back(tacky::Return{std::monostate{}});

  program.functions.push_back(main_func);

  std::stringstream ss;
  codegen.compile(program, ss);

  std::string output = ss.str();
  // Second LDI R16, 1 should be optimized away
  size_t first = output.find("LDI\tR16, 1");
  size_t second = output.find("LDI\tR16, 1", first + 1);
  EXPECT_EQ(second, std::string::npos);
}