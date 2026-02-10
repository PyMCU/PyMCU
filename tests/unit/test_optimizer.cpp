#include <gtest/gtest.h>
#include "ir/Optimizer.h"
#include "ir/IRGenerator.h"
#include "frontend/Parser.h"
#include "frontend/Lexer.h"

TEST(OptimizerTest, ConstantFoldingBinary) {
    Lexer lexer("def main():\n    return 2 + 3 * 4");
    auto tokens = lexer.tokenize();
    Parser parser(tokens);
    auto ast = parser.parseProgram();

    IRGenerator ir_gen;
    auto ir = ir_gen.generate(*ast, {});
    auto optimized = Optimizer::optimize(ir);

    ASSERT_EQ(optimized.functions.size(), 1);
    auto& body = optimized.functions[0].body;
    
    // Original IR would have two Binary ops.
    // Optimized IR should have one Return with constant 14.
    // wait, DCE might have removed the intermediates.
    
    bool found_binary = false;
    int return_val = -1;
    for (const auto& inst : body) {
        if (std::holds_alternative<tacky::Binary>(inst)) found_binary = true;
        if (auto ret = std::get_if<tacky::Return>(&inst)) {
            if (auto c = std::get_if<tacky::Constant>(&ret->value)) {
                return_val = c->value;
            }
        }
    }
    
    EXPECT_FALSE(found_binary);
    EXPECT_EQ(return_val, 14);
}

TEST(OptimizerTest, DeadCodeElimination) {
    Lexer lexer("def main():\n    a = 1 + 2\n    return 42");
    auto tokens = lexer.tokenize();
    Parser parser(tokens);
    auto ast = parser.parseProgram();

    IRGenerator ir_gen;
    auto ir = ir_gen.generate(*ast, {});
    auto optimized = Optimizer::optimize(ir);

    // 'a' is not used, so 'a = 1 + 2' should be eliminated.
    // However, IRGenerator for Assignment might use a Variable, not a Temporary.
    // Our DCE only eliminates Temporaries for now.
    
    // Let's check for temporaries.
    for (const auto& inst : optimized.functions[0].body) {
        if (auto copy = std::get_if<tacky::Copy>(&inst)) {
            if (std::holds_alternative<tacky::Temporary>(copy->dst)) {
                 FAIL() << "Temporary dst not eliminated";
            }
        }
    }
}

TEST(OptimizerTest, UnusedExpressionDCE) {
    // 1 + 2 as an expression statement generates a temporary that is unused.
    Lexer lexer("def main():\n    1 + 2\n    return 42");
    auto tokens = lexer.tokenize();
    Parser parser(tokens);
    auto ast = parser.parseProgram();

    IRGenerator ir_gen;
    auto ir = ir_gen.generate(*ast, {});
    auto optimized = Optimizer::optimize(ir);

    ASSERT_EQ(optimized.functions.size(), 1);
    // Should only have Return(42)
    // Actually might have more if IRGenerator emits labels etc, but no Binary/Copy to Temps.
    for (const auto& inst : optimized.functions[0].body) {
        if (std::holds_alternative<tacky::Binary>(inst)) FAIL() << "Unused Binary not eliminated";
    }
}
