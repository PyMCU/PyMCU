# -----------------------------------------------------------------------------
# PyMCU collections -- fixed-capacity containers (no heap, no GC).
# SPDX-License-Identifier: MIT
# -----------------------------------------------------------------------------
from pymcu.types import uint8, uint16, inline


class FixedDict:
    # Fixed-capacity integer dictionary: open addressing with linear probing over
    # fixed arrays sized at construction (a compile-time constant) -- no heap, no
    # GC, deterministic. Keys and values are uint16 (uint8 widens transparently).
    # Python semantics where they fit a fixed footprint: d[k] raises KeyError on a
    # missing key, `k in d`, len(d), get(k, default), pop(k), clear(). Inserting
    # into a FULL dict raises ValueError (a fixed dict cannot grow).
    def __init__(self, capacity: uint8):
        self._cap: uint8 = capacity
        self._keys: uint16[capacity] = [0] * capacity
        self._vals: uint16[capacity] = [0] * capacity
        # Slot states: 0 = empty, 1 = used, 2 = tombstone (deleted).
        self._state: uint8[capacity] = [0] * capacity
        self._count: uint8 = 0

    @inline
    def __len__(self) -> uint8:
        return self._count

    @inline
    def __setitem__(self, key: uint16, value: uint16):
        i: uint8 = uint8(key) % self._cap
        n: uint8 = 0
        free: uint8 = 255
        while n < self._cap:
            if self._state[i] == 0:
                break
            if self._state[i] == 1 and self._keys[i] == key:
                self._vals[i] = value
                return
            if self._state[i] == 2 and free == 255:
                free = i
            i = (i + 1) % self._cap
            n = n + 1
        if free != 255:
            i = free
        elif n >= self._cap:
            raise ValueError
        self._keys[i] = key
        self._vals[i] = value
        self._state[i] = 1
        self._count = self._count + 1

    @inline
    def __getitem__(self, key: uint16) -> uint16:
        i: uint8 = uint8(key) % self._cap
        n: uint8 = 0
        while n < self._cap:
            if self._state[i] == 0:
                raise KeyError
            if self._state[i] == 1 and self._keys[i] == key:
                return self._vals[i]
            i = (i + 1) % self._cap
            n = n + 1
        raise KeyError

    @inline
    def __contains__(self, key: uint16) -> uint8:
        i: uint8 = uint8(key) % self._cap
        n: uint8 = 0
        while n < self._cap:
            if self._state[i] == 0:
                return 0
            if self._state[i] == 1 and self._keys[i] == key:
                return 1
            i = (i + 1) % self._cap
            n = n + 1
        return 0

    @inline
    def get(self, key: uint16, default: uint16 = 0) -> uint16:
        i: uint8 = uint8(key) % self._cap
        n: uint8 = 0
        while n < self._cap:
            if self._state[i] == 0:
                return default
            if self._state[i] == 1 and self._keys[i] == key:
                return self._vals[i]
            i = (i + 1) % self._cap
            n = n + 1
        return default

    @inline
    def pop(self, key: uint16) -> uint16:
        i: uint8 = uint8(key) % self._cap
        n: uint8 = 0
        while n < self._cap:
            if self._state[i] == 0:
                raise KeyError
            if self._state[i] == 1 and self._keys[i] == key:
                self._state[i] = 2
                self._count = self._count - 1
                return self._vals[i]
            i = (i + 1) % self._cap
            n = n + 1
        raise KeyError

    @inline
    def clear(self):
        j: uint8 = 0
        while j < self._cap:
            self._state[j] = 0
            j = j + 1
        self._count = 0
