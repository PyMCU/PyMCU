#include <iostream>
#include <fstream>
#include <sstream>
#include <vector>
#include <argparse/argparse.hpp>

#include "frontend/Lexer.h"
#include "frontend/Parser.h"
#include "Diagnostic.h"
#include "Errors.h"
#include "Utils.h"
#include "backend/CodeGenFactory.h"
#include "ir/IRGenerator.h"
#include "ir/Tacky.h"

int main(const int argc, char* argv[]) {
    argparse::ArgumentParser program("pymcuc");
    program.add_argument("file").help("Input source file");
    program.add_argument("-o", "--output")
           .default_value(std::string(""))
           .help("Output ASM file (defaults to input filename with .asm extension)");
    program.add_argument("--arch").default_value(std::string("pic14"));

    try {
        program.parse_args(argc, argv);
    } catch (const std::exception& err) {
        std::cerr << err.what() << std::endl;
        return 1;
    }

    const auto filepath = program.get<std::string>("file");
    const auto arch = program.get<std::string>("--arch");
    auto output_path = program.get<std::string>("--output");

    if (output_path.empty()) {
        std::filesystem::path p(filepath);
        p.replace_extension(".asm");
        output_path = p.string();
    }

    std::string source;
    try {
        source = read_source(filepath);
    } catch (const std::exception& e) {
        std::cerr << "Fatal Error: " << e.what() << "\n";
        return 1;
    }

    try {
        Lexer lexer(source);
        const auto tokens = lexer.tokenize();

        Parser parser(tokens);
        const auto ast = parser.parseProgram();

        IRGenerator irGen;
        auto ir = irGen.generate(*ast);

        auto backend = CodeGenFactory::create(arch);

        std::ofstream asm_file(output_path);
        if (!asm_file.is_open()) {
            throw std::runtime_error("Cannot open output file: " + output_path);
        }

        std::cout << "[pymcuc] Compiling " << filepath << " -> " << output_path << " (" << arch << ")\n";
        backend->compile(ir, asm_file);

        std::cout << "[pymcuc] Success! Output written to " << output_path << "\n";
    }
    catch (const CompilerError& e) {
        Diagnostic::report(e, source, filepath);
        return 1;
    }
    catch (const std::exception& e) {
        std::cerr << "Internal Compiler Error: " << e.what() << "\n";
        return 1;
    }

    return 0;
}
