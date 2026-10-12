/* Host check of gc.c over a simulated 32-bit address space. Random plain and reference-bearing
 * objects are allocated into root slots, replacing what was there, through thousands of
 * collections. After every step each object reachable from a root must still hold what was
 * written into it and every inner reference must still lead to its object. */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

typedef unsigned int u32;
typedef unsigned short u16;
typedef unsigned char u8;

static u8 sim[1 << 16];
#define M8(a) (*(u8 *)(sim + (a)))
#define M16(a) (*(u16 *)(sim + (a)))
#define M32(a) (*(u32 *)(sim + (a)))
#define HEAP_START 0x2000u
#define HEAP_END 0x2300u
#define STATE 0x100u
#define SS_BASE 0x200u
#define GLOBAL_COUNT 2u
#define GLOBAL_ROOT(i) (0x500u + 4 * (i))
#define GC_HOST 1
#include "gc.c"

#define NROOT 12
#define SLOTS 0x400u

static unsigned long rs = 88172645463325252ull;
static u32 rnd(void) { rs ^= rs << 13; rs ^= rs >> 7; rs ^= rs << 17; return (u32)(rs >> 11); }

#define MAXOBJ 400000
struct obj { int refs; u32 size; u8 seed; int ninner; int inner[4]; };
static struct obj objs[MAXOBJ];
static int nobjs, rootid[NROOT];
static long bad;

static void check(u32 p, int id, int depth, int step)
{
    struct obj *o = &objs[id];
    if (M16(p - 2) != o->size || ((M8(p - 4) & 0x40) != 0) != (o->refs != 0)) {
        if (bad++ < 10) printf("step %d: object %d header wrong\n", step, id);
        return;
    }
    if (!o->refs) {
        for (u32 j = 0; j < o->size; j++)
            if (M8(p + j) != (u8)(o->seed + j)) { if (bad++ < 10) printf("step %d: object %d byte %u corrupt\n", step, id, j); return; }
    } else if (depth < 3) {
        for (int k = 0; k < o->ninner; k++) {
            u32 v = __pymcu_gc_rd32u(p + 2 + 4 * k);
            if (o->inner[k] < 0) { if (v && bad++ < 10) printf("step %d: object %d null inner is %u\n", step, id, v); continue; }
            if (!__pymcu_gc_in_heap(v)) { if (bad++ < 10) printf("step %d: object %d inner %d left the heap\n", step, id, k); continue; }
            check(v, o->inner[k], depth + 1, step);
        }
    }
}

int main(void)
{
    long collections = 0;
    for (int i = 0; i < NROOT; i++) { rootid[i] = -1; __pymcu_gc_root_push(SLOTS + 4 * i); }
    for (int step = 0; step < 300000 && nobjs < MAXOBJ - 2; step++) {
        int r = rnd() % NROOT, id = nobjs++;
        struct obj *o = &objs[id];
        memset(o, 0, sizeof *o);
        if (rnd() % 4 == 0) { o->refs = 1; o->ninner = 1 + rnd() % 3; o->size = 2 + 4 * o->ninner; }
        else { o->size = 1 + rnd() % 30; o->seed = (u8)rnd(); }
        u32 topbefore = M32(STATE);
        u32 p = __pymcu_gc_alloc(o->size, o->refs);
        if (topbefore && M32(STATE) < topbefore + 4 + o->size) collections++;
        if (!p) { M32(SLOTS + 4 * r) = 0; rootid[r] = -1; continue; }
        if (!o->refs) for (u32 j = 0; j < o->size; j++) M8(p + j) = (u8)(o->seed + j);
        else {
            M8(p) = (u8)o->ninner; M8(p + 1) = 0;
            for (int k = 0; k < o->ninner; k++) {
                int t = rnd() % NROOT;
                o->inner[k] = rootid[t] >= 0 && t != r ? rootid[t] : -1;
                __pymcu_gc_wr32u(p + 2 + 4 * k, o->inner[k] >= 0 ? M32(SLOTS + 4 * t) : 0);
            }
        }
        M32(SLOTS + 4 * r) = p;
        rootid[r] = id;
        for (int i = 0; i < NROOT; i++)
            if (rootid[i] >= 0) {
                if (!M32(SLOTS + 4 * i)) { if (bad++ < 10) printf("step %d: root %d lost\n", step, i); }
                else check(M32(SLOTS + 4 * i), rootid[i], 0, step);
            }
        /* an occasional list growth: move an object, as append's realloc does */
        if (rnd() % 7 == 0 && rootid[r] >= 0 && !objs[rootid[r]].refs) {
            struct obj *g = &objs[rootid[r]];
            u32 np = __pymcu_gc_alloc(g->size, 0);
            if (np) {
                u32 old = M32(SLOTS + 4 * r);   /* np survives: the collection ran before it was handed out */
                for (u32 j = 0; j < g->size; j++) M8(np + j) = M8(old + j);
                M32(SLOTS + 4 * r) = np;
                gc_list_fixup(old, np);
            }
        }
    }
    printf("%ld collections, %ld mismatches\n", collections, bad);
    return bad != 0;
}
