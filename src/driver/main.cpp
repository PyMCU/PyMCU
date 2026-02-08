#include <toml++/toml.hpp>
#include <argparse/argparse.hpp>
#include <expected>
#include <filesystem>
#include <cstdlib>

namespace fs = std::filesystem;

std::expected<toml::table, std::string> load_target_config(const std::string& target_name) {
    try {
        // En un caso real, buscarías en /usr/share/pymcu/config o local
        auto config = toml::parse_file("config/" + target_name + ".toml");
        return config;
    } catch (const toml::parse_error& err) {
        return std::unexpected(std::format("Error parseando TOML: {}", err.description()));
    }
}

int main(int argc, char* argv[]) {
    argparse::ArgumentParser program("pymcu");
    program.add_argument("file").help("Source File .py");
    program.add_argument("-t", "--target").default_value(std::string("pic16f84a"));

    try {
        program.parse_args(argc, argv);
    } catch (const std::exception& err) {
        std::println(stderr, "{}", err.what());
        return 1;
    }

    auto target = program.get<std::string>("--target");
    auto config_result = load_target_config(target);

    if (!config_result) {
        std::println(stderr, "Fatal: {}", config_result.error());
        return 1;
    }

    toml::table cfg = *config_result;

    // Extraer datos críticos para pasarlos al compilador
    auto ram_start = cfg["memory"]["ram_start"].value_or(0x00);
    auto arch = cfg["device"]["arch"].value_or("unknown");

    std::println("Target detectado: {} (Arch: {})",
        cfg["device"]["name"].value_or("Generic"), arch);

    // 1. Invocar al Compilador (pymcuc)
    // Pasamos la info del TOML como argumentos al compilador
    std::string cmd_compile = std::format(
        "./bin/pymcuc {} -o output.asm --arch {} --ram-start {}",
        program.get<std::string>("file"),
        arch,
        ram_start
    );

    std::println("Ejecutando: {}", cmd_compile);
    int ret = std::system(cmd_compile.c_str());
    if (ret != 0) return ret;

    // 2. Invocar a GPASM (usando flags del TOML)
    // ... lógica similar para gpasm y gplink ...

    return 0;
}