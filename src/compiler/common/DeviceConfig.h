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
