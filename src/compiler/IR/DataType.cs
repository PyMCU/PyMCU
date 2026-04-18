/*
 * -----------------------------------------------------------------------------
 * PyMCU Compiler (pymcuc)
 * Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
 *
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *
 * -----------------------------------------------------------------------------
 * SAFETY WARNING / HIGH RISK ACTIVITIES:
 * THE SOFTWARE IS NOT DESIGNED, MANUFACTURED, OR INTENDED FOR USE IN HAZARDOUS
 * ENVIRONMENTS REQUIRING FAIL-SAFE PERFORMANCE, SUCH AS IN THE OPERATION OF
 * NUCLEAR FACILITIES, AIRCRAFT NAVIGATION OR COMMUNICATION SYSTEMS, AIR
 * TRAFFIC CONTROL, DIRECT LIFE SUPPORT MACHINES, OR WEAPONS SYSTEMS.
 * -----------------------------------------------------------------------------
 */

namespace PyMCU.IR;

public enum DataType
{
    UINT8,
    INT8,
    UINT16,
    INT16,
    UINT32,
    INT32,
    FLOAT, // Placeholder for future support
    VOID,
    UNKNOWN
}

public static class DataTypeExtensions
{
    /// Returns the byte count for a given DataType.
    public static int SizeOf(this DataType type)
    {
        switch (type)
        {
            case DataType.UINT8:
            case DataType.INT8:
                return 1;
            case DataType.UINT16:
            case DataType.INT16:
                return 2;
            case DataType.UINT32:
            case DataType.INT32:
            case DataType.FLOAT:
                return 4;
            default:
                return 1; // Default to 1 byte for VOID/UNKNOWN
        }
    }

    /// Returns true if the DataType is a signed integer type.
    public static bool IsSigned(this DataType type)
    {
        switch (type)
        {
            case DataType.INT8:
            case DataType.INT16:
            case DataType.INT32:
                return true;
            default:
                return false;
        }
    }

    /// Maps a Python type annotation string to an internal DataType enum.
    public static DataType StringToDataType(string typeStr)
    {
        if (string.IsNullOrEmpty(typeStr) || typeStr == "uint8")
            return DataType.UINT8;
        if (typeStr == "int") return DataType.UINT16;
        if (typeStr == "int8") return DataType.INT8;
        if (typeStr == "uint16") return DataType.UINT16;
        if (typeStr == "int16") return DataType.INT16;
        if (typeStr == "uint32") return DataType.UINT32;
        if (typeStr == "int32") return DataType.INT32;
        if (typeStr == "float") return DataType.FLOAT;
        if (typeStr == "const") return DataType.UINT8; // Compile-time only, never allocated

        // Handle const[TYPE] — extract inner type (e.g., const[uint8] -> uint8)
        if (typeStr.StartsWith("const[") && typeStr.EndsWith("]"))
        {
            string inner = typeStr.Substring(6, typeStr.Length - 7);
            return StringToDataType(inner);
        }

        if (typeStr == "void" || typeStr == "None") return DataType.VOID;

        // For pointer/register types, extract the inner element type (e.g. ptr[uint8] -> UINT8)
        if (typeStr.StartsWith("ptr[") && typeStr.EndsWith("]"))
        {
            string inner = typeStr.Substring(4, typeStr.Length - 5);
            return StringToDataType(inner);
        }
        // Bare ptr (no inner type) or PIORegister — address-level default
        if (typeStr == "ptr" || typeStr.Contains("PIORegister"))
            return DataType.UINT16;

        return DataType.UNKNOWN;
    }

    /// Returns the promoted type when combining two operand types.
    public static DataType GetPromotedType(DataType a, DataType b)
    {
        if (a == b) return a;

        int sizeA = a.SizeOf();
        int sizeB = b.SizeOf();

        // Promote to the larger type
        if (sizeA > sizeB) return a;
        if (sizeB > sizeA) return b;

        // Same size, differing signedness — promote to signed variant of next size
        bool aSigned = a.IsSigned();
        bool bSigned = b.IsSigned();

        if (aSigned != bSigned)
        {
            switch (sizeA)
            {
                case 1:
                    return DataType.INT16;
                case 2:
                    return DataType.INT32;
                default:
                    return DataType.INT32; // Cap at 32-bit
            }
        }

        // Same size, same signedness — prefer 'a'
        return a;
    }
}