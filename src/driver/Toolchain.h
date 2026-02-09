#ifndef TOOLCHAIN_H
#define TOOLCHAIN_H

#include <string>
#include <vector>
#include <map>
#include <iostream>
#include <filesystem>
#include <unistd.h>
#ifdef __APPLE__
#include <mach-o/dyld.h>
#endif

namespace fs = std::filesystem;

class Toolchain {
public:
    static fs::path get_compiler_path() {
        fs::path exe_dir;
        #ifdef __APPLE__
            char path[1024];
            uint32_t size = sizeof(path);
            if (_NSGetExecutablePath(path, &size) == 0) {
                exe_dir = fs::path(path).parent_path();
            }
        #else
            char result[1024];
            ssize_t count = readlink("/proc/self/exe", result, 1024);
            if (count != -1) {
                exe_dir = fs::path(std::string(result, count)).parent_path();
            }
        #endif

        if (fs::path local_compiler = exe_dir / "pymcuc"; fs::exists(local_compiler)) return local_compiler;
        return "pymcuc";
    }

    static void run_compiler(const std::string& input, const std::string& output,
                             const std::string& arch, unsigned long freq,
                             const std::map<std::string, std::string>& configs) {

        std::string cmd = get_compiler_path().string() + " ";
        cmd += input + " ";
        cmd += "-o " + output + " ";
        cmd += "--arch " + arch + " ";
        cmd += "--freq " + std::to_string(freq) + " ";

        for (const auto& [key, val] : configs) {
            cmd += "-C " + key + "=" + val + " ";
        }

        std::cout << "[pymcu] Executing build...\n";
        // std::cout << "[debug] " << cmd << "\n";

        if (const int result = std::system(cmd.c_str()); result != 0) {
            throw std::runtime_error("Compilation failed.");
        }
    }
};

#endif // TOOLCHAIN_H