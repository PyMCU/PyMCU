# RTOS kernel for ATmega328P.
#
# FreeRTOS-style API:
#   add_task(fn, priority)  -- register a task (call before start_scheduler)
#   start_scheduler()       -- configure timers and launch the preemptive scheduler
#
# Supports up to 4 tasks. The scheduler uses a weighted round-robin model:
# priorities determine how many slots each task gets in an 8-entry schedule
# table that is built at startup:
#
#   priority 3 -> 4 slots (50 % of CPU time, task runs every tick)
#   priority 2 -> 2 slots (25 %)
#   priority 1 -> 1 slot  (12.5 %)
#   priority 0 -> 1 slot  (12.5 %)
#
# Higher priority = more 1 ms ticks per 8 ms cycle.  A task that calls
# delay_ms(50) with priority 1 effectively waits ~400 ms wall-clock because
# it only runs 1 out of every 8 ticks -- that is exactly what priorities do.
from pymcu.types import uint8, uint16, Callable, asm, naked, interrupt, Enum
from pymcu.hal.timer import Timer

# CPU time budgets: each priority maps to a number of slots in the 8-entry
# schedule table that is rebuilt every cycle.
#
#   HIGH   -> 4 slots (50 %)   -- time-critical I/O and sensor reads
#   NORMAL -> 2 slots (25 %)   -- display updates and protocol handling
#   LOW    -> 1 slot  (12.5 %) -- background work with bounded latency
#   IDLE   -> 1 slot  (12.5 %) -- housekeeping; may be starved by higher tasks
#
# The table pattern produced is [3,3,3,3,2,2,1,0] for one HIGH + NORMAL + LOW + IDLE task.
class Priority(Enum):
    IDLE   = 0
    LOW    = 1
    NORMAL = 2
    HIGH   = 3


# Task function pointers (2 bytes each; asm indexes as _task_fns + task_id*2).
_task_fns: Callable[4] = [0, 0, 0, 0]

# Per-task priorities (1 byte each; asm indexes as _task_prio + task_id).
_task_prio: uint8[4] = [0, 0, 0, 0]

# Scheduler counters.
_num_tasks: uint8 = 0
_cur_task:  uint8 = 0
_sched_idx: uint8 = 0

# Saved stack pointers (2 bytes per task; asm indexes as _task_sp + task_id*2).
_task_sp: uint8[8] = [0, 0, 0, 0, 0, 0, 0, 0]

# 8-slot schedule table (1 byte per slot; built by start_scheduler).
_sched: uint8[8] = [0, 0, 0, 0, 0, 0, 0, 0]

# Wall-clock millisecond counter -- incremented once per systick ISR (1 ms).
# Use delay_ms() from this module for timing that is correct under preemption.
_tick: uint16 = 0


