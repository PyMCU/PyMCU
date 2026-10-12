/*
 * -----------------------------------------------------------------------------
 * PyMCU RISC-V backend: the garbage-collected heap behind list[T] and friends
 * Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
 *
 * SPDX-License-Identifier: MIT
 * -----------------------------------------------------------------------------
 *
 * The same design as the ARM backend's runtime: a bump allocator over
 * [__heap_start, __heap_end), a shadow stack of the addresses of the GC_REF variable slots that
 * are live (GcRoot pushes one, GcUnroot pops it), and a collection that marks from the shadow
 * stack, marks what reference-bearing objects reach, and slides the live objects down the heap.
 *
 * Object: a 4-byte header, then the payload; user_ptr (what a GC_REF holds) is header + 4.
 *   byte 0     bit 7 = reached (set by mark, cleared by compaction), bit 6 = payload holds refs
 *   byte 1     0
 *   bytes 2-3  payload size, little endian
 * An object takes (size + 4 + 3) & ~3 bytes. A reference-bearing payload is a list[list[T]]:
 * byte 0 is the count and the refs are 4-byte words from byte 2 on, so they are NOT aligned and
 * are read and written a byte at a time.
 *
 * Unlike the ARM runtime, a move rewrites the refs held INSIDE reference-bearing objects as well
 * as the ones on the shadow stack, so an inner list survives its outer list's neighbours moving.
 *
 * All the state is three words the backend reserves (__pymcu_gc_state): the heap top, the shadow
 * stack depth and a flag. Everything goes through M8/M16/M32 so the host test can run this file
 * over a simulated 32-bit address space.
 */
typedef unsigned int u32;
typedef unsigned short u16;
typedef unsigned char u8;

#ifndef GC_HOST
extern u8 __heap_start[], __heap_end[];
extern u32 __pymcu_gc_state[];
extern u32 __pymcu_gc_ss_base[];
extern const u32 __pymcu_gc_global_roots[];
extern const u32 __pymcu_gc_global_count;
#define HEAP_START ((u32)__heap_start)
#define HEAP_END ((u32)__heap_end)
#define STATE ((u32)__pymcu_gc_state)
#define SS_BASE ((u32)__pymcu_gc_ss_base)
#define GLOBAL_ROOT(i) (__pymcu_gc_global_roots[i])
#define GLOBAL_COUNT (__pymcu_gc_global_count)
#define M8(a) (*(volatile u8 *)(a))
#define M16(a) (*(volatile u16 *)(a))
#define M32(a) (*(volatile u32 *)(a))
#endif

#define TOP M32(STATE)
#define SS_TOP M32(STATE + 4)
#define CHANGED M32(STATE + 8)
#define LIVE 0x80
#define REFS 0x40

static u32 __pymcu_gc_total_of(u32 hdr) { return ((u32)M16(hdr + 2) + 7) & ~3u; }

/* A word that may sit on any byte boundary. */
static u32 __pymcu_gc_rd32u(u32 a)
{
    return (u32)M8(a) | ((u32)M8(a + 1) << 8) | ((u32)M8(a + 2) << 16) | ((u32)M8(a + 3) << 24);
}

static void __pymcu_gc_wr32u(u32 a, u32 v)
{
    M8(a) = (u8)v;
    M8(a + 1) = (u8)(v >> 8);
    M8(a + 2) = (u8)(v >> 16);
    M8(a + 3) = (u8)(v >> 24);
}

void __pymcu_gc_root_push(u32 addr)
{
    M32(SS_BASE + SS_TOP * 4) = addr;
    SS_TOP = SS_TOP + 1;
}

void __pymcu_gc_root_pop(void)
{
    SS_TOP = SS_TOP - 1;
}

static int __pymcu_gc_in_heap(u32 v) { return v != 0 && v >= HEAP_START && v < TOP; }

/* Mark every object a shadow-stack slot refers to. */
static void __pymcu_gc_mark(void)
{
    u32 i;
    for (i = 0; i < SS_TOP; i++) {
        u32 v = M32(M32(SS_BASE + i * 4));
        if (__pymcu_gc_in_heap(v)) M8(v - 4) = M8(v - 4) | LIVE;
    }
}

/* Mark what the refs inside live reference-bearing objects reach, until nothing new is reached
 * (a list three deep needs one pass per level). */
