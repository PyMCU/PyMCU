#include <iostream>
#include <cstdlib>
#include <format>
#include <argparse/argparse.hpp>

int main(const int argc, char* argv[]) {
    argparse::ArgumentParser program("pymcu");
    program.add_argument("file").help("Source file .py");
    program.add_argument("-t", "--target").default_value(std::string("pic16f84a"));

    try {
        program.parse_args(argc, argv);
    } catch (const std::exception& err) {
        std::cerr << err.what() << std::endl;
        return 1;
    }

    auto file = program.get<std::string>("file");
    auto target = program.get<std::string>("--target");

    // 1. Build command for the compiler (pymcuc)
    // Assume that pymcuc is in the same bin directory or in the PATH
    // In a real case, you would use std::filesystem::canonical to find the binary relative path.
    const std::string compiler_cmd = std::format("./build/bin/pymcuc {} -o output.asm --arch {}", file, target);

    std::cout << "[pymcu] Compiling " << file << " for " << target << "...\n";

    // 2. Execute Compiler
    // system() redirects automatically stdout and stderr from the child to the parent.
    // If pymcuc prints the error nicely, the user will see it here.

    // WEXITSTATUS is for Linux/Unix to get the real exit code 0-255
    if (const int exit_code = std::system(compiler_cmd.c_str()); exit_code != 0) {
        // Do not print "Error", because pymcuc already printed the details.
        std::cerr << "[pymcu] Build failed.\n";
        return 1;
    }

    // 3. If successful, call GPASM
    // ...
    std::cout << "[pymcu] Build successful.\n";
    return 0;
}