#ifndef UTILS_H
#define UTILS_H

#include <iostream>
#include <fstream>
#include <sstream>
#include <string>
#include <filesystem> // C++17 Standard

namespace fs = std::filesystem;

inline std::string read_source(const std::string& path_str) {
    fs::path path(path_str);

    if (!fs::exists(path)) {
        throw std::runtime_error("File not found: '" + path_str +
                                 "'\nLooking in: " + fs::absolute(path).string());
    }

    std::ifstream f(path);
    if (!f.is_open()) {
        throw std::runtime_error("Permission denied or locked file: '" + path_str + "'");
    }

    std::stringstream buffer;
    buffer << f.rdbuf();
    return buffer.str();
}

#endif // UTILS_H
