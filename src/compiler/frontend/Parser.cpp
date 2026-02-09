#include "Parser.h"
#include "Errors.h"
#include <stdexcept>
#include <format>
#include <algorithm>

Parser::Parser(const std::vector<Token>& tokens) : tokens(tokens), pos(0) {}

const Token& Parser::peek() const {
    if (pos >= tokens.size()) return tokens.back();
    return tokens[pos];
}

const Token& Parser::previous() const {
    if (pos == 0) return tokens[0];
    return tokens[pos - 1];
}

Token Parser::advance() {
    if (pos < tokens.size()) pos++;
    return tokens[pos - 1];
}

bool Parser::check(const TokenType type) const {
    return peek().type == type;
}

bool Parser::match(const TokenType type) {
    if (check(type)) {
        advance();
        return true;
    }
    return false;
}

Token Parser::consume(const TokenType type, const std::string_view errorMessage) {
    if (check(type)) return advance();
    error(errorMessage);
}

void Parser::consumeStatementEnd() {
    if (match(TokenType::Semicolon)) {
        match(TokenType::Newline);
        return;
    }
    if (match(TokenType::Newline)) return;
    if (check(TokenType::Dedent)) return;
    if (check(TokenType::EndOfFile)) return;

    error("Expected newline or end of block");
}

void Parser::error(const std::string_view message) const {
    const auto& t = peek();
    if (t.type == TokenType::EndOfFile) {
        throw SyntaxError("Unexpected EOF while parsing", t.line, t.column);
    }
    throw SyntaxError(std::string(message), t.line, t.column);
}

void Parser::indentError(const std::string_view message) const {
    const auto& t = peek();
    throw IndentationError(std::string(message), t.line, t.column);
}

std::string Parser::parseTypeAnnotation() {
    const Token t = consume(TokenType::Identifier, "Expected type identifier");
    std::string typeStr = t.value;

    if (match(TokenType::LBracket)) {
        typeStr += "[";
        const Token inner = consume(TokenType::Identifier, "Expected inner type");
        typeStr += inner.value;
        consume(TokenType::RBracket, "Expected ']'");
        typeStr += "]";
    }
    return typeStr;
}

std::unique_ptr<Program> Parser::parseProgram() {
    auto prog = std::make_unique<Program>();

    while (!check(TokenType::EndOfFile)) {
        if (match(TokenType::Newline)) continue;

        if (check(TokenType::From)) {
            prog->imports.push_back(parseImportStatement());
        }
        else if (check(TokenType::Def)) {
            prog->functions.push_back(parseFunction());
        }
        else if (check(TokenType::Identifier)) {
            prog->global_statements.push_back(parseAssignmentOrDeclaration());
        }
        else {
            error("Expected function definition ('def')");
        }
    }
    return prog;
}

std::unique_ptr<FunctionDef> Parser::parseFunction() {
    consume(TokenType::Def, "Expected 'def'");
    const Token nameToken = consume(TokenType::Identifier, "Expected function name");

    consume(TokenType::LParen, "Expected '('");
    auto params = parseParameters();
    consume(TokenType::RParen, "Expected ')'");

    std::string returnType = "void";
    if (match(TokenType::Arrow)) {
        returnType = parseTypeAnnotation();
    }

    consume(TokenType::Colon, "Expected ':'");
    consume(TokenType::Newline, "Expected newline after function definition");

    auto bodyBlock = parseBlock();

    return std::make_unique<FunctionDef>(nameToken.value, std::move(params), returnType, std::move(bodyBlock));
}

std::vector<Param> Parser::parseParameters() {
    std::vector<Param> params;

    if (check(TokenType::RParen)) {
        return params;
    }

    do {
        const Token name = consume(TokenType::Identifier, "Expected parameter name");
        consume(TokenType::Colon, "Parameter type is required (e.g. 'a: int')");
        const std::string type = parseTypeAnnotation();

        params.push_back({name.value, type});
    } while (match(TokenType::Comma));

    return params;
}

