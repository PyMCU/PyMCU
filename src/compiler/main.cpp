#include <argparse/argparse.hpp>
#include <fstream>
#include <iostream>
#include <map>
#include <sstream>
#include <vector>

#include "DeviceConfig.h"
#include "Diagnostic.h"
#include "Errors.h"
#include "Utils.h"
#include "backend/CodeGenFactory.h"
#include "frontend/Lexer.h"
#include "frontend/Parser.h"
#include "ir/IRGenerator.h"
#include "ir/Optimizer.h"
#include "ir/Tacky.h"

namespace fs = std::filesystem;

std::map<std::string, std::unique_ptr<Program> > module_cache;
std::vector<const Program *> linear_imports;

std::string resolve_module(const std::string &module_name,
                           const std::vector<std::string> &include_paths,
                           const fs::path &current_file_path,
                           const int relative_level) {
    std::string path_rel = module_name;
    std::ranges::replace(path_rel, '.', fs::path::preferred_separator);

    if (relative_level > 0) {
        fs::path search_dir = current_file_path.parent_path();

        for (int i = 1; i < relative_level; ++i) {
            search_dir = search_dir.parent_path();
        }

        fs::path full_path = search_dir / (path_rel + ".py");

        if (fs::exists(full_path))
            return full_path.string();

        full_path = search_dir / path_rel / "__init__.py";
        if (fs::exists(full_path))
            return full_path.string();

        throw std::runtime_error("Relative import not found: " +
                                 full_path.string());
    }

    for (const auto &base: include_paths) {
        fs::path full_path = fs::path(base) / (path_rel + ".py");
        if (fs::exists(full_path))
            return full_path.string();

        full_path = fs::path(base) / path_rel / "__init__.py";
        if (fs::exists(full_path))
            return full_path.string();
    }

    throw std::runtime_error("Module not found: " + module_name);
}

void load_imports_recursively(const Program *ast, const fs::path &current_path,
                              const std::vector<std::string> &includes) {
    for (const auto &imp: ast->imports) {
        try {
            if (imp->module_name == "pymcu.types") {
                // Intrinsics
                continue;
            }

            std::string path = resolve_module(imp->module_name, includes,
                                              current_path, imp->relative_level);

            if (module_cache.contains(path))
                continue;

            std::cout << "Loading module: " << path << "\n";

            std::string src = read_source(path);
            Lexer l(src);
            const auto tokens = l.tokenize();
            Parser p(tokens);
            auto mod_ast = p.parseProgram();

            load_imports_recursively(mod_ast.get(), path, includes);

            auto &inserted_ptr = module_cache[path] = std::move(mod_ast);
            linear_imports.push_back(inserted_ptr.get());
        } catch (const std::exception &e) {
            std::cerr << "Error importing '" << imp->module_name << "': " << e.what()
                    << "\n";
            exit(1);
        }
    }
}

int main(int argc, char *argv[]) {
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
    } catch (const std::exception &err) {
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
        for (auto config_list = program.get<std::vector<std::string> >("-C");
             const auto &item: config_list) {
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

    std::vector<std::string> source_lines; // Move to outer scope
    std::string source;
    try {
        source = read_source(filepath);
        std::istringstream stream(source);
        std::string line;
        while (std::getline(stream, line)) {
            source_lines.push_back(line);
        }
    } catch (const std::exception &e) {
        std::cerr << "Fatal Error: " << e.what() << "\n";
        return 1;
    }

    std::vector<std::string> include_paths;
    if (program.is_used("-I")) {
        include_paths = program.get<std::vector<std::string> >("-I");
    }
    include_paths.emplace_back(".");

    try {
        Lexer lexer(source);
        const auto tokens = lexer.tokenize();

        Parser parser(tokens);
        const auto ast = parser.parseProgram();

        load_imports_recursively(ast.get(), fs::path(filepath), include_paths);

        // IR Generation
        IRGenerator ir_gen;
        auto ir = ir_gen.generate(*ast, linear_imports, source_lines);

        // ir = Optimizer::optimize(ir);

        auto backend = CodeGenFactory::create(arch, device_config);

        std::ofstream asm_file(output_path);
        if (!asm_file.is_open()) {
            throw std::runtime_error("Cannot open output file: " + output_path);
        }

        std::cout << "[pymcuc] Compiling " << filepath << " -> " << output_path
                << " (" << arch << " @ " << device_config.frequency << "Hz)\n";

        backend->compile(ir, asm_file);

        std::cout << "[pymcuc] Success! Output written to " << output_path << "\n";
    } catch (const CompilerError &e) {
        std::cerr << "Error in " << filepath << ":" << e.line << ": " << e.what()
                << "\n";
        // Print the line
        if (e.line > 0 && e.line <= (int) source_lines.size()) {
            std::cerr << "    " << source_lines[e.line - 1] << "\n";
            // Simple pointer to column if valid
            if (e.column > 0) {
                std::string pointer(e.column - 1, ' ');
                pointer += "^";
                std::cerr << "    " << pointer << "\n";
            }
        }
        return 1;
    } catch (const std::exception &e) {
        std::cerr << "Internal Compiler Error: " << e.what() << "\n";
        return 1;
    }

    return 0;
}