@naked
@interrupt(0x0016)
def _systick():
    # Context switch ISR -- preemptive, fires every 1 ms via Timer1 CTC.
    #
    # AVR has no hardware exception frame (unlike ARM Cortex-M which auto-saves
    # R0-R3/LR/PC/xPSR). Every register must be saved manually. FreeRTOS calls
    # these blocks portSAVE_CONTEXT / portRESTORE_CONTEXT; here they are inlined
    # because @inline + asm() + labels would duplicate labels at each call site.
    asm("""
    ; ---- portSAVE_CONTEXT ----
    ; Push all 32 GPRs + SREG onto the interrupted task's stack.
    ; Must happen before any C/Python code touches a register.
    push r0
    in   r0,  0x3f        ; SREG -> r0 (I-flag is clear inside ISR, so no race)
    push r0
    push r1
    push r2
    push r3
    push r4
    push r5
    push r6
    push r7
    push r8
    push r9
    push r10
    push r11
    push r12
    push r13
    push r14
    push r15
    push r16
    push r17
    push r18
    push r19
    push r20
    push r21
    push r22
    push r23
    push r24
    push r25
    push r26
    push r27
    push r28
    push r29
    push r30
    push r31
    in   r28, 0x3d        ; SPL -> Y (frame pointer for current task)
    in   r29, 0x3e        ; SPH -> Y

    ; --- Save current task SP (indexed: {_task_sp} + cur_task*2) ---
    lds  r16, {_cur_task}
    mov  r17, r16
    lsl  r17
    ldi  r26, lo8({_task_sp})
    ldi  r27, hi8({_task_sp})
    add  r26, r17
    adc  r27, r1
    st   x+, r28
    st   x,  r29

    ; --- Increment wall-clock tick counter (uint16, wraps every 65.5 s) ---
    lds  r24, {_tick}
    lds  r25, {_tick} + 1
    adiw r24, 1
    sts  {_tick},     r24
    sts  {_tick} + 1, r25

    ; --- Advance schedule index (mod 8) ---
    lds  r17, {_sched_idx}
    inc  r17
    andi r17, 0x07
    sts  {_sched_idx}, r17

    ; --- Load next task from schedule table ---
    ldi  r26, lo8({_sched})
    ldi  r27, hi8({_sched})
    add  r26, r17
    adc  r27, r1
    ld   r16, x
    sts  {_cur_task}, r16

    ; --- Restore next task SP (indexed: {_task_sp} + next_task*2) ---
    lsl  r16
    ldi  r26, lo8({_task_sp})
    ldi  r27, hi8({_task_sp})
    add  r26, r16
    adc  r27, r1
    ld   r28, x+
    ld   r29, x
    out  0x3d, r28        ; SPL <- new task's saved SP
    out  0x3e, r29        ; SPH <- new task's saved SP

    ; ---- portRESTORE_CONTEXT ----
    ; Pop all 32 GPRs + SREG from the new task's stack, then return.
    pop  r31
    pop  r30
    pop  r29
    pop  r28
    pop  r27
    pop  r26
    pop  r25
    pop  r24
    pop  r23
    pop  r22
    pop  r21
    pop  r20
    pop  r19
    pop  r18
    pop  r17
    pop  r16
    pop  r15
    pop  r14
    pop  r13
    pop  r12
    pop  r11
    pop  r10
    pop  r9
    pop  r8
    pop  r7
    pop  r6
    pop  r5
    pop  r4
    pop  r3
    pop  r2
    pop  r1
    pop  r0
    out  0x3f, r0         ; restore SREG (re-enables I-flag on RETI)
    pop  r0
    reti
    """)


def timer_init():
    # Timer0: free-running, prescaler 64 -- TCNT0 rolls over as pseudo-sensor input.
    Timer(0, 64)
    # Timer1: CTC at 1 ms -- 16 MHz / 64 / (249 + 1) = 1000 Hz preemptive systick.
    t1 = Timer(1, 64)
    t1.set_compare(249)


def _build_schedule():
    # Fill the 8-slot schedule table from per-task priorities.
    # Priority -> slot count: HIGH(3)->4, NORMAL(2)->2, LOW/IDLE(1/0)->1.
    global _sched, _num_tasks, _task_prio
    slot: uint8 = 0
    task: uint8 = 0
    while task < _num_tasks:
        n: uint8 = 1
        if _task_prio[task] == Priority.HIGH:
            n = 4
        elif _task_prio[task] == Priority.NORMAL:
            n = 2
        j: uint8 = 0
        while j < n:
            if slot < 8:
                _sched[slot] = task
                slot = slot + 1
            j = j + 1
        task = task + 1


def add_task(fn: Callable, priority: uint8):
    # Register a task in the next available slot (up to 4 tasks).
    # Priority determines how many of the 8 schedule slots the task receives:
    #   HIGH(3) -> 4 slots, NORMAL(2) -> 2 slots, LOW/IDLE(1/0) -> 1 slot each.
    # The schedule table is built in start_scheduler() after all tasks are registered.
    global _task_fns, _task_prio, _task_sp, _sched, _num_tasks
    if _num_tasks >= 4:
        return
    _task_fns[_num_tasks] = fn
    _task_prio[_num_tasks] = priority
    _task_sp[_num_tasks] = 0
    _sched[_num_tasks] = 0
    _num_tasks = _num_tasks + 1