std::unique_ptr<Block> Parser::parseBlock() {
    if (!match(TokenType::Indent)) {
        indentError("Expected an indented block");
    }

    auto block = std::make_unique<Block>();

    while (!check(TokenType::Dedent) && !check(TokenType::EndOfFile)) {
        if (match(TokenType::Newline)) continue; // Skip empty lines
        block->statements.push_back(parseStatement());
    }

    if (!match(TokenType::Dedent) && !check(TokenType::EndOfFile)) {
        indentError("Unindent does not match any outer indentation level");
    }

    return block;
}

std::unique_ptr<Statement> Parser::parseStatement() {
    if (check(TokenType::If)) return parseIfStatement();
    if (check(TokenType::While)) return parseWhileStatement();

    return parseSimpleStatement();
}

std::unique_ptr<Statement> Parser::parseReturnStatement() {
    consume(TokenType::Return, "Expected 'return'");
    std::unique_ptr<Expression> value = nullptr;

    if (!check(TokenType::Newline)) {
        value = parseExpression();
    }
    consumeStatementEnd();
    return std::make_unique<ReturnStmt>(std::move(value));
}

std::unique_ptr<ImportStmt> Parser::parseImportStatement() {
    consume(TokenType::From, "Expected 'from'");

    std::string mod_name = consume(TokenType::Identifier, "Expected module name").value;
    while (match(TokenType::Dot)) {
        mod_name += "." + consume(TokenType::Identifier, "Expected part name").value;
    }

    consume(TokenType::Import, "Expected 'import'");
    consume(TokenType::Star, "Only 'import *' supported for now");
    consumeStatementEnd();

    return std::make_unique<ImportStmt>(mod_name);
}

std::unique_ptr<Statement> Parser::parseIfStatement() {
    consume(TokenType::If, "Expected 'if'");
    auto condition = parseExpression();
    consume(TokenType::Colon, "Expected ':'");
    consume(TokenType::Newline, "Expected newline");

    auto thenBranch = parseBlock();
    std::unique_ptr<Statement> elseBranch = nullptr;

    if (match(TokenType::Elif)) {
        auto elifCond = parseExpression();
        consume(TokenType::Colon, "Expected ':'");
        consume(TokenType::Newline, "Expected newline");
        auto elifBlock = parseBlock();
        elseBranch = std::make_unique<IfStmt>(std::move(elifCond), std::move(elifBlock), nullptr);
    }
    else if (match(TokenType::Else)) {
        consume(TokenType::Colon, "Expected ':'");
        consume(TokenType::Newline, "Expected newline");
        elseBranch = parseBlock();
    }

    return std::make_unique<IfStmt>(std::move(condition), std::move(thenBranch), std::move(elseBranch));
}

std::unique_ptr<Statement> Parser::parseWhileStatement() {
    consume(TokenType::While, "Expected 'while'");
    auto condition = parseExpression();
    consume(TokenType::Colon, "Expected ':'");
    consume(TokenType::Newline, "Expected newline");
    auto body = parseBlock();
    return std::make_unique<WhileStmt>(std::move(condition), std::move(body));
}

std::unique_ptr<Statement> Parser::parseSimpleStatement() {
    if (check(TokenType::Return)) {
        return parseReturnStatement();
    }

    if (match(TokenType::Pass)) {
        consumeStatementEnd();
        return std::make_unique<PassStmt>();
    }

    if (match(TokenType::Break)) {
        consumeStatementEnd();
        return std::make_unique<BreakStmt>();
    }

    if (match(TokenType::Continue)) {
        consumeStatementEnd();
        return std::make_unique<ContinueStmt>();
    }

    return parseAssignmentOrDeclaration();
}

