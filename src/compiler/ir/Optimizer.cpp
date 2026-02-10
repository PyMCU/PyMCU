#include "Optimizer.h"
#include <variant>
#include <map>
#include <set>
#include <algorithm>

tacky::Program Optimizer::optimize(const tacky::Program& program) {
    tacky::Program optimized = program;
    for (auto& func : optimized.functions) {
        optimize_function(func);
    }
    return optimized;
}

void Optimizer::optimize_function(tacky::Function& func) {
    for (int i = 0; i < 10; ++i) { // Fixed number of passes for simplicity
        propagate_copies(func);
        fold_constants(func);
        coalesce_instructions(func);
        eliminate_dead_code(func);
    }
}

static std::optional<int> get_constant(const tacky::Val& val) {
    if (auto c = std::get_if<tacky::Constant>(&val)) {
        return c->value;
    }
    return std::nullopt;
}

void Optimizer::fold_constants(tacky::Function& func) {
    for (auto& instr : func.body) {
        if (auto* binary = std::get_if<tacky::Binary>(&instr)) {
            auto c1 = get_constant(binary->src1);
            auto c2 = get_constant(binary->src2);
            if (c1 && c2) {
                int result = 0;
                bool foldable = true;
                switch (binary->op) {
                    case tacky::BinaryOp::Add: result = *c1 + *c2; break;
                    case tacky::BinaryOp::Sub: result = *c1 - *c2; break;
                    case tacky::BinaryOp::Mul: result = *c1 * *c2; break;
                    case tacky::BinaryOp::Div: if (*c2 != 0) result = *c1 / *c2; else foldable = false; break;
                    case tacky::BinaryOp::Mod: if (*c2 != 0) result = *c1 % *c2; else foldable = false; break;
                    case tacky::BinaryOp::Equal: result = (*c1 == *c2); break;
                    case tacky::BinaryOp::NotEqual: result = (*c1 != *c2); break;
                    case tacky::BinaryOp::LessThan: result = (*c1 < *c2); break;
                    case tacky::BinaryOp::LessEqual: result = (*c1 <= *c2); break;
                    case tacky::BinaryOp::GreaterThan: result = (*c1 > *c2); break;
                    case tacky::BinaryOp::GreaterEqual: result = (*c1 >= *c2); break;
                    case tacky::BinaryOp::BitAnd: result = *c1 & *c2; break;
                    case tacky::BinaryOp::BitOr:  result = *c1 | *c2; break;
                    case tacky::BinaryOp::BitXor: result = *c1 ^ *c2; break;
                    case tacky::BinaryOp::LShift: result = *c1 << *c2; break;
                    case tacky::BinaryOp::RShift: result = *c1 >> *c2; break;
                    default: foldable = false; break;
                }
                if (foldable) {
                    instr = tacky::Copy{tacky::Constant{result}, binary->dst};
                }
            }
        } else if (auto* unary = std::get_if<tacky::Unary>(&instr)) {
            auto c = get_constant(unary->src);
            if (c) {
                int result = 0;
                bool foldable = true;
                switch (unary->op) {
                    case tacky::UnaryOp::Neg: result = -(*c); break;
                    case tacky::UnaryOp::Not: result = !(*c); break;
                    case tacky::UnaryOp::BitNot: result = ~(*c); break;
                    default: foldable = false; break;
                }
                if (foldable) {
                    instr = tacky::Copy{tacky::Constant{result}, unary->dst};
                }
            }
        }
    }
}

void Optimizer::eliminate_dead_code(tacky::Function& func) {
    // Simple DCE: remove Copy/Binary/Unary/Call if dst is a Temporary and never used.
    // Note: Call might have side effects, so we should be careful. 
    // But for now let's focus on Temporaries which are strictly internal.
    
    std::set<std::string> used_temps;
    auto register_use = [&](const tacky::Val& val) {
        if (auto t = std::get_if<tacky::Temporary>(&val)) {
            used_temps.insert(t->name);
        }
    };

    for (const auto& instr : func.body) {
        std::visit([&](auto&& arg) {
            using T = std::decay_t<decltype(arg)>;
            if constexpr (std::is_same_v<T, tacky::Return>) register_use(arg.value);
            else if constexpr (std::is_same_v<T, tacky::Unary>) register_use(arg.src);
            else if constexpr (std::is_same_v<T, tacky::Binary>) {
                register_use(arg.src1);
                register_use(arg.src2);
            }
            else if constexpr (std::is_same_v<T, tacky::Copy>) register_use(arg.src);
            else if constexpr (std::is_same_v<T, tacky::JumpIfZero>) register_use(arg.condition);
            else if constexpr (std::is_same_v<T, tacky::JumpIfNotZero>) register_use(arg.condition);
            else if constexpr (std::is_same_v<T, tacky::Call>) {
                for (const auto& arg_val : arg.args) register_use(arg_val);
            }
            else if constexpr (std::is_same_v<T, tacky::BitCheck>) register_use(arg.source);
            else if constexpr (std::is_same_v<T, tacky::BitWrite>) {
                register_use(arg.target);
                register_use(arg.src);
            }
            else if constexpr (std::is_same_v<T, tacky::BitSet>) register_use(arg.target);
            else if constexpr (std::is_same_v<T, tacky::BitClear>) register_use(arg.target);
        }, instr);
    }

    auto is_dead = [&](const tacky::Instruction& instr) {
        return std::visit([&](auto&& arg) -> bool {
            using T = std::decay_t<decltype(arg)>;
            const tacky::Val* dst_ptr = nullptr;
            if constexpr (requires { arg.dst; }) {
                if constexpr (!std::is_same_v<T, tacky::Call>) {
                    dst_ptr = &arg.dst;
                }
            }
            
            if (dst_ptr) {
                if (auto t = std::get_if<tacky::Temporary>(dst_ptr)) {
                    return used_temps.find(t->name) == used_temps.end();
                }
            }
            return false;
        }, instr);
    };

    func.body.erase(std::remove_if(func.body.begin(), func.body.end(), is_dead), func.body.end());
}

