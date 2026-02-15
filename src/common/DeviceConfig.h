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
};

#endif  // DEVICE_CONFIG_H
