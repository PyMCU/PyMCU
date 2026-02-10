//
// Created by Ivan Montiel Cardona on 09/02/26.
//

#ifndef STACK_ALLOCATOR_H
#define STACK_ALLOCATOR_H


#pragma once
#include <map>
#include <string>
#include <vector>
#include <set>

#include "ir/Tacky.h"

class StackAllocator {
public:
    std::pair<std::map<std::string, int>, int> allocate(const tacky::Program& program);
private:
    struct FunctionNode {
        std::string name;
        int local_size = 0;
        std::vector<std::string> callees;
        std::set<std::string> locals;
        bool visited = false;
    };

    std::map<std::string, FunctionNode> call_graph;
    std::map<std::string, int> offsets;
    std::map<std::string, int> offsets_base;
    int max_stack_usage = 0;

    void build_graph(const tacky::Program& program);
    void calculate_offsets(const std::string& func_name, int current_base);
};


#endif //STACK_ALLOCATOR_H