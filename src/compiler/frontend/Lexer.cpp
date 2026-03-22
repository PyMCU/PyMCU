/*
 * -----------------------------------------------------------------------------
 * Whipsnake Compiler (whipc)
 * Copyright (C) 2026 Ivan Montiel Cardona and the Whipsnake Project Authors
 *
 * SPDX-License-Identifier: MIT
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction, including without limitation the rights
 * to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
 * copies of the Software, and to permit persons to whom the Software is
 * furnished to do so, subject to the following conditions:
 *
 * The above copyright notice and this permission notice shall be included in
 * all copies or substantial portions of the Software.
 *
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 * IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 * FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 * AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 * LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
 * OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
 * THE SOFTWARE.
 * -----------------------------------------------------------------------------
 * SAFETY WARNING / HIGH RISK ACTIVITIES:
 * THE SOFTWARE IS NOT DESIGNED, MANUFACTURED, OR INTENDED FOR USE IN HAZARDOUS
 * ENVIRONMENTS REQUIRING FAIL-SAFE PERFORMANCE, SUCH AS IN THE OPERATION OF
 * NUCLEAR FACILITIES, AIRCRAFT NAVIGATION OR COMMUNICATION SYSTEMS, AIR
 * TRAFFIC CONTROL, DIRECT LIFE SUPPORT MACHINES, OR WEAPONS SYSTEMS.
 * -----------------------------------------------------------------------------
 */

#include "Lexer.h"

#include <cctype>
#include <format>
#include <map>
#include <stdexcept>

#include "Errors.h"

static const std::map<std::string, TokenType> keywords = {
    {"def", TokenType::Def},       {"return", TokenType::Return},
    {"if", TokenType::If},         {"elif", TokenType::Elif},
    {"else", TokenType::Else},     {"while", TokenType::While},
    {"for", TokenType::For},       {"in", TokenType::In},
    {"break", TokenType::Break},   {"continue", TokenType::Continue},
    {"pass", TokenType::Pass},     {"match", TokenType::Match},
    {"case", TokenType::Case},     {"import", TokenType::Import},
    {"from", TokenType::From},     {"as", TokenType::As},
    {"True", TokenType::True},     {"False", TokenType::False},
    {"None", TokenType::None},     {"or", TokenType::Or},
    {"and", TokenType::And},       {"not", TokenType::Not},
    {"global", TokenType::Global}, {"class", TokenType::Class},
    {"yield", TokenType::Yield},
    {"raise", TokenType::Raise},
    {"with", TokenType::With},
    {"assert", TokenType::Assert},
    {"is", TokenType::Is},
    {"lambda", TokenType::Lambda},
    {"nonlocal", TokenType::Nonlocal},
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
  } else if (spaces < current_indent) {
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
    case 2:
      return c == '0' || c == '1';
    case 8:
      return c >= '0' && c <= '7';
    case 10:
      return isdigit(c);
    case 16:
      return isxdigit(c);
    default:
      return false;
  }
}

Token Lexer::number() {
  std::string text;
  int base = 10;

  if (peek() == '0') {
    if (pos + 1 < src.size()) {
      if (const char next = peek_next(); tolower(next) == 'x') {
        // Hex
        base = 16;
        text += advance();  // 0
        text += advance();  // x
      } else if (tolower(next) == 'b') {
        // Binary
        base = 2;
        text += advance();  // 0
        text += advance();  // b
      } else if (tolower(next) == 'o') {
        // Octal
        base = 8;
        text += advance();  // 0
        text += advance();  // o
      } else if (isdigit(next)) {
        error(
            "leading zeros in decimal integer literals are not permitted; "
            "use an 0o prefix for octal integers");
      }
    }
  }

  while (is_digit_for_base(peek(), base)) {
    text += advance();
  }

  // Floating point support
  if (base == 10 && peek() == '.') {
    if (isdigit(peek_next())) {
      text += advance();  // Consume '.'
      while (isdigit(peek())) {
        text += advance();
      }
    }
  }

  if (text.back() == '_') {
    error("decimal literal cannot end with an underscore");
  }
  if (base != 10 && text.size() > 2 && text[2] == '_') {
    error("underscores not allowed immediately after base prefix");
  }

  if (isalnum(peek()) || peek() == '_') {
    char bad = peek();
    if (base == 2)
      error(std::format("invalid digit '{}' in binary literal", bad));
    if (base == 8)
      error(std::format("invalid digit '{}' in octal literal", bad));
    error(std::format("invalid suffix '{}' on integer literal", bad));
  }

  return {TokenType::Number, text, line, column};
}

