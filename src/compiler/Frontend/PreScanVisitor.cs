/*
 * -----------------------------------------------------------------------------
 * PyMCU Compiler (pymcuc)
 * Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
 *
 * SPDX-License-Identifier: MIT
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

namespace PyMCU.Frontend;

// Scans the AST for configuration calls like device_info()
// This runs BEFORE type checking or code generation.
public class PreScanVisitor
{
    private readonly DeviceConfig config;

    public PreScanVisitor(DeviceConfig config)
    {
        this.config = config;
    }

    public void Scan(ProgramNode program)
    {
        foreach (var stmt in program.GlobalStatements)
        {
            VisitStatement(stmt);
        }
    }

    private void VisitStatement(Statement stmt)
    {
        if (stmt is ExprStmt exprStmt)
        {
            if (exprStmt.Expr is CallExpr call)
            {
                if (call.Callee is VariableExpr varExpr)
                {
                    if (varExpr.Name == "device_info")
                    {
                        HandleDeviceInfo(call);
                    }
                }
            }
        }
    }

    private void HandleDeviceInfo(CallExpr call)
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
                    case 0:
                        key = "arch";
                        break;
                    case 1:
                        key = "chip";
                        break;
                    case 2:
                        key = "ram_size";
                        break;
                    case 3:
                        key = "flash_size";
                        break;
                    case 4:
                        key = "eeprom_size";
                        break;
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
                    config.DetectedChip = parsedChip;
                    config.Chip = parsedChip;

                    // Validation: CLI/TOML vs source code chip
                    // Source code device_info() takes precedence over CLI --arch,
                    // since the code was written for a specific chip.
                    if (!string.IsNullOrEmpty(config.TargetChip) && config.TargetChip != parsedChip)
                    {
                        Console.Error.WriteLine(
                            $"[Warning] Build specifies '{config.TargetChip}', but code imports '{parsedChip}'. Using source code chip.");
                        config.TargetChip = parsedChip;
                    }

                    // Auto-detect architecture if not already set
                    if (string.IsNullOrEmpty(config.Arch))
                    {
                        if (parsedChip.StartsWith("pic16f1") && parsedChip.Length >= 9)
                        {
                            config.Arch = "pic14e";
                        }
                        else
                        {
                            config.Arch = "pic14";
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
                    config.Arch = lit.Value;
                }
                else
                {
                    throw new CompilerError("ConfigError", "arch must be a string literal", call.Line, 0);
                }
            }
            else if (key == "ram_size")
            {
                if (valueExpr is IntegerLiteral lit)
                {
                    config.RamSize = lit.Value;
                }
            }
            else if (key == "flash_size")
            {
                if (valueExpr is IntegerLiteral lit)
                {
                    config.FlashSize = lit.Value;
                }
            }
            else if (key == "eeprom_size")
            {
                if (valueExpr is IntegerLiteral lit)
                {
                    config.EepromSize = lit.Value;
                }
            }
        }

        Console.WriteLine(
            $"[PreScan] Configured device: {config.Chip} (Arch: {config.Arch}) (RAM: {config.RamSize}, Flash: {config.FlashSize})");
    }
}