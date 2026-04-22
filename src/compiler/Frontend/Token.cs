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

namespace PyMCU.Frontend;

public enum TokenType
{
    // Keywords
    Def,
    Return,
    If,
    Elif,
    Else,
    While,
    For,
    In,
    Break,
    Continue,
    Pass,
    Match,
    Case,
    Import,
    From,
    As,
    True,
    False,
    None,
    Or,
    And,
    Not,
    Global,
    Class,
    Yield,
    Raise,
    With,
    Assert,
    Is,
    Lambda,
    Nonlocal,

    // Literals
    Identifier,
    Number,
    String,
    FString,
    BytesLiteral,

    // Structure & Types
    Colon,
    Walrus,
    Semicolon,
    Comma,
    Dot,
    Arrow,
    LParen,
    RParen,
    LBracket,
    RBracket,

    // Operators
    Plus,
    Minus,
    Star,
    DoubleStar,
    Slash,
    FloorDiv,
    Percent,

    // Bitwise
    Ampersand,
    Pipe,
    Caret,
    Tilde,
    LShift,
    RShift,

    // Comparison & Assignment
    Equal,
    EqualEqual,
    BangEqual,
    Less,
    LessEqual,
    Greater,
    GreaterEqual,

    // Augmented Assignment
    PlusEqual,
    MinusEqual,
    StarEqual,
    SlashEqual,
    FloorDivEqual,
    PercentEqual,
    AmpEqual,
    PipeEqual,
    CaretEqual,
    LShiftEqual,
    RShiftEqual,

    // Control
    Newline,
    Indent,
    Dedent,
    EndOfFile,
    At,
    Unknown
}

public readonly record struct Token(TokenType Type, string Value, int Line, int Column);