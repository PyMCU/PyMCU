#include <gtest/gtest.h>

#include <cstdlib>
#include <fstream>
#include <string>
#include <vector>

class ClassesIntegrationTest : public ::testing::Test {
 protected:
  void SetUp() override {
    // storage for checked files
  }

  void TearDown() override {
    std::remove("temp_test.py");
    std::remove("temp_test.asm");
  }

  void writeSource(const std::string& code) {
    std::ofstream out("temp_test.py");
    out << code;
    out.close();
  }

  bool compile() {
    std::string command =
        std::string(PYMCUC_PATH) + " temp_test.py -o temp_test.asm";
    int result = std::system(command.c_str());
    return result == 0;
  }

  std::string readAssembly() {
    std::ifstream in("temp_test.asm");
    return std::string((std::istreambuf_iterator<char>(in)),
                       std::istreambuf_iterator<char>());
  }
};

#include <algorithm>

// Helper to remove whitespace for robust checking
std::string clean(std::string s) {
  s.erase(std::remove_if(s.begin(), s.end(), ::isspace), s.end());
  return s;
}

TEST_F(ClassesIntegrationTest, StaticClassFields) {
  writeSource(R"(
class MyDevice:
    DATA_PIN = 1
    CLK_PIN = 2

def main():
    x = MyDevice.DATA_PIN
    y = MyDevice.CLK_PIN
    )");

  ASSERT_TRUE(compile());
  std::string asmCode = readAssembly();
  std::string cleanAsm = clean(asmCode);

  // Check if constants are used (MOVLW 0x01, MOVLW 0x02)
  // ASM usually outputs hex: MOVLW 0x01
  bool found1 = (cleanAsm.find(clean("MOVLW 1")) != std::string::npos) ||
                (cleanAsm.find(clean("MOVLW 0x01")) != std::string::npos);
  bool found2 = (cleanAsm.find(clean("MOVLW 2")) != std::string::npos) ||
                (cleanAsm.find(clean("MOVLW 0x02")) != std::string::npos);

  if (!found1 || !found2) {
    std::cerr << "ASM DUMP (Fields):" << asmCode << std::endl;
  }
  EXPECT_TRUE(found1) << "Missing MOVLW 1";
  EXPECT_TRUE(found2) << "Missing MOVLW 2";
}

TEST_F(ClassesIntegrationTest, StaticClassMethods) {
  writeSource(R"(
class MathUtils:
    def add(a: uint8, b: uint8) -> uint8:
        return a + b

def main():
    res = MathUtils.add(10, 20)
    )");

  ASSERT_TRUE(compile());
  std::string asmCode = readAssembly();
  std::string cleanAsm = clean(asmCode);

  // Check for mangled function call: CALL MathUtils_add
  bool foundCall =
      (cleanAsm.find(clean("CALL MathUtils_add")) != std::string::npos);

  if (!foundCall) {
    std::cerr << "ASM DUMP (Methods):" << asmCode << std::endl;
  }
  EXPECT_TRUE(foundCall) << "Missing CALL MathUtils_add";
}

TEST_F(ClassesIntegrationTest, NestedStaticInModule) {
  writeSource(R"(
class LCD:
    RS = 4
    EN = 5
    
    def init():
        # Validate resolving static members inside method
        # and from main
        return LCD.RS

def main():
    x = LCD.init()
    y = LCD.EN
    )");

  ASSERT_TRUE(compile());
  std::string asmCode = readAssembly();
  std::string cleanAsm = clean(asmCode);

  bool foundCall = (cleanAsm.find(clean("CALL LCD_init")) != std::string::npos);
  bool foundConst1 =
      (cleanAsm.find(clean("MOVLW 0x04")) != std::string::npos);  // RS
  bool foundConst2 =
      (cleanAsm.find(clean("MOVLW 0x05")) != std::string::npos);  // EN

  if (!foundCall || !foundConst1 || !foundConst2) {
    std::cerr << "ASM DUMP (Nested):" << asmCode << std::endl;
  }
  EXPECT_TRUE(foundCall) << "Missing CALL LCD_init";
  EXPECT_TRUE(foundConst1) << "Missing MOVLW 0x04 (RS)";
  EXPECT_TRUE(foundConst2) << "Missing MOVLW 0x05 (EN)";
}

TEST_F(ClassesIntegrationTest, StaticMethodDecorator) {
  writeSource(R"(
class Math:
    @staticmethod
    def add(a: uint8, b: uint8) -> uint8:
        return a + b

def main():
    x = Math.add(5, 5)
    )");

  ASSERT_TRUE(compile());
  std::string asmCode = readAssembly();
  EXPECT_NE(clean(asmCode).find(clean("CALL Math_add")), std::string::npos);
}

TEST_F(ClassesIntegrationTest, InlineClassMethod) {
  writeSource(R"(
class GPIO:
    @inline
    def set_high(pin: uint8):
        # We use a dummy operation to verify inlining
        x = pin + 1

def main():
    GPIO.set_high(5)
    )");

  ASSERT_TRUE(compile());
  std::string asmCode = readAssembly();
  std::string cleanAsm = clean(asmCode);

  // Should NOT find CALL GPIO_set_high (it was inlined)
  EXPECT_EQ(cleanAsm.find(clean("CALL GPIO_set_high")), std::string::npos);
  // Constant folding: pin=5, 5+1=6 → MOVLW 0x06
  EXPECT_NE(cleanAsm.find(clean("MOVLW 0x06")), std::string::npos)
      << "Expected constant-folded MOVLW 0x06 (5+1=6)";
}
