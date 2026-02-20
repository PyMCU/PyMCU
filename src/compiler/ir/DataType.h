/*
 * -----------------------------------------------------------------------------
 * PyMCU Compiler (pymcuc)
 * Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
 *
 * This file is part of the PyMCU Development Ecosystem.
 *
 * PyMCU is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * PyMCU is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with PyMCU.  If not, see <https://www.gnu.org/licenses/>.
 *
 * -----------------------------------------------------------------------------
 * SAFETY WARNING / HIGH RISK ACTIVITIES:
 * THE SOFTWARE IS NOT DESIGNED, MANUFACTURED, OR INTENDED FOR USE IN HAZARDOUS
 * ENVIRONMENTS REQUIRING FAIL-SAFE PERFORMANCE, SUCH AS IN THE OPERATION OF
 * NUCLEAR FACILITIES, AIRCRAFT NAVIGATION OR COMMUNICATION SYSTEMS, AIR
 * TRAFFIC CONTROL, DIRECT LIFE SUPPORT MACHINES, OR WEAPONS SYSTEMS.
 * -----------------------------------------------------------------------------
 */

#ifndef DATATYPE_H
#define DATATYPE_H

#pragma once
#include <string>

enum class DataType {
  UINT8,
  INT8,
  UINT16,
  INT16,
  UINT32,
  INT32,
  FLOAT,  // Placeholder for future support
  VOID,
  UNKNOWN
};

/// Returns the byte count for a given DataType.
inline int size_of(DataType type) {
  switch (type) {
    case DataType::UINT8:
    case DataType::INT8:
      return 1;
    case DataType::UINT16:
    case DataType::INT16:
      return 2;
    case DataType::UINT32:
    case DataType::INT32:
    case DataType::FLOAT:
      return 4;
    default:
      return 1;  // Default to 1 byte for VOID/UNKNOWN
  }
}

/// Returns true if the DataType is a signed integer type.
inline bool is_signed(DataType type) {
  switch (type) {
    case DataType::INT8:
    case DataType::INT16:
    case DataType::INT32:
      return true;
    default:
      return false;
  }
}

/// Maps a Python type annotation string to an internal DataType enum.
inline DataType string_to_datatype(const std::string &type_str) {
  if (type_str == "uint8" || type_str.empty())
    return DataType::UINT8;
  if (type_str == "int") return DataType::UINT16;
  if (type_str == "int8") return DataType::INT8;
  if (type_str == "uint16") return DataType::UINT16;
  if (type_str == "int16") return DataType::INT16;
  if (type_str == "uint32") return DataType::UINT32;
  if (type_str == "int32") return DataType::INT32;
  if (type_str == "float") return DataType::FLOAT;
  if (type_str == "const") return DataType::UINT8;  // Compile-time only, never allocated
  // Handle const[TYPE] — extract inner type (e.g., const[uint8] -> uint8)
  if (type_str.find("const[") == 0 && type_str.back() == ']') {
    std::string inner = type_str.substr(6, type_str.size() - 7);
    return string_to_datatype(inner);
  }
  if (type_str == "void" || type_str == "None") return DataType::VOID;
  // For pointer/register types, treat as UINT16 (address-level for AVR)
  if (type_str.find("ptr") != std::string::npos ||
      type_str.find("PIORegister") != std::string::npos)
    return DataType::UINT16;
  return DataType::UNKNOWN;
}

/// Returns the promoted type when combining two operand types.
/// Rule: promote to the larger type. If same size but differing
/// signedness, promote to the signed variant of next larger size.
inline DataType get_promoted_type(DataType a, DataType b) {
  if (a == b) return a;

  int size_a = size_of(a);
  int size_b = size_of(b);

  // Promote to the larger type
  if (size_a > size_b) return a;
  if (size_b > size_a) return b;

  // Same size, differing signedness — promote to signed variant of next size
  bool a_signed = is_signed(a);
  bool b_signed = is_signed(b);

  if (a_signed != b_signed) {
    switch (size_a) {
      case 1:
        return DataType::INT16;
      case 2:
        return DataType::INT32;
      default:
        return DataType::INT32;  // Cap at 32-bit
    }
  }

  // Same size, same signedness — prefer 'a'
  return a;
}

#endif  // DATATYPE_H