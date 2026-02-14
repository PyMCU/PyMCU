#ifndef ERRORS_H
#define ERRORS_H

#pragma once
#include <stdexcept>
#include <string>
#include <utility>

// Base class for all compilation errors
class CompilerError : public std::runtime_error {
public:
    int line;
    int column;
    std::string type_name; // "SyntaxError", "IndentationError"

    CompilerError(std::string name, const std::string &msg, const int l, const int c)
        : std::runtime_error(msg), line(l), column(c), type_name(std::move(name)) {
    }
};

// Python: SyntaxError
class SyntaxError : public CompilerError {
public:
    SyntaxError(const std::string &msg, const int l, const int c)
        : CompilerError("SyntaxError", msg, l, c) {
    }
};

// Python: IndentationError
class IndentationError : public CompilerError {
public:
    IndentationError(const std::string &msg, const int l, const int c)
        : CompilerError("IndentationError", msg, l, c) {
    }
};

class LexicalError : public CompilerError {
public:
    LexicalError(const std::string &msg, const int l, const int c)
        : CompilerError("LexicalError", msg, l, c) {
    }
};

#endif //ERRORS_H