std::unique_ptr<Statement> Parser::parseAssignmentOrDeclaration() {
    auto expr = parseExpression();

    if (match(TokenType::Colon)) {
        const auto varExpr = dynamic_cast<VariableExpr*>(expr.get());
        if (!varExpr) error("Only simple variables can be annotated with types");

        std::string name = varExpr->name;
        std::string type = parseTypeAnnotation();

        std::unique_ptr<Expression> init = nullptr;
        if (match(TokenType::Equal)) {
            init = parseExpression();
        }
        consumeStatementEnd();

        return std::make_unique<VarDecl>(name, type, std::move(init));
    }

    if (match(TokenType::Equal)) {
        auto value = parseExpression();
        consumeStatementEnd();
        return std::make_unique<AssignStmt>(std::move(expr), std::move(value));
    }

    consumeStatementEnd();
    return std::make_unique<ExprStmt>(std::move(expr));
}

std::unique_ptr<Expression> Parser::parseExpression() {
    return parseLogicalOr();
}

std::unique_ptr<Expression> Parser::parseLogicalOr() {
    auto left = parseLogicalAnd();
    return left;
}

std::unique_ptr<Expression> Parser::parseLogicalAnd() {
    return parseBitwiseOr();
}

std::unique_ptr<Expression> Parser::parseBitwiseOr() {
    auto left = parseBitwiseXor();
    while (match(TokenType::Pipe)) {
        auto right = parseBitwiseXor();
        left = std::make_unique<BinaryExpr>(std::move(left), BinaryOp::BitOr, std::move(right));
    }
    return left;
}

std::unique_ptr<Expression> Parser::parseBitwiseXor() {
    auto left = parseBitwiseAnd();
    while (match(TokenType::Caret)) {
        auto right = parseBitwiseAnd();
        left = std::make_unique<BinaryExpr>(std::move(left), BinaryOp::BitXor, std::move(right));
    }
    return left;
}

std::unique_ptr<Expression> Parser::parseBitwiseAnd() {
    auto left = parseEquality();
    while (match(TokenType::Ampersand)) {
        auto right = parseEquality();
        left = std::make_unique<BinaryExpr>(std::move(left), BinaryOp::BitAnd, std::move(right));
    }
    return left;
}

std::unique_ptr<Expression> Parser::parseEquality() {
    auto left = parseRelational();
    while (check(TokenType::EqualEqual) || check(TokenType::BangEqual)) {
        Token opToken = advance();
        BinaryOp op = (opToken.type == TokenType::EqualEqual) ? BinaryOp::Equal : BinaryOp::NotEqual;
        auto right = parseRelational();
        left = std::make_unique<BinaryExpr>(std::move(left), op, std::move(right));
    }
    return left;
}

std::unique_ptr<Expression> Parser::parseRelational() {
    auto left = parseShift();
    while (check(TokenType::Less) || check(TokenType::LessEqual) ||
           check(TokenType::Greater) || check(TokenType::GreaterEqual)) {
        Token opToken = advance();
        BinaryOp op;
        if (opToken.type == TokenType::Less) op = BinaryOp::Less;
        else if (opToken.type == TokenType::LessEqual) op = BinaryOp::LessEq;
        else if (opToken.type == TokenType::Greater) op = BinaryOp::Greater;
        else op = BinaryOp::GreaterEq;

        auto right = parseShift();
        left = std::make_unique<BinaryExpr>(std::move(left), op, std::move(right));
    }
    return left;
}

std::unique_ptr<Expression> Parser::parseShift() {
    auto left = parseAdditive();
    while (check(TokenType::LShift) || check(TokenType::RShift)) {
        Token opToken = advance();
        BinaryOp op = (opToken.type == TokenType::LShift) ? BinaryOp::LShift : BinaryOp::RShift;
        auto right = parseAdditive();
        left = std::make_unique<BinaryExpr>(std::move(left), op, std::move(right));
    }
    return left;
}