static void __pymcu_gc_trace(void)
{
    do {
        u32 hdr = HEAP_START;
        CHANGED = 0;
        while (hdr < TOP) {
            u8 flags = M8(hdr);
            u32 total = __pymcu_gc_total_of(hdr);
            if ((flags & LIVE) && (flags & REFS)) {
                u32 user = hdr + 4, count = M8(user), k;
                for (k = 0; k < count; k++) {
                    u32 v = __pymcu_gc_rd32u(user + 2 + 4 * k);
                    if (__pymcu_gc_in_heap(v) && !(M8(v - 4) & LIVE)) {
                        M8(v - 4) = M8(v - 4) | LIVE;
                        CHANGED = 1;
                    }
                }
            }
            hdr += total;
        }
    } while (CHANGED);
}

/* Rewrite every reference to `old` as `nw`: the shadow-stack slots, and the refs inside
 * reference-bearing objects in [lo1, hi1) and [lo2, hi2) (the parts of the heap that hold
 * objects at the moment of the call). `liveonly` skips objects the mark did not reach. */
static void __pymcu_gc_fixup_in(u32 old, u32 nw, u32 lo1, u32 hi1, u32 lo2, u32 hi2, int liveonly)
{
    u32 i, part;
    for (i = 0; i < SS_TOP; i++) {
        u32 slot = M32(SS_BASE + i * 4);
        if (M32(slot) == old) M32(slot) = nw;
    }
    for (part = 0; part < 2; part++) {
        u32 hdr = part ? lo2 : lo1, end = part ? hi2 : hi1;
        while (hdr < end) {
            u8 flags = M8(hdr);
            u32 total = __pymcu_gc_total_of(hdr);
            if ((flags & REFS) && (!liveonly || (flags & LIVE))) {
                u32 user = hdr + 4, count = M8(user), k;
                for (k = 0; k < count; k++)
                    if (__pymcu_gc_rd32u(user + 2 + 4 * k) == old) __pymcu_gc_wr32u(user + 2 + 4 * k, nw);
            }
            hdr += total;
        }
    }
}

/* A list that grew moved to a new allocation: everything that aliased it follows. */
void gc_list_fixup(u32 old, u32 nw)
{
    __pymcu_gc_fixup_in(old, nw, HEAP_START, TOP, 0, 0, 0);
}

/* Slide the live objects down, one linear sweep. [HEAP_START, write) holds what is already
 * compacted, [read, top) what is not; the gap between is stale and is not scanned. */
static void __pymcu_gc_compact(void)
{
    u32 read = HEAP_START, write = HEAP_START, top = TOP;
    while (read < top) {
        u32 total = __pymcu_gc_total_of(read);
        if (M8(read) & LIVE) {
            if (read != write) {
                u32 c;
                __pymcu_gc_fixup_in(read + 4, write + 4, HEAP_START, write, read, top, 1);
                for (c = 0; c < total; c += 4) M32(write + c) = M32(read + c);
            }
            M8(write) = M8(write) & ~LIVE;
            write += total;
        }
        read += total;
    }
    TOP = write;
}

static void __pymcu_gc_collect(void)
{
#ifndef GC_HOST
    u32 mstatus;
    __asm__ volatile("csrrci %0, mstatus, 8" : "=r"(mstatus));   /* interrupts off */
#endif
    __pymcu_gc_mark();
    __pymcu_gc_trace();
    __pymcu_gc_compact();
#ifndef GC_HOST
    __asm__ volatile("csrw mstatus, %0" : : "r"(mstatus) : "memory");
#endif
}

static u32 __pymcu_gc_try(u32 size, u32 refs)
{
    u32 total = (size + 7) & ~3u, top = TOP;
    if (top + total > HEAP_END) return 0;
    M8(top) = refs ? REFS : 0;
    M8(top + 1) = 0;
    M16(top + 2) = (u16)size;
    TOP = top + total;
    return top + 4;
}

/* Returns the user_ptr, or 0 when the heap cannot hold the object even after a collection. */
u32 __pymcu_gc_alloc(u32 size, u32 refs)
{
    u32 r;
    if (TOP == 0) {
        u32 i;
        /* The GC_REF globals live as long as the program: seed them once, never pop. */
        for (i = 0; i < GLOBAL_COUNT; i++) __pymcu_gc_root_push(GLOBAL_ROOT(i));
        TOP = HEAP_START;
    }
    r = __pymcu_gc_try(size, refs);
    if (r) return r;
    __pymcu_gc_collect();
    return __pymcu_gc_try(size, refs);
}