@naked
def delay_ms(ms: uint16):
    # Wall-clock delay: waits until at least `ms` systick ticks have elapsed.
    # Unlike pymcu.time.delay_ms (busy-wait counting CPU cycles), this uses the
    # _tick counter maintained by the systick ISR -- so a delay_ms(500) in a
    # LOW-priority task still takes 500 ms of wall-clock time, not 4000 ms.
    #
    # @naked avoids the compiler prolog so r24:r25 = ms at entry (AVR calling
    # convention).  Uses r18-r23 (all caller-saved) as scratch; callee-saved
    # registers are untouched.  Handles uint16 wrap at 65535 ms (~65 s).
    asm("""
    ; r24:r25 = ms at entry.  Save it in r18:r19 (caller-saved scratch).
    mov  r18, r24
    mov  r19, r25
    ; Capture start tick in r20:r21.
    lds  r20, {_tick}
    lds  r21, {_tick} + 1
_rtos_dl_lp:
    ; elapsed = _tick - start
    lds  r22, {_tick}
    lds  r23, {_tick} + 1
    sub  r22, r20           ; elapsed.lo = _tick.lo - start.lo
    sbc  r23, r21           ; elapsed.hi = _tick.hi - start.hi - borrow
    ; if elapsed < ms: loop
    cp   r22, r18
    cpc  r23, r19
    brcs _rtos_dl_lp
    ret
    """)


def tick_ms():
    # Return the current wall-clock millisecond counter.
    # The value increments once per systick ISR (every 1 ms) and wraps at
    # 65535 (~65 seconds).  Use this for non-blocking timeouts:
    #
    #   start: uint16 = tick_ms()
    #   while (tick_ms() - start) < 100:   # poll for up to 100 ms
    #       if sensor_ready():
    #           break
    return _tick


def sleep_until(target: uint16):
    # Block until tick_ms() reaches `target`.
    # Complements delay_ms() for periodic loops that must not drift:
    #
    #   deadline: uint16 = tick_ms() + 20
    #   while True:
    #       do_work()
    #       sleep_until(deadline)   # always wakes at the exact same cadence
    #       deadline = deadline + 20
    #
    # Handles the uint16 wrap at 65535 correctly via signed subtraction.
    # Max sleep duration: 32767 ms (~32 s).  For longer sleeps use delay_ms().
    while _tick != target:
        pass


# Stack canary guard addresses: one sentinel byte per task, written during init.
# Stacks grow down; the canary sits at the lowest address the stack should reach.
#   task 1 stack: 0x0600-0x067F   task 2 stack: 0x0500-0x057F
#   task 3 stack: 0x0400-0x047F
_t1_guard: ptr[uint8] = ptr(0x0600)
_t2_guard: ptr[uint8] = ptr(0x0500)
_t3_guard: ptr[uint8] = ptr(0x0400)


def check_stack_canaries():
    # Return 1 if no task stack has overflowed into its guard region, 0 otherwise.
    # Call from main or a monitor task to detect stack overflow early.
    ok: uint8 = 1
    if _t1_guard.value != 0xCC:
        ok = 0
    if _t2_guard.value != 0xCC:
        ok = 0
    if _t3_guard.value != 0xCC:
        ok = 0
    return ok


def _init_task1_frame():
    # Lay down a fake saved-context frame for task 1 at SRAM 0x0680.
    # Frame layout (35 bytes, written low-to-high):
    #   byte  0     -- r31 (0)
    #   byte  1     -- r30 (0)
    #   byte  2     -- r29 = Y-high (0x01, so Y = 0x0100 on resume)
    #   byte  3     -- r28 = Y-low  (0x00)
    #   bytes 4-32  -- r27..r0+SREG zeros
    #   byte  33    -- PCH = hi8(task_fn word-address)
    #   byte  34    -- PCL = lo8(task_fn word-address)
    # SP is set to frame_base - 1 = 0x067F so the systick ISR pops
    # this frame as if task 1 had been preempted at its entry point.
    # Stack canary: 0xCC at 0x0600 (bottom of task 1's stack region).
    asm("""
    lds  r28, {_task_fns} + 2    ; task 1 fn lo
    lds  r29, {_task_fns} + 3    ; task 1 fn hi
    ldi  r26, lo8(0x0680)
    ldi  r27, hi8(0x0680)
    ldi  r16, 0
    ldi  r17, 33
_rtos_zero_t1:
    st   x+, r16
    dec  r17
    brne _rtos_zero_t1
    st   x+, r29
    st   x,  r28
    ; Fix Y-high slot (frame[2] = 0x0682): tasks need Y = 0x0100 on first run
    ldi  r26, lo8(0x0682)
    ldi  r27, hi8(0x0682)
    ldi  r16, 0x01
    st   x, r16
    ldi  r16, 0x7F
    sts  {_task_sp} + 2, r16
    ldi  r16, 0x06
    sts  {_task_sp} + 3, r16
    ; Stack canary: mark guard byte at 0x0600
    ldi  r16, 0xCC
    sts  0x0600, r16
    """)


