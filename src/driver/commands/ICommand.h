#ifndef ICOMMAND_H
#define ICOMMAND_H

#include <argparse/argparse.hpp>

class ICommand {
public:
    virtual ~ICommand() = default;
    virtual void configure(argparse::ArgumentParser& parser) = 0;
    virtual void execute(const argparse::ArgumentParser& parser) = 0;
};

#endif