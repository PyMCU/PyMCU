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

    static std::string get_stdlib_path() {
        const std::string cmd = "python3 -c \"import os, pymcu; print(os.path.dirname(pymcu.__file__), end='')\"";

        std::array<char, 128> buffer{};
        std::string result;
        const std::unique_ptr<FILE, decltype(&pclose)> pipe(popen(cmd.c_str(), "r"), pclose);

        if (!pipe) {
            std::cerr << "[Warning] Could not invoke python3 to find stdlib.\n";
            return "";
        }

        while (fgets(buffer.data(), buffer.size(), pipe.get()) != nullptr) {
            result += buffer.data();
        }

        return result;
    }

    static void run_compiler(const std::string& input, const std::string& output,
                             const std::string& arch, unsigned long freq,
                             const std::map<std::string, std::string>& configs) {

        std::string cmd = get_compiler_path().string() + " ";
        cmd += input + " ";
        cmd += "-o " + output + " ";
        cmd += "--arch " + arch + " ";
        cmd += "--freq " + std::to_string(freq) + " ";

        if (std::string stdlib = get_stdlib_path(); !stdlib.empty()) {
            fs::path p(stdlib);
            cmd += "-I " + p.parent_path().string() + " ";
        }

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