def _init_task2_frame():
    # Same as _init_task1_frame but for task 2 at SRAM 0x0580 / SP 0x057F.
    # Stack canary: 0xCC at 0x0500.
    asm("""
    lds  r28, {_task_fns} + 4    ; task 2 fn lo
    lds  r29, {_task_fns} + 5    ; task 2 fn hi
    ldi  r26, lo8(0x0580)
    ldi  r27, hi8(0x0580)
    ldi  r16, 0
    ldi  r17, 33
_rtos_zero_t2:
    st   x+, r16
    dec  r17
    brne _rtos_zero_t2
    st   x+, r29
    st   x,  r28
    ldi  r26, lo8(0x0582)
    ldi  r27, hi8(0x0582)
    ldi  r16, 0x01
    st   x, r16
    ldi  r16, 0x7F
    sts  {_task_sp} + 4, r16
    ldi  r16, 0x05
    sts  {_task_sp} + 5, r16
    ; Stack canary: mark guard byte at 0x0500
    ldi  r16, 0xCC
    sts  0x0500, r16
    """)


def _init_task3_frame():
    # Same as _init_task1_frame but for task 3 at SRAM 0x0480 / SP 0x047F.
    # Stack canary: 0xCC at 0x0400.
    asm("""
    lds  r28, {_task_fns} + 6    ; task 3 fn lo
    lds  r29, {_task_fns} + 7    ; task 3 fn hi
    ldi  r26, lo8(0x0480)
    ldi  r27, hi8(0x0480)
    ldi  r16, 0
    ldi  r17, 33
_rtos_zero_t3:
    st   x+, r16
    dec  r17
    brne _rtos_zero_t3
    st   x+, r29
    st   x,  r28
    ldi  r26, lo8(0x0482)
    ldi  r27, hi8(0x0482)
    ldi  r16, 0x01
    st   x, r16
    ldi  r16, 0x7F
    sts  {_task_sp} + 6, r16
    ldi  r16, 0x04
    sts  {_task_sp} + 7, r16
    ; Stack canary: mark guard byte at 0x0400
    ldi  r16, 0xCC
    sts  0x0400, r16
    """)


def _launch_task0():
    # Hand control to task 0 by jumping to its entry point.
    # SEI and IJMP are deliberately adjacent: AVR guarantees that the one
    # instruction following SEI executes before any pending interrupt fires,
    # so the jump always completes before the first systick can preempt it.
    asm("""
    ldi  r28, lo8(_stack_base)
    ldi  r29, hi8(_stack_base)
    lds  r30, {_task_fns}
    lds  r31, {_task_fns} + 1
    sei
    ijmp
    """)


def start_scheduler():
    # Configure timers, build the schedule, set up task contexts, and hand
    # off to the preemptive scheduler.
    #
    # Stack layout (non-overlapping, stacks grow downward):
    #   task 0 -- main stack  (SP = 0x08FF, launched via _launch_task0)
    #   task 1 -- SP = 0x067F, fake frame at 0x0680-0x069E
    #   task 2 -- SP = 0x057F, fake frame at 0x0580-0x059E
    #   task 3 -- SP = 0x047F, fake frame at 0x0480-0x049E
    global _cur_task, _sched_idx
    timer_init()
    _build_schedule()
    _cur_task  = 0
    _sched_idx = 0
    _init_task1_frame()
    _init_task2_frame()
    _init_task3_frame()
    _launch_task0()