void Optimizer::propagate_copies(tacky::Function& func) {
    std::map<std::string, tacky::Val> temp_copies;

    for (auto& instr : func.body) {
        // 1. Substitute uses of temporaries
        std::visit([&](auto&& arg) {
            using T = std::decay_t<decltype(arg)>;
            auto replace_val = [&](tacky::Val& v) {
                if (auto t = std::get_if<tacky::Temporary>(&v)) {
                    if (temp_copies.contains(t->name)) {
                        v = temp_copies[t->name];
                    }
                }
            };

            if constexpr (std::is_same_v<T, tacky::Return>) replace_val(arg.value);
            else if constexpr (std::is_same_v<T, tacky::Unary>) replace_val(arg.src);
            else if constexpr (std::is_same_v<T, tacky::Binary>) {
                replace_val(arg.src1);
                replace_val(arg.src2);
            }
            else if constexpr (std::is_same_v<T, tacky::Copy>) replace_val(arg.src);
            else if constexpr (std::is_same_v<T, tacky::JumpIfZero>) replace_val(arg.condition);
            else if constexpr (std::is_same_v<T, tacky::JumpIfNotZero>) replace_val(arg.condition);
            else if constexpr (std::is_same_v<T, tacky::Call>) {
                for (auto& v : arg.args) replace_val(v);
            }
            else if constexpr (std::is_same_v<T, tacky::BitCheck>) replace_val(arg.source);
            else if constexpr (std::is_same_v<T, tacky::BitWrite>) {
                replace_val(arg.src);
                replace_val(arg.target);
            }
            else if constexpr (std::is_same_v<T, tacky::BitSet>) replace_val(arg.target);
            else if constexpr (std::is_same_v<T, tacky::BitClear>) replace_val(arg.target);
        }, instr);

        // 2. Track new copies to temporaries
        if (auto* copy = std::get_if<tacky::Copy>(&instr)) {
            if (auto t_dst = std::get_if<tacky::Temporary>(&copy->dst)) {
                temp_copies[t_dst->name] = copy->src;
            }
        }
    }
}

void Optimizer::coalesce_instructions(tacky::Function& func) {
    std::map<std::string, int> use_count;
    auto register_use = [&](const tacky::Val& v) {
        if (auto t = std::get_if<tacky::Temporary>(&v)) {
            use_count[t->name]++;
        }
    };

    for (const auto& instr : func.body) {
        std::visit([&](auto&& arg) {
            using T = std::decay_t<decltype(arg)>;
            if constexpr (std::is_same_v<T, tacky::Return>) register_use(arg.value);
            else if constexpr (std::is_same_v<T, tacky::Unary>) register_use(arg.src);
            else if constexpr (std::is_same_v<T, tacky::Binary>) {
                register_use(arg.src1);
                register_use(arg.src2);
            }
            else if constexpr (std::is_same_v<T, tacky::Copy>) register_use(arg.src);
            else if constexpr (std::is_same_v<T, tacky::JumpIfZero>) register_use(arg.condition);
            else if constexpr (std::is_same_v<T, tacky::JumpIfNotZero>) register_use(arg.condition);
            else if constexpr (std::is_same_v<T, tacky::Call>) {
                for (const auto& v : arg.args) register_use(v);
            }
            else if constexpr (std::is_same_v<T, tacky::BitCheck>) register_use(arg.source);
            else if constexpr (std::is_same_v<T, tacky::BitWrite>) {
                register_use(arg.src);
                register_use(arg.target);
            }
            else if constexpr (std::is_same_v<T, tacky::BitSet>) register_use(arg.target);
            else if constexpr (std::is_same_v<T, tacky::BitClear>) register_use(arg.target);
        }, instr);
    }

    std::vector<tacky::Instruction> new_body;
    for (size_t i = 0; i < func.body.size(); ++i) {
        if (i + 1 < func.body.size()) {
            auto* next_copy = std::get_if<tacky::Copy>(&func.body[i+1]);
            if (next_copy) {
                if (auto t_src = std::get_if<tacky::Temporary>(&next_copy->src)) {
                    if (use_count[t_src->name] == 1) {
                        tacky::Instruction current = func.body[i];
                        bool coalesced = std::visit([&](auto&& arg) -> bool {
                            using T = std::decay_t<decltype(arg)>;
                            if constexpr (requires { arg.dst; }) {
                                if (auto t_dst = std::get_if<tacky::Temporary>(&arg.dst)) {
                                    if (t_dst->name == t_src->name) {
                                        arg.dst = next_copy->dst;
                                        return true;
                                    }
                                }
                            }
                            return false;
                        }, current);
                        
                        if (coalesced) {
                            new_body.push_back(current);
                            i++; // skip the copy
                            continue;
                        }
                    }
                }
            }
        }
        new_body.push_back(func.body[i]);
    }
    func.body = new_body;
}
