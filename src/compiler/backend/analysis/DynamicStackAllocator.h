#ifndef DYNAMIC_STACK_ALLOCATOR_H
#define DYNAMIC_STACK_ALLOCATOR_H

#include <map>
#include <string>
#include <utility>

#include "ir/Tacky.h"

class DynamicStackAllocator {
 public:
  DynamicStackAllocator(int word_size = 4, int reserved_top = 8)
      : word_size(word_size), reserved_top(reserved_top) {}

  std::pair<std::map<std::string, int>, int> allocate(
      const tacky::Function &func);

 private:
  int word_size;
  int reserved_top;
};

#endif  // DYNAMIC_STACK_ALLOCATOR_H