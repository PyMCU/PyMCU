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

using PyMCU.Common;
using PyMCU.Common.Models;

namespace PyMCU.Frontend;

// Scans the AST for configuration calls like device_info()
// This runs BEFORE type checking or code generation.
public class PreScanVisitor(DeviceConfig config)
{
    // isTargetEstablished = true  → target was set by BootstrapPhase; validate only.
    // isTargetEstablished = false → first device_info() seen establishes the target.
    public void Scan(ProgramNode program, bool isTargetEstablished = false)
    {
        foreach (var stmt in program.GlobalStatements)
        {
            VisitStatement(stmt, isTargetEstablished);
        }
    }

    private void VisitStatement(Statement stmt, bool isTargetEstablished)
    {
        if (stmt is ExprStmt { Expr: CallExpr { Callee: VariableExpr { Name: "device_info" } } call })
        {
            HandleDeviceInfo(call, isTargetEstablished);
        }
    }

    private void HandleDeviceInfo(CallExpr call, bool isTargetEstablished)
    {
        int positionalIndex = 0;

        foreach (var arg in call.Args)
        {
            string key = "";
            Expression valueExpr;

            if (arg is KeywordArgExpr kw)
            {
                key = kw.Key;
                valueExpr = kw.Value;
            }
            else
            {
                switch (positionalIndex)
                {
                    case 0: key = "arch"; break;
                    case 1: key = "chip"; break;
                    case 2: key = "ram_size"; break;
                    case 3: key = "flash_size"; break;
                    case 4: key = "eeprom_size"; break;
                    default:
                        throw new CompilerError("ConfigError", "Too many positional arguments in device_info",
                            call.Line, 0);
                }

                valueExpr = arg;
                positionalIndex++;
            }

            if (key == "chip")
            {
                if (valueExpr is StringLiteral lit)
                {
                    string parsedChip = lit.Value;

                    if (isTargetEstablished)
                    {
                        // Target is locked. Validate arch-family compatibility.
                        // Same chip family (e.g. atmega328 in an atmega328p project): warning only.
                        // Different family (e.g. pic18 chip in an avr project): fatal error.
                        if (!ArchFamilyResolver.SameFamily(config.Arch, parsedChip))
                        {
                            var importedFamily = ArchFamilyResolver.Resolve(parsedChip);
                            var targetFamily   = ArchFamilyResolver.Resolve(config.Arch);
                            throw new CompilerError("TargetMismatch",
                                $"Cross-architecture import detected: chip file declares '{parsedChip}' " +
                                $"(family={importedFamily}) but the build target is '{config.TargetChip}' " +
                                $"(family={targetFamily}). Cannot mix chip definitions from different " +
                                $"architecture families. Check your imports or your [tool.pymcu] target.",
                                call.Line, 0);
                        }

                        if (config.TargetChip != parsedChip)
                        {
                            Logger.Warning("PreScan",
                                $"Chip file declares '{parsedChip}' but build target is '{config.TargetChip}'. " +
                                $"Same architecture family — proceeding with build target.");
                        }
                        // Do NOT overwrite config.Chip or config.TargetChip.
                    }
                    else
                    {
                        // Bootstrap mode: first device_info() establishes the target.
                        config.DetectedChip = parsedChip;
                        config.Chip = parsedChip;

                        if (!string.IsNullOrEmpty(config.TargetChip) && config.TargetChip != parsedChip)
                        {
                            Logger.Warning("PreScan",
                                $"Target '{config.TargetChip}' (from build config) differs from chip file '{parsedChip}'. " +
                                $"Using build target.");
                        }
                    }
                }
                else
                {
                    throw new CompilerError("ConfigError", "chip must be a string literal", call.Line, 0);
                }
            }
            else if (key == "arch")
            {
                if (valueExpr is StringLiteral lit)
                {
                    if (!isTargetEstablished)
                        config.Arch = lit.Value;
                    // When established: arch= in a non-bootstrap chip file is ignored;
                    // family validation already happened via the chip= key above.
                }
                else
                {
                    throw new CompilerError("ConfigError", "arch must be a string literal", call.Line, 0);
                }
            }
            else if (key == "ram_size")
            {
                if (valueExpr is IntegerLiteral lit && !isTargetEstablished)
                    config.RamSize = lit.Value;
            }
            else if (key == "flash_size")
            {
                if (valueExpr is IntegerLiteral lit && !isTargetEstablished)
                    config.FlashSize = lit.Value;
            }
            else if (key == "eeprom_size")
            {
                if (valueExpr is IntegerLiteral lit && !isTargetEstablished)
                    config.EepromSize = lit.Value;
            }
        }

        Logger.Verbose("PreScan",
            $"Configured device: {config.Chip} (Arch: {config.Arch}) (RAM: {config.RamSize}, Flash: {config.FlashSize})");
    }
}