; =============================================================================
; PyMCU GC Runtime  —  Mark-and-Compact with Shadow Stack (AVR, ATmega328P)
; =============================================================================
;
; SRAM layout (.equ constants set by AvrCodeGen when program.NeedsGc = true):
;
;   _gc_ss_base      Shadow-stack data: 64 slots x 2 bytes = 128 bytes.
;                    Slot i holds the absolute SRAM address of a local that
;                    currently contains a live GC_REF value.
;   _gc_ss_top_addr  1-byte count of active shadow-stack entries.
;   _gc_heap_top_lo  lo byte of the bump-allocator write pointer.
;   _gc_heap_top_hi  hi byte of the bump-allocator write pointer.
;   _heap_start      First byte of GC-managed heap (constant after gc_init).
;   _heap_end        Exclusive end of usable heap.
;
; Object layout (header immediately BEFORE the user pointer):
;   +0  mark byte  : bit7 = GC mark flag; bits[6:0] spare
;   +1  size byte  : payload length 0-255 bytes
;   +2..N+1  payload
;
; user_ptr = &header + 2   (value stored in a GC_REF variable)
;
; Public API:
;   gc_init    — initialise GC state; call once from main prologue
;   gc_alloc   — R24:R25 = payload size; returns user_ptr in R24:R25
;                (0x0000 on permanent OOM after one collection attempt)
;   gc_collect — mark + compact (also called internally by gc_alloc on OOM)
;
; Calling convention notes:
;   _gc_update_ss uses R16-R21, R26-R27 only; R22-R25 survive the call.
;   Callers of _gc_update_ss must PUSH/POP any of R16-R21 they care about.
; =============================================================================

; ---------------------------------------------------------------------------
; gc_init  --  initialise GC state once from main.
; ---------------------------------------------------------------------------
gc_init:
    CLR     R16
    STS     _gc_ss_top_addr, R16        ; shadow-stack depth = 0

    LDI     R16, lo8(_heap_start)
    LDI     R17, hi8(_heap_start)
    STS     _gc_heap_top_lo, R16
    STS     _gc_heap_top_hi, R17        ; heap_top = _heap_start
    RET

; ---------------------------------------------------------------------------
; gc_alloc  --  bump-allocate `size` (R24:R25) bytes + 2-byte header.
; Returns user_ptr in R24:R25 (0x0000 = permanent OOM).
; ---------------------------------------------------------------------------
gc_alloc:
    ; Copy size into R22:R23 so we can restore it if gc_collect clobbers R24:R25.
    ; _gc_alloc_inner does NOT clobber R22:R23, so the first call is safe.
    ; gc_collect (via _gc_do_compact) DOES clobber R22, so we PUSH before it.
    MOVW    R22, R24                    ; R23:R22 = size copy

    CALL    _gc_alloc_inner             ; attempt 1; R24:R25 = result

    MOV     R16, R24
    OR      R16, R25
    BRNE    _galloc_done                ; success

    ; OOM: save size (R22 clobbered by gc_collect internals)
    PUSH    R22
    PUSH    R23
    CALL    gc_collect
    POP     R23
    POP     R22

    MOVW    R24, R22                    ; pass size for attempt 2
    CALL    _gc_alloc_inner             ; attempt 2

_galloc_done:
    RET

; _gc_alloc_inner: attempt bump allocation.
; In:  R24:R25 = payload size.
; Out: R24:R25 = user_ptr (0x0000 on failure).
; Clobbers: R16, R17, R18, R19, R26, R27, R30, R31
_gc_alloc_inner:
    MOVW    R18, R24                    ; R19:R18 = payload size

    ; total = payload_size + 2 (header bytes)
    MOV     R16, R18
    MOV     R17, R19
    SUBI    R16, lo8(-2)                ; R16 += 2
    SBCI    R17, hi8(-2)                ; R17:R16 = total

    ; Load heap_top into Z
    LDS     R30, _gc_heap_top_lo
    LDS     R31, _gc_heap_top_hi

    ; new_top = heap_top + total
    MOVW    R26, R30                    ; R27:R26 = heap_top
    ADD     R26, R16
    ADC     R27, R17                    ; R27:R26 = new_top

    ; Check new_top <= _heap_end
    LDI     R16, lo8(_heap_end)
    LDI     R17, hi8(_heap_end)
    CP      R26, R16
    CPC     R27, R17
    BRSH    _gai_fail                   ; new_top >= _heap_end: fail

    ; Write header at Z (current heap_top)
    CLR     R16
    ST      Z+, R16                     ; mark byte = 0
    ST      Z+, R18                     ; size byte = payload lo byte

    ; Commit: update heap_top
    STS     _gc_heap_top_lo, R26
    STS     _gc_heap_top_hi, R27

    ; Return user_ptr = Z (points to first payload byte, right after header)
    MOVW    R24, R30
    RET

