/*
 * -----------------------------------------------------------------------------
 * Whipsnake Compiler (whipc)
 * Copyright (C) 2026 Ivan Montiel Cardona and the Whipsnake Project Authors
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

//
// Created by Ivan Montiel Cardona on 09/02/26.
//

#ifndef DEVICE_CONFIG_H
#define DEVICE_CONFIG_H
#include <map>

struct DeviceConfig {
  std::string chip;
  std::string target_chip;    // Source of Truth (CLI/TOML)
  std::string detected_chip;  // From source code (device_info)
  std::string arch;
  unsigned long frequency;
  int ram_size = 0;
  int flash_size = 0;
  int eeprom_size = 0;
  std::map<std::string, std::string> fuses;
  int reset_vector = -1;
  int interrupt_vector = -1;
  int interrupt_vector_high = -1;
  int interrupt_vector_low = -1;
};

#endif  // DEVICE_CONFIG_H