Token Lexer::identifier() {
  std::string text;
  while (isalnum(peek()) || peek() == '_') {
    text += advance();
  }

  // b-string prefix: exactly the letter "b" followed immediately by a quote
  // Produces a BytesLiteral token whose value is comma-separated decimal byte values.
  if (text == "b" && (peek() == '"' || peek() == '\'')) {
    char quote = advance();  // consume the opening quote
    // Parse byte string into comma-separated decimal values (e.g. "65,66,67")
    std::string encoded;
    bool first_byte = true;
    while (peek() != quote && peek() != '\0') {
      if (peek() == '\n') line++;
      int byte_val = 0;
      if (peek() == '\\') {
        advance();  // consume backslash
        char esc = advance();
        switch (esc) {
          case 'n':  byte_val = '\n'; break;
          case 't':  byte_val = '\t'; break;
          case 'r':  byte_val = '\r'; break;
          case '0':  byte_val = '\0'; break;
          case '\\': byte_val = '\\'; break;
          case '\'': byte_val = '\''; break;
          case '"':  byte_val = '"';  break;
          case 'x': {
            // \xNN hex escape
            char h1 = advance();
            char h2 = advance();
            int hi = isdigit(h1) ? (h1 - '0') : (tolower(h1) - 'a' + 10);
            int lo = isdigit(h2) ? (h2 - '0') : (tolower(h2) - 'a' + 10);
            byte_val = (hi << 4) | lo;
            break;
          }
          default: byte_val = (unsigned char)esc; break;
        }
      } else {
        byte_val = (unsigned char)advance();
      }
      if (!first_byte) encoded += ",";
      encoded += std::to_string(byte_val);
      first_byte = false;
    }
    if (peek() == '\0') {
      error("Unterminated bytes literal");
    }
    advance();  // consume closing quote
    return {TokenType::BytesLiteral, encoded, line, column};
  }

  // r-string prefix: exactly the letter "r" followed immediately by a quote.
  // Raw strings suppress all escape-sequence processing -- every character
  // is appended verbatim.  r"\n" is two chars (backslash + n), not a newline.
  if (text == "r" && (peek() == '"' || peek() == '\'')) {
    char quote = advance();  // consume opening quote
    std::string raw;
    while (peek() != quote && peek() != '\0') {
      if (peek() == '\n') line++;
      raw += advance();  // verbatim -- no escape processing
    }
    if (peek() == '\0') {
      error("Unterminated raw string literal");
    }
    advance();  // consume closing quote
    return {TokenType::String, raw, line, column};
  }

  // f-string prefix: exactly the letter "f" followed immediately by a quote
  if (text == "f" && (peek() == '"' || peek() == '\'')) {
    char quote = advance();  // consume the opening quote
    // Collect the raw interior without processing escape sequences or expressions.
    // The Parser will handle splitting on '{' / '}' and sub-parsing each expr part.
    std::string raw;
    while (peek() != quote && peek() != '\0') {
      if (peek() == '\n') line++;
      if (peek() == '\\') {
        raw += advance();  // keep backslash
        if (peek() != '\0') raw += advance();
      } else {
        raw += advance();
      }
    }
    if (peek() == '\0') {
      error("Unterminated f-string literal");
    }
    advance();  // consume closing quote
    return {TokenType::FString, raw, line, column};
  }

  auto type = TokenType::Identifier;
  if (keywords.contains(text)) {
    type = keywords.at(text);
  }
  return {type, text, line, column};
}