_gai_fail:
    CLR     R24
    CLR     R25
    RET

; ---------------------------------------------------------------------------
; gc_collect  --  run a full mark-then-compact GC cycle.
; ---------------------------------------------------------------------------
gc_collect:
    CALL    _gc_do_mark
    CALL    _gc_do_compact
    RET

; ---------------------------------------------------------------------------
; _gc_do_mark  --  walk shadow stack; set bit7 in each reachable object header.
; Clobbers: R16, R17, R18, R19, R20, R21, R26, R27
; ---------------------------------------------------------------------------
_gc_do_mark:
    LDS     R16, _gc_ss_top_addr        ; R16 = number of shadow-stack entries
    TST     R16
    BREQ    _gdm_done

    CLR     R17                         ; R17 = loop index i = 0

_gdm_loop:
    CP      R17, R16
    BRGE    _gdm_done

    ; --- Compute X = _gc_ss_base + i*2 ---
    MOV     R18, R17
    LSL     R18                         ; R18 = i*2  (R17 unchanged)
    LDI     R26, lo8(_gc_ss_base)
    LDI     R27, hi8(_gc_ss_base)
    CLR     R19
    ADD     R26, R18
    ADC     R27, R19                    ; X = _gc_ss_base + i*2

    ; --- Load 2-byte var_address from slot ---
    LD      R20, X+                     ; var_addr lo
    LD      R21, X                      ; var_addr hi

    ; --- Load 2-byte GC_REF value from var_address ---
    MOVW    R26, R20                    ; X = var_address
    LD      R18, X+                     ; gc_ref lo
    LD      R19, X                      ; gc_ref hi   (R19:R18 = GC_REF value)

    ; --- Skip if null ---
    MOV     R20, R18
    OR      R20, R19
    BREQ    _gdm_next

    ; --- Skip if gc_ref < _heap_start ---
    LDI     R20, lo8(_heap_start)
    LDI     R21, hi8(_heap_start)
    CP      R18, R20
    CPC     R19, R21
    BRLO    _gdm_next

    ; --- Skip if gc_ref >= heap_top ---
    LDS     R20, _gc_heap_top_lo
    LDS     R21, _gc_heap_top_hi
    CP      R18, R20
    CPC     R19, R21
    BRGE    _gdm_next

    ; --- Mark: header at user_ptr - 2 ---
    MOVW    R26, R18
    SUBI    R26, 2
    SBCI    R27, 0                      ; X = user_ptr - 2 = header address
    LD      R20, X
    ORI     R20, 0x80
    ST      X, R20                      ; set mark bit

_gdm_next:
    INC     R17
    RJMP    _gdm_loop

_gdm_done:
    RET

; ---------------------------------------------------------------------------
; _gc_do_compact  --  compact live objects toward _heap_start.
;
; Y = read cursor (src), starts at _heap_start
; Z = write cursor (dst), starts at _heap_start
; R17:R16 = heap_top limit
;
; For each live (marked) object at Y:
;   - Compute old_user_ptr = Y+2, new_user_ptr = Z+2
;   - If moved, update shadow-stack pointers via _gc_update_ss
;   - Copy header+payload from Y to Z (clearing mark bit)
;   - Advance both Y and Z by total_size
; For each dead object at Y:
;   - Advance Y by total_size; Z stays
;
; After the loop: _gc_heap_top = Z
;
; _gc_update_ss(old=R23:R22, new=R25:R24) clobbers R16-R21, R26-R27.
; We save R16 (heap_top lo), R17 (heap_top hi), and the size byte (R19)
; using PUSH/POP before/after the call.
; ---------------------------------------------------------------------------
_gc_do_compact:
    LDI     R28, lo8(_heap_start)
    LDI     R29, hi8(_heap_start)       ; Y = src = _heap_start
    LDI     R30, lo8(_heap_start)
    LDI     R31, hi8(_heap_start)       ; Z = dst = _heap_start

    LDS     R16, _gc_heap_top_lo
    LDS     R17, _gc_heap_top_hi        ; R17:R16 = scan limit

