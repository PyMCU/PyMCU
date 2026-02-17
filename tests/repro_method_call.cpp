#include <cassert>
#include <iostream>

#include "../src/compiler/frontend/Ast.h"
#include "../src/compiler/frontend/Lexer.h"
#include "../src/compiler/frontend/Parser.h"

int main() {
  std::string source = "obj.method()";
  Lexer lexer(source);
  auto tokens = lexer.tokenize();
  Parser parser(tokens);

  try {
    auto expr = parser.parseExpression();
    // We expect a CallExpr
    auto* call = dynamic_cast<CallExpr*>(expr.get());
    if (!call) {
      std::cerr << "FAILED: Expected CallExpr, got something else."
                << std::endl;
      return 1;
    }
    std::cout << "PASSED: Successfully parsed method call." << std::endl;
  } catch (const std::exception& e) {
    std::cerr << "FAILED: Exception during parsing: " << e.what() << std::endl;
    return 1;
  }
  return 0;
}
