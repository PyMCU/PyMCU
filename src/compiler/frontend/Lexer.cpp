#include "Lexer.h"
#include <map>
#include <stdexcept>
#include <format>
#include <cctype>
#include "Errors.h"

static const std::map<std::string, TokenType> keywords = {
    {"def", TokenType::Def},
    {"return", TokenType::Return},
    {"if", TokenType::If},
    {"elif", TokenType::Elif},
    {"else", TokenType::Else},
    {"while", TokenType::While},
    {"for", TokenType::For},
    {"in", TokenType::In},
    {"break", TokenType::Break},
    {"continue", TokenType::Continue},
    {"pass", TokenType::Pass},
    {"import", TokenType::Import},
    {"from", TokenType::From},
    {"as", TokenType::As},
    {"True", TokenType::True},
    {"False", TokenType::False},
    {"None", TokenType::None},
};

Lexer::Lexer(const std::string_view source) : src(source) {
    indent_stack.push_back(0);
}

char Lexer::peek() const {
    if (pos >= src.size()) return '\0';
    return src[pos];
}

char Lexer::peek_next() const {
    if (pos + 1 >= src.size()) return '\0';
    return src[pos + 1];
}

char Lexer::advance() {
    if (pos >= src.size()) return '\0';
    const char c = src[pos++];
    column++;
    return c;
}

bool Lexer::match(const char expected) {
    if (peek() == expected) {
        advance();
        return true;
    }
    return false;
}

void Lexer::skip_whitespace() {
    while (true) {
        const char c = peek();
        if (c == ' ' || c == '\t' || c == '\r') {
            advance();
        } else {
            break;
        }
    }
}

void Lexer::skip_comment() {
    if (peek() == '#') {
        while (peek() != '\n' && peek() != '\0') {
            advance();
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

    if (temp_pos >= src.size() || src[temp_pos] == '\n' || src[temp_pos] == '#') {
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
            error("Unindent does not match any outer indentation level");
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
            if (const char next = peek_next(); tolower(next) == 'x') { // Hex
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
    while (isalnum(peek()) || peek() == '_') {
        text += advance();
    }

    auto type = TokenType::Identifier;
    if (keywords.contains(text)) {
        type = keywords.at(text);
    }
    return {type, text, line, column};
}

Token Lexer::scan_token() {
    skip_whitespace();
    skip_comment();

    if (pos >= src.size()) return {TokenType::EndOfFile, "", line, column};

    char c = peek();

    if (c == '\n') {
        advance();
        line++;
        column = 1;
        at_line_start = true;
        return {TokenType::Newline, "\\n", line - 1, column};
    }

    if (isalpha(c) || c == '_') return identifier();
    if (isdigit(c)) return number();

    advance();

    switch (c) {
        case '(': return {TokenType::LParen, "(", line, column};
        case ')': return {TokenType::RParen, ")", line, column};
        case '[': return {TokenType::LBracket, "[", line, column};
        case ']': return {TokenType::RBracket, "]", line, column};
        case ':': return {TokenType::Colon, ":", line, column};
        case ';': return {TokenType::Semicolon, ";", line, column};
        case ',': return {TokenType::Comma, ",", line, column};
        case '.': return {TokenType::Dot, ".", line, column};

        case '-':
            if (match('>')) return {TokenType::Arrow, "->", line, column};
            return {TokenType::Minus, "-", line, column};
        case '+': return {TokenType::Plus, "+", line, column};
        case '*': return {TokenType::Star, "*", line, column};
        case '/':
            // Podrías añadir // para división entera aquí
            return {TokenType::Slash, "/", line, column};
        case '%': return {TokenType::Percent, "%", line, column};

        case '=':
            if (match('=')) return {TokenType::EqualEqual, "==", line, column};
            return {TokenType::Equal, "=", line, column};
        case '!':
            if (match('=')) return {TokenType::BangEqual, "!=", line, column};
            error("Expected '=' after '!'");
            break;
        case '<':
            if (match('=')) return {TokenType::LessEqual, "<=", line, column};
            if (match('<')) return {TokenType::LShift, "<<", line, column};
            return {TokenType::Less, "<", line, column};
        case '>':
            if (match('=')) return {TokenType::GreaterEqual, ">=", line, column};
            if (match('>')) return {TokenType::RShift, ">>", line, column};
            return {TokenType::Greater, ">", line, column};

        case '&': return {TokenType::Ampersand, "&", line, column};
        case '|': return {TokenType::Pipe, "|", line, column};
        case '^': return {TokenType::Caret, "^", line, column};
        case '~': return {TokenType::Tilde, "~", line, column};

        default:
            error(std::format("invalid character '{}'", c));
    }
    return {TokenType::Unknown, "", line, column};
}

std::vector<Token> Lexer::tokenize() {
    std::vector<Token> tokens;

    while (true) {
        if (!token_queue.empty()) {
            tokens.push_back(token_queue.front());
            token_queue.erase(token_queue.begin());
            continue;
        }
        if (pos >= src.size()) break;

        if (at_line_start) {
            handle_indentation();
            if (!token_queue.empty()) continue;
        }

        Token token = scan_token();

        if (token.type == TokenType::EndOfFile) break;

        if (token.type == TokenType::Newline) {
            if (!tokens.empty() && tokens.back().type == TokenType::Newline) {
                continue;
            }
        }

        tokens.push_back(token);
    }

    if (!tokens.empty() && tokens.back().type != TokenType::Newline) {
         tokens.push_back({TokenType::Newline, "\\n", line, column});
    }

    while (indent_stack.size() > 1) {
        indent_stack.pop_back();
        tokens.push_back({TokenType::Dedent, "", line, column});
    }

    tokens.push_back({TokenType::EndOfFile, "", line, column});
    return tokens;
}