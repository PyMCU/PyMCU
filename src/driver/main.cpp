#include <iostream>
#include <string>
#include <argparse/argparse.hpp>
#include "commands/CmdNew.h"
#include "commands/CmdBuild.h"

int main(const int argc, char* argv[]) {
    argparse::ArgumentParser program("pymcu");

    CmdNew cmd_new;
    CmdBuild cmd_build;

    argparse::ArgumentParser new_parser("new");
    argparse::ArgumentParser build_parser("build");

    cmd_new.configure(new_parser);
    cmd_build.configure(build_parser);

    program.add_subparser(new_parser);
    program.add_subparser(build_parser);

    try {
        program.parse_args(argc, argv);
    } catch (const std::runtime_error& err) {
        std::cerr << err.what() << std::endl;
        std::cerr << program;
        return 1;
    }

    try {
        if (program.is_subcommand_used("new")) {
            cmd_new.execute(new_parser);
        }
        else if (program.is_subcommand_used("build")) {
            cmd_build.execute(build_parser);
        }
        else {
            std::cout << program;
        }
    } catch (const std::exception& e) {
        std::cerr << "Error: " << e.what() << "\n";
        return 1;
    }

    return 0;
}