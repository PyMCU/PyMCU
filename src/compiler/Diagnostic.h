#ifndef DIAGNOSTIC_H
#define DIAGNOSTIC_H

#include "Errors.h"
#include <string_view>
#include <iostream>
#include <vector>
#include <format>
#include <sstream>

class Diagnostic {
public:
    static void report(const CompilerError &err, const std::string_view source, std::string_view filename) {
        std::cerr << std::format("  File \"{}\", line {}\n", filename, err.line);

        if (const std::string line_content = get_line(source, err.line); !line_content.empty()) {
            std::cerr << "    " << line_content << "\n";
            const std::string pointer(err.column + 4 - 1, ' ');
            std::cerr << pointer << "^\n";
        }
        std::cerr << std::format("{}: {}\n", err.type_name, err.what());
    }

private:
    static std::string get_line(const std::string_view src, const int target_line) {
        int current = 1;
        size_t start = 0;
        for (size_t i = 0; i < src.size(); ++i) {
            if (src[i] == '\n') {
                if (current == target_line) return std::string(src.substr(start, i - start));
                current++;
                start = i + 1;
            }
        }
        if (current == target_line) return std::string(src.substr(start));
        return "";
    }
};

#endif