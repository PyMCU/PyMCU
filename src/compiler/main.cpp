#include <iostream>
#include <fstream>
#include <sstream>
#include <vector>
#include <map>
#include <argparse/argparse.hpp>

#include "frontend/Lexer.h"
#include "frontend/Parser.h"
#include "Diagnostic.h"
#include "Errors.h"
#include "Utils.h"
#include "backend/CodeGenFactory.h"
#include "ir/IRGenerator.h"
#include "ir/Tacky.h"
#include "DeviceConfig.h"

std::string resolve_module(const std::string& module_name, const std::vector<std::string>& include_paths) {
    std::string path_rel = module_name;
    std::ranges::replace(path_rel, '.', '/');
    path_rel += ".py";

    for (const auto& base_path : include_paths) {
        if (std::filesystem::path full_path = std::filesystem::path(base_path) / path_rel; std::filesystem::exists(full_path)) {
            return full_path.string();
        }
    }
    throw std::runtime_error("Module not found: " + module_name);
}

int main(int argc, char* argv[]) {
    argparse::ArgumentParser program("pymcuc");

    program.add_argument("file").help("Input source file");
    program.add_argument("-o", "--output")
           .default_value(std::string(""))
           .help("Output ASM file");
    program.add_argument("--arch").default_value(std::string("pic14"));

    program.add_argument("--freq")
           .help("Clock frequency in Hz")
           .default_value(4000000UL)
           .scan<'u', unsigned long>();

    program.add_argument("-C", "--config")
           .help("Configuration bits (KEY=VALUE)")
           .append();

    program.add_argument("-I", "--include")
           .help("Add directory to search path for imports")
           .append();

    try {
        program.parse_args(argc, argv);
    } catch (const std::exception& err) {
        std::cerr << err.what() << std::endl;
        return 1;
    }

    const auto filepath = program.get<std::string>("file");
    const auto arch = program.get<std::string>("--arch");
    auto output_path = program.get<std::string>("--output");

    DeviceConfig device_config;
    device_config.frequency = program.get<unsigned long>("--freq");
    device_config.chip = arch;

    if (program.is_used("-C")) {
        for (auto config_list = program.get<std::vector<std::string>>("-C"); const auto& item : config_list) {
            if (size_t eq_pos = item.find('='); eq_pos != std::string::npos) {
                std::string key = item.substr(0, eq_pos);
                std::string val = item.substr(eq_pos + 1);
                device_config.fuses[key] = val;
            }
        }
    }

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

    std::vector<std::string> include_paths;
    if (program.is_used("-I")) {
        include_paths = program.get<std::vector<std::string>>("-I");
    }
    include_paths.emplace_back(".");

    try {
        Lexer lexer(source);
        const auto tokens = lexer.tokenize();

        Parser parser(tokens);
        const auto ast = parser.parseProgram();

        std::vector<std::unique_ptr<Program>> loaded_modules;
        std::vector<const Program*> imported_programs;

        for (const auto& importStmt : ast->imports) {
            try {
                std::string mod_path = resolve_module(importStmt->module_name, include_paths);
                std::cout << "[pymcuc] Importing " << importStmt->module_name << " from " << mod_path << "\n";
                std::string mod_src = read_source(mod_path);

                Lexer mod_lexer(mod_src);
                auto mod_tokens = mod_lexer.tokenize();

                Parser mod_parser(mod_tokens);
                auto mod_ast = mod_parser.parseProgram();

                imported_programs.push_back(mod_ast.get());
                loaded_modules.push_back(std::move(mod_ast));

            } catch (const std::exception& e) {
                std::cerr << "Import Error: " << e.what() << "\n";
                return 1;
            }
        }

        IRGenerator irGen;
        auto ir = irGen.generate(*ast, imported_programs);

        auto backend = CodeGenFactory::create(arch, device_config);

        std::ofstream asm_file(output_path);
        if (!asm_file.is_open()) {
            throw std::runtime_error("Cannot open output file: " + output_path);
        }

        std::cout << "[pymcuc] Compiling " << filepath << " -> " << output_path
                  << " (" << arch << " @ " << device_config.frequency << "Hz)\n";

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