#include <gtest/gtest.h>

#include <cassert>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <sstream>
#include <string>
#include <vector>

// Simula la llamada al compilador (asumiendo que puedes linkear contra tu lib
// del compilador) O invoca el binario compilado mediante system() si es un test
// de caja negra. Aquí usaremos system() para ser verdaderamente E2E.

namespace fs = std::filesystem;

void write_file(const std::string& filename, const std::string& content) {
  std::ofstream out(filename);
  out << content;
  out.close();
}

void assert_contains(const std::string& content, const std::string& substring,
                     const std::string& context) {
  if (content.find(substring) == std::string::npos) {
    FAIL() << "[FAIL] " << context << "\n"
           << "       Expected to find: '" << substring << "'\n"
           << "       But it was missing from the output ASM.";
  }
}

void assert_not_contains(const std::string& content,
                         const std::string& substring,
                         const std::string& context) {
  if (content.find(substring) != std::string::npos) {
    FAIL() << "[FAIL] " << context << "\n"
           << "       Found forbidden string: '" << substring << "'\n"
           << "       Legacy code generation detected where Enhanced was "
              "expected.";
  }
}

#include <gtest/gtest.h>

TEST(IntegrationTest, PIC14E) {
  std::cout << "[TEST] Starting Strict PIC14E Integration Test...\n";

  // 1. Setup Environment
  fs::create_directories("test_env/whipsnake/chips");

  // Create Chip Definition (Mocking the real one)
  write_file("test_env/whipsnake/chips/pic16f18877.py", R"(
from whipsnake.types import ptr, uint8, device_info
device_info(arch="pic14e", chip="pic16f18877", ram_size=4096, flash_size=16384)
# Minimal registers for test
RC6PPS: ptr[uint8] = ptr(0x1F26) # Bank 62
TRISC: ptr[uint8] = ptr(0x0013)  # Bank 0
)");

  // Create User Code
  write_file("test_env/main.py", R"(
from whipsnake.chips.pic16f18877 import *
def main():
    # Access Bank 62 (Requires MOVLB 62)
    RC6PPS.value = 0x10
    # Access Bank 0 (Requires MOVLB 0 or Common)
    TRISC.value = 0
)");

// 2. Execute Compiler
#ifdef WHIPC_PATH
  std::string cmd =
      std::string(WHIPC_PATH) +
      " test_env/main.py -o test_env/output.asm -I test_env --arch pic16f877a";
#else
  std::string cmd =
      "./whipc test_env/main.py -o test_env/output.asm -I test_env --arch "
      "pic16f877a";
#endif

  std::cout << "[TEST] Running compiler: " << cmd << "\n";
  int ret = system(cmd.c_str());

  if (ret != 0) {
    FAIL() << "[FAIL] Compiler crashed or returned non-zero exit code: " << ret;
  }

  // 3. Forensic Analysis
  std::ifstream ifs("test_env/output.asm");
  std::stringstream buffer;
  buffer << ifs.rdbuf();
  std::string asm_content = buffer.str();

  std::cout << "[TEST] Analyzing Output ASM...\n";

  // CHECK 1: Correct Header (Must override CLI)
  assert_contains(asm_content, "LIST P=pic16f18877", "Header Verification");
  assert_contains(asm_content, "#include <p16f18877.inc>",
                  "Include Verification");

  // CHECK 2: Enhanced Banking (MOVLB)
  assert_contains(asm_content, "MOVLB", "Instruction Verification");

  // Specific Bank Check
  if (asm_content.find("MOVLB\t62") == std::string::npos &&
      asm_content.find("MOVLB 62") == std::string::npos &&
      asm_content.find("MOVLB 0x3E") == std::string::npos) {
    FAIL() << "[FAIL] Bank Selection Verification\n"
           << "       Expected 'MOVLB 62' for address 0x1F26.";
  }

  std::cout << "[PASS] All strict checks passed. The compiler is correctly "
               "generating PIC14E code.\n";
}