std::unique_ptr<Expression> Parser::parseAdditive() {
    auto left = parseMultiplicative();
    while (check(TokenType::Plus) || check(TokenType::Minus)) {
        const Token opToken = advance();
        BinaryOp op = (opToken.type == TokenType::Plus) ? BinaryOp::Add : BinaryOp::Sub;
        auto right = parseMultiplicative();
        left = std::make_unique<BinaryExpr>(std::move(left), op, std::move(right));
    }
    return left;
}

std::unique_ptr<Expression> Parser::parseMultiplicative() {
    auto left = parseUnary();
    while (check(TokenType::Star) || check(TokenType::Slash) || check(TokenType::Percent)) {
        const Token opToken = advance();
        BinaryOp op;
        if (opToken.type == TokenType::Star) op = BinaryOp::Mul;
        else if (opToken.type == TokenType::Slash) op = BinaryOp::Div;
        else op = BinaryOp::Mod;

        auto right = parseUnary();
        left = std::make_unique<BinaryExpr>(std::move(left), op, std::move(right));
    }
    return left;
}

std::unique_ptr<Expression> Parser::parseUnary() {
    if (check(TokenType::Minus)) {
        advance();
        auto operand = parseUnary();
        return std::make_unique<UnaryExpr>(UnaryOp::Negate, std::move(operand));
    }
    if (check(TokenType::Tilde)) {
        advance();
        auto operand = parseUnary();
        return std::make_unique<UnaryExpr>(UnaryOp::BitNot, std::move(operand));
    }
    return parsePostfix();
}

std::unique_ptr<Expression> Parser::parsePostfix() {
    auto expr = parsePrimary();

    while (true) {
        if (match(TokenType::LParen)) {
            std::string calleeName;
            if (const auto var = dynamic_cast<VariableExpr*>(expr.get())) {
                calleeName = var->name;
            } else {
                error("Expression is not callable");
            }

            std::vector<std::unique_ptr<Expression>> args;
            if (!check(TokenType::RParen)) {
                do {
                    args.push_back(parseExpression());
                } while (match(TokenType::Comma));
            }
            consume(TokenType::RParen, "Expected ')'");
            expr = std::make_unique<CallExpr>(calleeName, std::move(args));
        }
        else if (match(TokenType::LBracket)) {
            auto index = parseExpression();
            consume(TokenType::RBracket, "Expected ']'");
            expr = std::make_unique<IndexExpr>(std::move(expr), std::move(index));
        }
        else {
            break;
        }
    }
    return expr;
}

std::unique_ptr<Expression> Parser::parsePrimary() {
    if (match(TokenType::True)) return std::make_unique<BooleanLiteral>(true);
    if (match(TokenType::False)) return std::make_unique<BooleanLiteral>(false);

    if (match(TokenType::Identifier)) {
        return std::make_unique<VariableExpr>(previous().value);
    }

    if (match(TokenType::Number)) {
        const Token t = previous();
        std::string text = t.value;
        std::erase(text, '_');

        int base = 10;
        size_t offset = 0;

        if (text.size() >= 2 && text[0] == '0') {
            if (const char prefix = std::tolower(text[1]); prefix == 'x') {
                base = 16;
                offset = 2;
            }
            else if (prefix == 'b') {
                base = 2;
                offset = 2;
            }
            else if (prefix == 'o') {
                base = 8;
                offset = 2;
            }
        }

        try {
            int val = std::stoi(text.substr(offset), nullptr, base);
            return std::make_unique<IntegerLiteral>(val);
        }
        catch (const std::out_of_range&) {
            error(std::format("Integer literal is too large: '{}'", t.value));
        }
        catch (const std::invalid_argument&) {
            error(std::format("Invalid integer literal: '{}'", t.value));
        }
        return nullptr;
    }

    if (match(TokenType::LParen)) {
        auto expr = parseExpression();
        consume(TokenType::RParen, "Expected ')'");
        return expr;
    }

    error("Expected expression");
    return nullptr;
}