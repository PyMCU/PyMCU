#ifndef CMD_NEW_H
#define CMD_NEW_H

#include "ICommand.h"
#include <fstream>
#include <iostream>
#include <filesystem>
#include <optional>

namespace fs = std::filesystem;

class CmdNew : public ICommand {
public:
    void configure(argparse::ArgumentParser& parser) override {
        parser.add_description("Creates a new pymcu project scaffold");
        parser.add_argument("name").help("Name of the project").required();
        parser.add_argument("-m", "--mcu").help("Target MCU (e.g., pic16f84a)");
    }

    void execute(const argparse::ArgumentParser& parser) override {
        const auto name = parser.get<std::string>("name");
        std::optional<std::string> chip_opt;
        
        if (parser.present("--mcu")) {
            chip_opt = parser.get<std::string>("--mcu");
        }

        create_project(name, chip_opt);
    }

private:
    void create_project(const std::string& project_name, const std::optional<std::string>& chip_opt) {
        fs::path project_path = project_name;
        if (fs::exists(project_path)) {
            throw std::runtime_error("Directory '" + project_name + "' already exists.");
        }

        std::string chip = chip_opt.value_or("");
        if (chip.empty()) {
            std::cout << "Target MCU [pic16f84a]: ";
            std::getline(std::cin, chip);
            if (chip.empty()) chip = "pic16f84a";
        }

        long long freq = 4000000;

        fs::create_directories(project_path / "src");

        // pyproject.toml
        std::ofstream toml_file(project_path / "pyproject.toml");
        toml_file << "[project]\n"
                  << "name = \"" << project_name << "\"\n"
                  << "version = \"0.1.0\"\n"
                  << "dependencies = [\n"
                  << "    \"pymcu-stdlib\",\n"
                  << "]\n\n"
                  << "[[tool.uv.index]]\n"
                  << "name = \"gitea\"\n"
                  << "url = \"https://gitea.begeistert.dev/api/packages/begeistert/pypi/simple\"\n"
                  << "explicit = true\n\n"
                  << "[tool.uv.sources]\n"
                  << "pymcu-stdlib = {index = \"gitea\"}\n\n"
                  << "[tool.pymcu]\n"
                  << "chip = \"" << chip << "\"\n"
                  << "frequency = " << freq << "\n\n"
                  << "[tool.pymcu.config]\n"
                  << "# FOSC = \"HS\"\n";
        toml_file.close();

        // src/main.py
        std::ofstream main_py(project_path / "src/main.py");
        main_py << "from pymcu.chips." << chip << " import *\n\n";
        main_py << "def main():\n    PORTB[RB0] = 1\n";
        main_py.close();

        std::cout << "[pymcu] Project '" << project_name << "' created successfully!\n";
    }
};

#endif