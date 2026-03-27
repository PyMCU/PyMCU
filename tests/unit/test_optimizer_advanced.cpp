#include <gtest/gtest.h>
#include "ir/Optimizer.h"
#include <variant>

using namespace tacky;

TEST(OptimizerAdvancedTest, CopyPropagation) {
    Function func;
    func.name = "test";
    // x = param   (non-constant source -- tests pure copy propagation)
    // t1 = x
    // return t1
    // propagate_copies also folds constants through variables, so using a
    // constant source (x = 1) would propagate all the way to return 1.
    // A variable source keeps the test focused on copy-chain substitution.
    func.body.push_back(Copy{Variable{"param"}, Variable{"x"}});
    func.body.push_back(Copy{Variable{"x"}, Temporary{"t1"}});
    func.body.push_back(Return{Temporary{"t1"}});

    Optimizer::propagate_copies(func);

    // After propagation:
    // x = param
    // t1 = x
    // return x    (t1 was a single-definition copy of x, substituted)
    ASSERT_EQ(func.body.size(), 3);
    auto ret = std::get_if<Return>(&func.body[2]);
    ASSERT_NE(ret, nullptr);
    auto val = std::get_if<Variable>(&ret->value);
    ASSERT_NE(val, nullptr);
    EXPECT_EQ(val->name, "x");
}

TEST(OptimizerAdvancedTest, InstructionCoalescing) {
    Function func;
    func.name = "test";
    // t1 = a + b
    // x = t1
    func.body.push_back(Binary{BinaryOp::Add, Variable{"a"}, Variable{"b"}, Temporary{"t1"}});
    func.body.push_back(Copy{Temporary{"t1"}, Variable{"x"}});

    Optimizer::coalesce_instructions(func);

    // After coalescing:
    // x = a + b
    ASSERT_EQ(func.body.size(), 1);
    auto bin = std::get_if<Binary>(&func.body[0]);
    ASSERT_NE(bin, nullptr);
    auto dst = std::get_if<Variable>(&bin->dst);
    ASSERT_NE(dst, nullptr);
    EXPECT_EQ(dst->name, "x");
}

TEST(OptimizerAdvancedTest, FullOptimizationChain) {
    Function func;
    func.name = "main";  // must be "main" to survive Dead Function Elimination (DFE)
    // x = 10
    // y = 20
    // t1 = x + y
    // res = t1
    // return res
    func.body.push_back(Copy{Constant{10}, Variable{"x"}});
    func.body.push_back(Copy{Constant{20}, Variable{"y"}});
    func.body.push_back(Binary{BinaryOp::Add, Variable{"x"}, Variable{"y"}, Temporary{"t1"}});
    func.body.push_back(Copy{Temporary{"t1"}, Variable{"res"}});
    func.body.push_back(Return{Variable{"res"}});

    // Constant Folding will see x and y are NOT temporaries, so it won't fold them yet if they are variables.
    // Wait, currently Optimizer only folds temporaries.

    // Let's use temporaries for constant folding test
    func.body.clear();
    func.body.push_back(Copy{Constant{10}, Temporary{"t1"}});
    func.body.push_back(Copy{Constant{20}, Temporary{"t2"}});
    func.body.push_back(Binary{BinaryOp::Add, Temporary{"t1"}, Temporary{"t2"}, Temporary{"t3"}});
    func.body.push_back(Copy{Temporary{"t3"}, Variable{"res"}});
    func.body.push_back(Return{Variable{"res"}});

    tacky::Program prog;
    prog.functions.push_back(func);
    auto optimized_prog = Optimizer::optimize(prog);
    auto &optimized = optimized_prog.functions[0];

    // t1 = 10
    // t2 = 20
    // t3 = 10 + 20 => t3 = 30
    // res = t3 => res = 30
    // return res

    // With DCE and Coalescing:
    // res = 30
    // return res

    // Wait, DCE removes t1, t2, t3 if they are temporaries and not used.
    // After coalescing: res = 30.

    bool found_res_30 = false;
    for (const auto &inst: optimized.body) {
        if (auto copy = std::get_if<Copy>(&inst)) {
            if (auto c = std::get_if<Constant>(&copy->src)) {
                if (c->value == 30) {
                    if (auto v = std::get_if<Variable>(&copy->dst)) {
                        if (v->name == "res") found_res_30 = true;
                    }
                }
            }
        }
    }
    EXPECT_TRUE(found_res_30);
}