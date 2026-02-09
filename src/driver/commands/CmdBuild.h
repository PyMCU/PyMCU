#ifndef CMD_BUILD_H
#define CMD_BUILD_H

#include "ICommand.h"
#include "../Toolchain.h"
#include "toml++/toml.hpp"
#include <iostream>

class CmdBuild : public ICommand {
public:
    void configure(argparse::ArgumentParser& parser) override {
        parser.add_description("Builds the current project based on pyproject.toml");
        // Here other flags can be added in the future like --verbose or --release
    }

    void execute(const argparse::ArgumentParser& parser) override {
        if (!fs::exists("pyproject.toml")) {
            throw std::runtime_error("No pyproject.toml found. Are you in a pymcu project?");
        }

        try {
            auto config = toml::parse_file("pyproject.toml");

            std::string chip = config["tool"]["pymcu"]["chip"].value_or("pic16f84a");
            int64_t freq = config["tool"]["pymcu"]["frequency"].value_or(4000000);

            std::map<std::string, std::string> config_map;
            if (auto tbl = config["tool"]["pymcu"]["config"].as_table()) {
                for (auto& [key, val] : *tbl) {
                    if (val.is_string()) {
                        config_map[std::string(key.str())] = val.as_string()->get();
                    } else if (val.is_integer()) {
                        config_map[std::string(key.str())] = std::to_string(val.as_integer()->get());
                    }
                }
            }

            std::string entry_point = "src/main.py";
            std::string output_dir = "dist";
            std::string output_file = output_dir + "/firmware.asm";

            if (!fs::exists(entry_point)) {
                throw std::runtime_error("Entry point 'src/main.py' not found.");
            }

            if (!fs::exists(output_dir)) fs::create_directory(output_dir);

            std::cout << "[pymcu] Building project for " << chip << " (" << freq/1000000 << "MHz)...\n";
            
            Toolchain::run_compiler(entry_point, output_file, chip, freq, config_map);
            
            std::cout << "[pymcu] Build successful! Artifact: " << output_file << "\n";

        } catch (const toml::parse_error& err) {
            throw std::runtime_error(std::string("Error parsing pyproject.toml: ") + err.what());
        }
    }
};

#endif