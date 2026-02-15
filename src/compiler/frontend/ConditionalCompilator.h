#ifndef CONDITIONAL_H
#define CONDITIONAL_H

#include <string>
#include <vector>

#include "Ast.h"

class ConditionalCompilator {
 public:
  explicit ConditionalCompilator(std::string target_chip);
  void process(Program& program);

 private:
  std::string target_chip;

  // Returns true if the statement was an IfStmt handled (and thus should be
  // removed/replaced) Returns false if it's a normal statement to keep
  bool process_statement(const Statement* stmt, Program& prog,
                         std::vector<std::unique_ptr<Statement>>& new_globals);

  // Evaluates a condition. Only supports: __CHIP__ == "literal"
  bool evaluate_condition(const Expression* expr);
};

#endif
