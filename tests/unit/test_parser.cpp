#include <gtest/gtest.h>
#include "frontend/Lexer.h"
#include "frontend/Parser.h"
#include "Errors.h"

static std::unique_ptr<Program> parse(std::string_view source) {
    Lexer lexer(source);
    auto tokens = lexer.tokenize();
    Parser parser(tokens);
    return parser.parseProgram();
}

TEST(ParserTest, EmptyProgram) {
    auto prog = parse("");
    EXPECT_TRUE(prog->functions.empty());
}

TEST(ParserTest, SingleFunction) {
    auto prog = parse("def main():\n    return 42");
    ASSERT_EQ(prog->functions.size(), 1);
    EXPECT_EQ(prog->functions[0]->name, "main");
    ASSERT_EQ(prog->functions[0]->body.size(), 1);
    
    auto* returnStmt = dynamic_cast<ReturnStmt*>(prog->functions[0]->body[0].get());
    ASSERT_NE(returnStmt, nullptr);
    
    auto* numExpr = dynamic_cast<NumberExpr*>(returnStmt->value.get());
    ASSERT_NE(numExpr, nullptr);
    EXPECT_EQ(numExpr->value, 42);
}

TEST(ParserTest, MultipleFunctions) {
    auto prog = parse("def a():\n    return 1\n\ndef b():\n    return 2");
    ASSERT_EQ(prog->functions.size(), 2);
    EXPECT_EQ(prog->functions[0]->name, "a");
    EXPECT_EQ(prog->functions[1]->name, "b");
}

TEST(ParserTest, NumberBases) {
    auto prog = parse("def f():\n    return 0x10\n    return 0b10\n    return 0o10\n    return 1_000");
    ASSERT_EQ(prog->functions.size(), 1);
    auto& body = prog->functions[0]->body;
    ASSERT_EQ(body.size(), 4);
    
    EXPECT_EQ(dynamic_cast<NumberExpr*>(dynamic_cast<ReturnStmt*>(body[0].get())->value.get())->value, 16);
    EXPECT_EQ(dynamic_cast<NumberExpr*>(dynamic_cast<ReturnStmt*>(body[1].get())->value.get())->value, 2);
    EXPECT_EQ(dynamic_cast<NumberExpr*>(dynamic_cast<ReturnStmt*>(body[2].get())->value.get())->value, 8);
    EXPECT_EQ(dynamic_cast<NumberExpr*>(dynamic_cast<ReturnStmt*>(body[3].get())->value.get())->value, 1000);
}

TEST(ParserTest, SyntaxError) {
    EXPECT_THROW(parse("def main("), SyntaxError);
    EXPECT_THROW(parse("def main(): return 1"), SyntaxError); // Block must be indented on new line
}

TEST(ParserTest, IndentationError) {
    EXPECT_THROW(parse("def main():\nreturn 1"), IndentationError);
}

TEST(ParserTest, NestedBlocksAreNotSupportedYet) {
    // Current parser only supports flat list of statements in a function
    // But Lexer handles nested indentation.
    // Let's see how Parser handles it.
    // Currently Parser::parseBlock() calls parseStatement() in a loop until Dedent.
    // If it encounters another 'def' inside, parseStatement() will fail because it doesn't expect 'def'.
    
    EXPECT_THROW(parse("def a():\n    def b():\n        return 1"), SyntaxError);
}
