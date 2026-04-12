/*
 * -----------------------------------------------------------------------------
 * PyMCU Compiler (pymcuc)
 * Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
 *
 * SPDX-License-Identifier: MIT
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction, including without limitation the rights
 * to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
 * copies of the Software, and to permit persons to whom the Software is
 * furnished to do so, subject to the following conditions:
 *
 * The above copyright notice and this permission notice shall be included in
 * all copies or substantial portions of the Software.
 *
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 * IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 * FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 * AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 * LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
 * OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
 * THE SOFTWARE.
 * -----------------------------------------------------------------------------
 * SAFETY WARNING / HIGH RISK ACTIVITIES:
 * THE SOFTWARE IS NOT DESIGNED, MANUFACTURED, OR INTENDED FOR USE IN HAZARDOUS
 * ENVIRONMENTS REQUIRING FAIL-SAFE PERFORMANCE, SUCH AS IN THE OPERATION OF
 * NUCLEAR FACILITIES, AIRCRAFT NAVIGATION OR COMMUNICATION SYSTEMS, AIR
 * TRAFFIC CONTROL, DIRECT LIFE SUPPORT MACHINES, OR WEAPONS SYSTEMS.
 * -----------------------------------------------------------------------------
 */

using PyMCU.Backend.Targets.AVR;
using PyMCU.Backend.Targets.PIC12;
using PyMCU.Backend.Targets.PIC14;
using PyMCU.Backend.Targets.PIO;
using PyMCU.Backend.Targets.RiscV;
using PyMCU.Common;
using PyMCU.Backend.Targets.PIC18;

namespace PyMCU.Backend;

public static class CodeGenFactory
{
    public static CodeGen Create(string arch, DeviceConfig config)
    {
        if (arch == "pic12" || arch == "baseline" || arch.StartsWith("pic10f") || arch.StartsWith("pic12f"))
        {
            return new PIC12CodeGen(config);
        }
        
        if (arch == "pic14" || arch == "pic14e" || arch == "midrange" || arch.StartsWith("pic16f"))
        {
            return new PIC14CodeGen(config);
        }
        
        if (arch == "pic18" || arch == "advanced" || arch.StartsWith("pic18f"))
        {
            return new PIC18CodeGen(config);
        }
        
        if (arch == "avr" || arch == "avr8" || arch == "atmega328p")
        {
            return new AvrCodeGen(config);
        }
        
        if (arch == "riscv" || arch == "rv32ec" || arch.StartsWith("ch32v"))
        {
            return new RiscvCodeGen(config);
        }
        
        if (arch == "pio" || arch == "rp2040-pio")
        {
            return new PIOCodeGen(config);
        }

        throw new ArgumentException($"Unknown architecture: {arch}", nameof(arch));
    }
}