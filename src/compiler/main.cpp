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

int main(const int argc, char* argv[]) {
    argparse::ArgumentParser program("pymcuc");
    program.add_argument("file").help("Input source file");
    program.add_argument("-o", "--output").default_value(std::string("out.asm"));
    program.add_argument("--arch").default_value(std::string("generic"));

    try {
        program.parse_args(argc, argv);
    } catch (const std::exception& err) {
        std::cerr << err.what() << std::endl;
        return 1;
    }

    const auto filepath = program.get<std::string>("file");

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
        auto ast = parser.parseProgram();

        // C. CodeGen (Future)
        // CodeGen generator(ast);
        // generator.emit(program.get<std::string>("-o"));

        // Temporary Debug
        std::cout << "; Compilation Successful. AST generated in memory.\n";

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