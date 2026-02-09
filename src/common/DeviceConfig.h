//
// Created by Ivan Montiel Cardona on 09/02/26.
//

#ifndef DEVICE_CONFIG_H
#define DEVICE_CONFIG_H
#include <map>

struct DeviceConfig {
    unsigned long frequency;
    std::map<std::string, std::string> fuses;
};

#endif //DEVICE_CONFIG_H