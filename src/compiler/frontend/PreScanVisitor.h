#ifndef PRESCANVISITOR_H
#define PRESCANVISITOR_H

#include <memory>
#include <string>

#include "../../common/DeviceConfig.h"
#include "Ast.h"

// Scans the AST for configuration calls like device_info()
// This runs BEFORE type checking or code generation.
class PreScanVisitor {
 public:
  explicit PreScanVisitor(DeviceConfig& config);

  void scan(const Program& program) const;

 private:
  DeviceConfig& config;

  void visit_statement(const Statement* stmt) const;
  void handle_device_info(const CallExpr* call) const;
};

#endif  // PRESCANVISITOR_H
