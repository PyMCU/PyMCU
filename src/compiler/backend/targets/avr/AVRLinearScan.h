/*
 * Whisnake Compiler (whipc) — AVR Linear Scan Register Allocator (Temporaries)
 * Copyright (C) 2026 Ivan Montiel Cardona and the Whisnake Project Authors
 *
 * Licensed under the GNU General Public License v3. See LICENSE for details.
 *
 * Assigns R16/R17 to short-lived temporaries that do NOT span function calls,
 * eliminating STD Y+N / LDD Y+N traffic for those values.
 */

#ifndef AVRLINEARSCAN_H
#define AVRLINEARSCAN_H

#pragma once
#include <map>
#include <string>

#include "../../../ir/Tacky.h"

class AVRLinearScan {
 public:
  // Per-function: assign R16/R17 to UINT8 temporaries whose live intervals
  // do NOT strictly span any Call instruction (i.e., def < call_idx < last_use).
  // Temporaries whose last_use == call_idx are safe: the value is consumed as
  // a function argument before the call dispatches, so it can live in R16/R17.
  // Returns: map from tmp_name → "R16" or "R17".
  static std::map<std::string, std::string> allocate(const tacky::Function &func);
};

#endif  // AVRLINEARSCAN_H
