/*
 * -----------------------------------------------------------------------------
 * PyMCU Compiler (pymcuc)
 * Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
 *
 * SPDX-License-Identifier: MIT
 * -----------------------------------------------------------------------------
 */

namespace PyMCU.Frontend.Pio;

// Result of assembling an @asm_pio / @rp2.asm_pio function body: the 16-bit PIO
// machine-code words plus the state-machine configuration derived from the
// decorator keyword arguments. Consumed by the IR generator, which materialises
// the words as compile-time constants and feeds the config into the StateMachine
// HAL construction.
public sealed class AssembledPioProgram
{
    public required ushort[] Words { get; init; }
    public required PioConfig Config { get; init; }
    // wrap_target() / wrap() instruction indices. Defaults: target=0, wrap=last.
    public int WrapTarget { get; init; }
    public int Wrap { get; init; }
}

public enum PioShiftDir { Left = 0, Right = 1 }
public enum PioFifoJoin { None = 0, Tx = 1, Rx = 2 }

// State-machine configuration distilled from @asm_pio(...) keyword arguments.
// Pin *bases* are NOT here -- they are supplied at StateMachine construction.
public sealed class PioConfig
{
    public int SideSetCount { get; set; }            // bits consumed by .side()
    public bool SideSetOpt { get; set; }             // optional side-set (top enable bit)
    public bool SideSetPinDir { get; set; }          // side-set drives pindirs, not pins

    public int OutInitCount { get; set; }            // # of OUT pins to init as output
    public int SetInitCount { get; set; }            // # of SET pins to init as output
    public int SideSetInitCount { get; set; }        // # of side-set pins to init as output

    public PioShiftDir InShiftDir { get; set; } = PioShiftDir.Left;
    public PioShiftDir OutShiftDir { get; set; } = PioShiftDir.Left;
    public bool AutoPush { get; set; }
    public bool AutoPull { get; set; }
    public int PushThreshold { get; set; } = 32;     // 1..32 (32 encoded as 0)
    public int PullThreshold { get; set; } = 32;
    public PioFifoJoin FifoJoin { get; set; } = PioFifoJoin.None;
}

// Raised by the PIO assembler on any malformed program. The IR layer converts
// this into a located CompileError diagnostic.
public sealed class PioAsmException(string message) : System.Exception(message);

// Encoding tables for the nine PIO instructions (RP2040/RP2350 PIO ISA).
internal static class PioEnc
{
    // Opcode base (bits [15:13]).
    public const ushort JMP  = 0x0000;
    public const ushort WAIT = 0x2000;
    public const ushort IN   = 0x4000;
    public const ushort OUT  = 0x6000;
    public const ushort PUSH = 0x8000;   // bit7 = 0
    public const ushort PULL = 0x8080;   // bit7 = 1
    public const ushort MOV  = 0xA000;
    public const ushort IRQ  = 0xC000;
    public const ushort SET  = 0xE000;

    // JMP conditions (bits [7:5]).
    public static int JmpCond(string name) => name switch
    {
        "not_x"    => 1,
        "x_dec"    => 2,
        "not_y"    => 3,
        "y_dec"    => 4,
        "x_not_y"  => 5,
        "pin"      => 6,
        "not_osre" => 7,
        _ => throw new PioAsmException($"unknown jmp condition '{name}'"),
    };

    // WAIT source (bits [6:5]).
    public static int WaitSrc(string name) => name switch
    {
        "gpio" => 0,
        "pin"  => 1,
        "irq"  => 2,
        _ => throw new PioAsmException($"unknown wait source '{name}'"),
    };

    // IN source (bits [7:5]).
    public static int InSrc(string name) => name switch
    {
        "pins" => 0,
        "x"    => 1,
        "y"    => 2,
        "null" => 3,
        "isr"  => 6,
        "osr"  => 7,
        _ => throw new PioAsmException($"invalid 'in' source '{name}'"),
    };

    // OUT destination (bits [7:5]).
    public static int OutDest(string name) => name switch
    {
        "pins"    => 0,
        "x"       => 1,
        "y"       => 2,
        "null"    => 3,
        "pindirs" => 4,
        "pc"      => 5,
        "isr"     => 6,
        "exec"    => 7,
        _ => throw new PioAsmException($"invalid 'out' destination '{name}'"),
    };

    // SET destination (bits [7:5]); only pins/x/y/pindirs are valid.
    public static int SetDest(string name) => name switch
    {
        "pins"    => 0,
        "x"       => 1,
        "y"       => 2,
        "pindirs" => 4,
        _ => throw new PioAsmException($"invalid 'set' destination '{name}'"),
    };

    // MOV destination (bits [7:5]).
    public static int MovDest(string name) => name switch
    {
        "pins" => 0,
        "x"    => 1,
        "y"    => 2,
        "exec" => 4,
        "pc"   => 5,
        "isr"  => 6,
        "osr"  => 7,
        _ => throw new PioAsmException($"invalid 'mov' destination '{name}'"),
    };

    // MOV source (bits [2:0]).
    public static int MovSrc(string name) => name switch
    {
        "pins"   => 0,
        "x"      => 1,
        "y"      => 2,
        "null"   => 3,
        "status" => 5,
        "isr"    => 6,
        "osr"    => 7,
        _ => throw new PioAsmException($"invalid 'mov' source '{name}'"),
    };

    // MOV operation (bits [4:3]): none / invert (~) / bit-reverse.
    public const int MovOpNone    = 0;
    public const int MovOpInvert  = 1;
    public const int MovOpReverse = 2;
}
