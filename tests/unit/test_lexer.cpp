#include <gtest/gtest.h>
#include "frontend/Lexer.h"
#include "Errors.h"

TEST(LexerTest, BasicTokens) {
    Lexer lexer("def main():\n    return 42");
    auto tokens = lexer.tokenize();

    ASSERT_EQ(tokens.size(), 12);
    EXPECT_EQ(tokens[0].type, TokenType::Def);
    EXPECT_EQ(tokens[1].type, TokenType::Identifier);
    EXPECT_EQ(tokens[1].value, "main");
    EXPECT_EQ(tokens[2].type, TokenType::LParen);
    EXPECT_EQ(tokens[3].type, TokenType::RParen);
    EXPECT_EQ(tokens[4].type, TokenType::Colon);
    EXPECT_EQ(tokens[5].type, TokenType::Newline);
    EXPECT_EQ(tokens[6].type, TokenType::Indent);
    EXPECT_EQ(tokens[7].type, TokenType::Return);
    EXPECT_EQ(tokens[8].type, TokenType::Number);
    EXPECT_EQ(tokens[8].value, "42");
    EXPECT_EQ(tokens[9].type, TokenType::Newline);
    EXPECT_EQ(tokens[10].type, TokenType::Dedent);
    EXPECT_EQ(tokens[11].type, TokenType::EndOfFile);
}

TEST(LexerTest, Indentation) {
    Lexer lexer("def a():\n    def b():\n        return 1\n    return 2");
    const auto tokens = lexer.tokenize();

    // Check for Indent/Dedent sequence
    // def a(): \n
    // INDENT def b(): \n
    // INDENT return 1 \n
    // DEDENT return 2 \n
    // DEDENT EOF

    bool found_indent = false;
    bool found_dedent = false;
    for (const auto &t: tokens) {
        if (t.type == TokenType::Indent) found_indent = true;
        if (t.type == TokenType::Dedent) found_dedent = true;
    }
    EXPECT_TRUE(found_indent);
    EXPECT_TRUE(found_dedent);
}

TEST(LexerTest, Numbers) {
    Lexer lexer("0 123 0x10 0b1010 0o77 1_000");
    const auto tokens = lexer.tokenize();

    ASSERT_GE(tokens.size(), 6);
    EXPECT_EQ(tokens[0].value, "0");
    EXPECT_EQ(tokens[1].value, "123");
    EXPECT_EQ(tokens[2].value, "0x10");
    EXPECT_EQ(tokens[3].value, "0b1010");
    EXPECT_EQ(tokens[4].value, "0o77");
    EXPECT_EQ(tokens[5].value, "1_000");
}

TEST(LexerTest, InvalidCharacter) {
    Lexer lexer("$");
    EXPECT_THROW(lexer.tokenize(), LexicalError);
}

TEST(LexerTest, InvalidNumber) {
    Lexer lexer("0123"); // Leading zero in decimal
    EXPECT_THROW(lexer.tokenize(), LexicalError);
}

TEST(LexerTest, Comments) {
    Lexer lexer("def # comment\n    return 1");
    const auto tokens = lexer.tokenize();

    EXPECT_EQ(tokens[0].type, TokenType::Def);
    EXPECT_EQ(tokens[1].type, TokenType::Newline);
}