Token Lexer::string(char quote) {
  std::string text;
  while (peek() != quote && peek() != '\0') {
    if (peek() == '\n') line++;
    if (peek() == '\\') {
      advance();  // consume backslash
      char esc = advance();
      switch (esc) {
        case 'n':  text += '\n'; break;
        case 't':  text += '\t'; break;
        case 'r':  text += '\r'; break;
        case '0':  text += '\0'; break;
        case '\\': text += '\\'; break;
        case '\'': text += '\''; break;
        case '"':  text += '"';  break;
        default:   text += '\\'; text += esc; break;
      }
    } else {
      text += advance();
    }
  }

  if (peek() == '\0') {
    error("Unterminated string literal");
  }

  advance();  // Consume closing quote
  return {TokenType::String, text, line, column};
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
    case '(':
      return {TokenType::LParen, "(", line, column};
    case ')':
      return {TokenType::RParen, ")", line, column};
    case '[':
      return {TokenType::LBracket, "[", line, column};
    case ']':
      return {TokenType::RBracket, "]", line, column};
    case ':':
      if (match('=')) return {TokenType::Walrus, ":=", line, column};
      return {TokenType::Colon, ":", line, column};
    case ';':
      return {TokenType::Semicolon, ";", line, column};
    case ',':
      return {TokenType::Comma, ",", line, column};
    case '.':
      return {TokenType::Dot, ".", line, column};
    case '@':
      return {TokenType::At, "@", line, column};

    case '-':
      if (match('>')) return {TokenType::Arrow, "->", line, column};
      if (match('=')) return {TokenType::MinusEqual, "-=", line, column};
      return {TokenType::Minus, "-", line, column};
    case '"':
      // Triple-quoted docstring """...""" — consume and return empty string
      if (peek() == '"' && peek_next() == '"') {
        advance(); advance();  // consume second and third "
        while (!(peek() == '"' && peek_next() == '"')) {
          if (peek() == '\0') { error("Unterminated docstring"); break; }
          if (peek() == '\n') line++;
          advance();
        }
        if (peek() == '"') { advance(); advance(); advance(); }  // closing """
        return {TokenType::String, "", line, column};
      }
      return string('"');
    case '\'':
      // Triple-quoted docstring '''...''' — same treatment
      if (peek() == '\'' && peek_next() == '\'') {
        advance(); advance();
        while (!(peek() == '\'' && peek_next() == '\'')) {
          if (peek() == '\0') { error("Unterminated docstring"); break; }
          if (peek() == '\n') line++;
          advance();
        }
        if (peek() == '\'') { advance(); advance(); advance(); }
        return {TokenType::String, "", line, column};
      }
      return string('\'');
    case '+':
      if (match('=')) return {TokenType::PlusEqual, "+=", line, column};
      return {TokenType::Plus, "+", line, column};
    case '*':
      if (match('*')) return {TokenType::DoubleStar, "**", line, column};
      if (match('=')) return {TokenType::StarEqual, "*=", line, column};
      return {TokenType::Star, "*", line, column};
    case '/':
      if (match('=')) return {TokenType::SlashEqual, "/=", line, column};
      if (match('/')) {
        if (match('=')) return {TokenType::FloorDivEqual, "//=", line, column};
        return {TokenType::FloorDiv, "//", line, column};
      }
      return {TokenType::Slash, "/", line, column};
    case '%':
      if (match('=')) return {TokenType::PercentEqual, "%=", line, column};
      return {TokenType::Percent, "%", line, column};

    case '=':
      if (match('=')) return {TokenType::EqualEqual, "==", line, column};
      return {TokenType::Equal, "=", line, column};
    case '!':
      if (match('=')) return {TokenType::BangEqual, "!=", line, column};
      error("Invalid syntax. Did you mean 'not' or '!='?");
      break;
    case '<':
      if (match('<')) {
        if (match('=')) return {TokenType::LShiftEqual, "<<=", line, column};
        return {TokenType::LShift, "<<", line, column};
      }
      if (match('=')) return {TokenType::LessEqual, "<=", line, column};
      return {TokenType::Less, "<", line, column};
    case '>':
      if (match('>')) {
        if (match('=')) return {TokenType::RShiftEqual, ">>=", line, column};
        return {TokenType::RShift, ">>", line, column};
      }
      if (match('=')) return {TokenType::GreaterEqual, ">=", line, column};
      return {TokenType::Greater, ">", line, column};

    case '&':
      if (match('=')) return {TokenType::AmpEqual, "&=", line, column};
      return {TokenType::Ampersand, "&", line, column};
    case '|':
      if (match('=')) return {TokenType::PipeEqual, "|=", line, column};
      return {TokenType::Pipe, "|", line, column};
    case '^':
      if (match('=')) return {TokenType::CaretEqual, "^=", line, column};
      return {TokenType::Caret, "^", line, column};
    case '~':
      return {TokenType::Tilde, "~", line, column};

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