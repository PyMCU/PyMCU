#include "Lexer.h"
#include <map>
#include <stdexcept>
#include <format>

#include "Errors.h"

static const std::map<std::string, TokenType> keywords = {
    {"def", TokenType::Def},
    {"return", TokenType::Return},
    // {"volatile", TokenType::Volatile}, // Uncomment if using volatile
    // {"u8", TokenType::TypeU8},         // Uncomment if using types
};

Lexer::Lexer(const std::string_view source) : src(source) {
    indent_stack.push_back(0);
}

char Lexer::peek() const {
    if (pos >= src.size()) return '\0';
    return src[pos];
}

char Lexer::advance() {
    if (pos >= src.size()) return '\0';
    const char c = src[pos++];

    if (c == '\n') {
        line++;
        column = 1;
        at_line_start = true;
    } else {
        column++;
    }
    return c;
}

bool Lexer::match(const char expected) {
    if (peek() == expected) {
        advance();
        return true;
    }
    return false;
}

void Lexer::skip_whitespace_and_comments() {
    while (true) {
        if (const char c = peek(); c == ' ' || c == '\t' || c == '\r') {
            advance();
        }
        else if (c == '#') {
            while (peek() != '\n' && peek() != '\0') {
                advance();
            }
        }
        else {
            break;
        }
    }
}

void Lexer::error(const std::string_view message) const {
    throw LexicalError(std::string(message), line, column);
}

void Lexer::handle_indentation() {
    int spaces = 0;
    size_t temp_pos = pos;

    while (temp_pos < src.size() && src[temp_pos] == ' ') {
        spaces++;
        temp_pos++;
    }

    if (temp_pos < src.size() && (src[temp_pos] == '\n' || src[temp_pos] == '#')) {
        return;
    }

    for (int i = 0; i < spaces; i++) advance();

    if (const int current_indent = indent_stack.back(); spaces > current_indent) {
        indent_stack.push_back(spaces);
        token_queue.push_back({TokenType::Indent, "", line, column});
    }
    else if (spaces < current_indent) {
        while (spaces < indent_stack.back()) {
            indent_stack.pop_back();
            token_queue.push_back({TokenType::Dedent, "", line, column});
        }
        if (indent_stack.back() != spaces) {
            throw std::runtime_error(std::format("Error de Indentación: Línea {}", line));
        }
    }

    at_line_start = false;
}

static bool is_digit_for_base(const char c, const int base) {
    if (c == '_') return true;
    switch (base) {
        case 2: return c == '0' || c == '1';
        case 8: return c >= '0' && c <= '7';
        case 10: return isdigit(c);
        case 16: return isxdigit(c);
        default: return false;
    }
}

Token Lexer::number() {
    std::string text;
    int base = 10;

    if (peek() == '0') {
        if (pos + 1 < src.size()) {
            if (const char next = src[pos + 1]; tolower(next) == 'x') { // Hex
                base = 16;
                text += advance(); // 0
                text += advance(); // x
            }
            else if (tolower(next) == 'b') { // Binary
                base = 2;
                text += advance(); // 0
                text += advance(); // b
            }
            else if (tolower(next) == 'o') { // Octal
                base = 8;
                text += advance(); // 0
                text += advance(); // o
            }
            else if (isdigit(next)) {
                error("leading zeros in decimal integer literals are not permitted; use an 0o prefix for octal integers");
            }
        }
    }

    while (is_digit_for_base(peek(), base)) {
        text += advance();
    }

    if (text.back() == '_') {
        error("decimal literal cannot end with an underscore");
    }
    if (base != 10 && text.size() > 2 && text[2] == '_') {
        error("underscores not allowed immediately after base prefix");
    }

    if (isalnum(peek()) || peek() == '_') {
        char bad = peek();
        if (base == 2) error(std::format("invalid digit '{}' in binary literal", bad));
        if (base == 8) error(std::format("invalid digit '{}' in octal literal", bad));
        error(std::format("invalid suffix '{}' on integer literal", bad));
    }

    return {TokenType::Number, text, line, column};
}

Token Lexer::identifier() {
    std::string text;
    while (isalnum(peek()) || peek() == '_') text += advance();

    auto type = TokenType::Identifier;
    if (keywords.contains(text)) {
        type = keywords.at(text);
    }
    return {type, text, line, column};
}

Token Lexer::scan_token() {
    skip_whitespace_and_comments();

    if (pos >= src.size()) return {TokenType::EndOfFile, "", line, column};

    char c = peek();

    if (c == '\n') {
        advance();
        return {TokenType::Newline, "\\n", line-1, column};
    }

    if (isalpha(c) || c == '_') return identifier();
    if (isdigit(c)) return number();

    advance();
    switch (c) {
        case '(': return {TokenType::LParen, "(", line, column};
        case ')': return {TokenType::RParen, ")", line, column};
        case ':': return {TokenType::Colon, ":", line, column};
        default:
            error(std::format("invalid character '{}'", c));
    }
}

std::vector<Token> Lexer::tokenize() {
    std::vector<Token> tokens;

    while (pos < src.size() || !token_queue.empty()) {
        if (!token_queue.empty()) {
            tokens.push_back(token_queue.front());
            token_queue.erase(token_queue.begin());
            continue;
        }

        if (at_line_start) {
            handle_indentation();
            if (!token_queue.empty()) continue;
        }

        Token token = scan_token();

        if (token.type == TokenType::EndOfFile) break;

        tokens.push_back(token);
    }

    while (indent_stack.size() > 1) {
        indent_stack.pop_back();
        tokens.push_back({TokenType::Dedent, "", line, column});
    }

    tokens.push_back({TokenType::EndOfFile, "", line, column});
    return tokens;
}