_gdc_loop:
    CP      R28, R16
    CPC     R29, R17
    BRGE    _gdc_done                   ; Y >= heap_top: finished

    ; --- Load object header at Y ---
    LD      R18, Y                      ; mark byte
    LDD     R19, Y+1                    ; size byte

    ; total_size = size + 2  (R20 = lo, R21 = hi=0 since max total = 257)
    MOV     R20, R19
    SUBI    R20, lo8(-2)                ; R20 = size + 2
    CLR     R21

    ; --- Is object live? ---
    SBRS    R18, 7                      ; bit7 set = live
    RJMP    _gdc_skip                   ; dead: skip

    ; --- Live object ---
    ; old_user_ptr = Y+2 (R23:R22)
    MOVW    R22, R28
    SUBI    R22, lo8(-2)
    SBCI    R23, hi8(-2)

    ; new_user_ptr = Z+2 (R25:R24)
    MOVW    R24, R30
    SUBI    R24, lo8(-2)
    SBCI    R25, hi8(-2)

    ; Update shadow stack if object moved
    CP      R22, R24
    CPC     R23, R25
    BREQ    _gdc_copy                   ; same address, no fixup

    PUSH    R16                         ; save heap_top lo
    PUSH    R17                         ; save heap_top hi
    PUSH    R19                         ; save size byte
    CALL    _gc_update_ss               ; (R23:R22=old, R25:R24=new) → no retval
    POP     R19                         ; restore size byte
    POP     R17
    POP     R16                         ; restore heap_top

    ; Recompute total_size (R19 survived PUSH/POP)
    MOV     R20, R19
    SUBI    R20, lo8(-2)
    CLR     R21

_gdc_copy:
    ; Copy total_size bytes from Y to Z, clearing the mark bit in the header.
    ; Use X as src pointer (= Y), Z is already the dst.
    MOVW    R26, R28                    ; X = Y (src)
    MOV     R22, R20                    ; R22 = byte counter (total_size)

    ; Header: write mark=0, then size byte
    LD      R23, X+                     ; consume src mark byte
    CLR     R23
    ST      Z+, R23                     ; write mark byte = 0
    LD      R23, X+                     ; consume + copy size byte
    ST      Z+, R23
    SUBI    R22, 2                      ; subtract 2 header bytes from counter

    ; Copy payload
_gdc_payload:
    TST     R22
    BREQ    _gdc_adv_src
    LD      R23, X+
    ST      Z+, R23
    DEC     R22
    RJMP    _gdc_payload

_gdc_adv_src:
    ADD     R28, R20
    ADC     R29, R21                    ; Y += total_size
    RJMP    _gdc_loop

_gdc_skip:
    ; Dead: advance Y only
    ADD     R28, R20
    ADC     R29, R21
    RJMP    _gdc_loop

_gdc_done:
    STS     _gc_heap_top_lo, R30
    STS     _gc_heap_top_hi, R31        ; reset heap_top = Z
    RET

; ---------------------------------------------------------------------------
; _gc_update_ss  --  update shadow-stack variable slots holding old_user_ptr.
; In:  R23:R22 = old_user_ptr (hi:lo)   — NOT clobbered
;      R25:R24 = new_user_ptr (hi:lo)   — NOT clobbered
; Clobbers: R16, R17, R18, R19, R20, R21, R26, R27
; ---------------------------------------------------------------------------
_gc_update_ss:
    LDS     R16, _gc_ss_top_addr        ; R16 = depth
    TST     R16
    BREQ    _guss_done

    CLR     R17                         ; R17 = i = 0

_guss_loop:
    CP      R17, R16
    BRGE    _guss_done

    ; X = _gc_ss_base + i*2
    MOV     R18, R17                    ; R18 = i (R17 preserved)
    LSL     R18                         ; R18 = i*2
    LDI     R26, lo8(_gc_ss_base)
    LDI     R27, hi8(_gc_ss_base)
    CLR     R19
    ADD     R26, R18
    ADC     R27, R19                    ; X = ss_base + i*2

    ; Load var_address from slot (2 bytes)
    LD      R20, X+                     ; var_addr lo
    LD      R21, X                      ; var_addr hi  (R21:R20 = var_address)

    ; Load GC_REF value from var_address
    MOVW    R26, R20                    ; X = var_address
    LD      R18, X+                     ; gc_ref lo
    LD      R19, X                      ; gc_ref hi

    ; Compare with old_user_ptr (R23:R22 = hi:lo → compare lo first then hi)
    CP      R18, R22
    CPC     R19, R23
    BRNE    _guss_next                  ; no match

    ; Match: write new_user_ptr into the variable at var_address (R21:R20)
    MOVW    R26, R20                    ; X = var_address
    ST      X+, R24                     ; write new_user_ptr lo
    ST      X,  R25                     ; write new_user_ptr hi

_guss_next:
    INC     R17
    RJMP    _guss_loop

_guss_done:
    RET
