#ifndef UTILS_H
#define UTILS_H

#include <string>
#include <fstream>
#include <sstream>
#include <stdexcept>

inline std::string read_source(const std::string& path) {
    std::ifstream f(path);
    if (!f.is_open()) throw std::runtime_error("can't open file '" + path + "'");
    std::stringstream buffer;
    buffer << f.rdbuf();
    return buffer.str();
}

#endif // UTILS_H
