// wprcpu - a C ABI over dynarmic's A32 JIT for the WPR WP8 probe.
//
// One CPU, one 32-bit guest address space, host-owned memory. The C# side owns everything
// that means anything (the trap table, the allocator, the stubs); this owns the CPU and the
// bytes, and calls back for the four things only the host can decide.
#pragma once
#include <stdint.h>

#ifdef _WIN32
#define WPRCPU_API __declspec(dllexport)
#else
#define WPRCPU_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef struct wprcpu wprcpu;

enum wprcpu_prot { WPRCPU_PROT_NONE = 0, WPRCPU_PROT_READ = 1, WPRCPU_PROT_WRITE = 2, WPRCPU_PROT_EXEC = 4 };
enum wprcpu_access { WPRCPU_ACCESS_READ = 0, WPRCPU_ACCESS_WRITE = 1, WPRCPU_ACCESS_FETCH = 2 };

// Why wprcpu_run returned.
enum wprcpu_outcome {
    WPRCPU_OUTCOME_BUDGET = 0,     // the instruction budget ran out
    WPRCPU_OUTCOME_HALTED = 1,     // wprcpu_halt was called (by a callback or another thread)
    WPRCPU_OUTCOME_FAULT = 2,      // the CPU raised something the host declined (see on_exception)
    WPRCPU_OUTCOME_STOPPED = 3,    // a callback asked for the run to end with a message
};

// Callbacks. `user` is the pointer given at create. All run on the thread that called
// wprcpu_run, from inside the JIT: they must not call wprcpu_run and must not throw.
typedef struct wprcpu_callbacks {
    void* user;

    // A trap slot executed its svc. `pc` is the address of the instruction AFTER the svc, so
    // the slot is pc - 2. Registers are exact here (svc ends a block). The handler may write
    // any register; on return the CPU continues at pc - which in a slot is the bx r12.
    void (*on_svc)(void* user, uint32_t pc, uint32_t swi);

    // The CPU touched a page that is not mapped, or is mapped without the needed permission.
    // Return 1 after mapping/permitting it to have the access retried transparently, or 0 to
    // stop the run with WPRCPU_OUTCOME_FAULT. For READ/WRITE the register file may be stale
    // (mid-block); for FETCH it is exact.
    int (*on_unmapped)(void* user, int access, uint32_t address, int size);

    // Undefined / unpredictable instruction, decode error, interpreter fallback and the like.
    // `kind` is dynarmic's Exception value (or -1 for InterpreterFallback). The run stops with
    // WPRCPU_OUTCOME_FAULT after this returns.
    void (*on_exception)(void* user, uint32_t pc, int kind);
} wprcpu_callbacks;

WPRCPU_API int wprcpu_abi_version(void);

// Diagnostics: the last guest address the translator fetched, and the fetch count.
WPRCPU_API uint32_t wprcpu_last_fetch(void);
WPRCPU_API uint64_t wprcpu_fetch_count(void);

WPRCPU_API wprcpu* wprcpu_create(const wprcpu_callbacks* callbacks);
WPRCPU_API void wprcpu_destroy(wprcpu* cpu);

// ---- guest memory, host side -------------------------------------------------------------
// Pages are 4 KB. map allocates zero-filled host memory; protect changes permissions and
// decides whether a page is served by the JIT's fast path (RWX) or by callbacks (anything
// else - which is how a trap page refuses writes and a null page reads as zero).
WPRCPU_API int  wprcpu_map(wprcpu* cpu, uint32_t address, uint32_t size, int prot);
WPRCPU_API int  wprcpu_protect(wprcpu* cpu, uint32_t address, uint32_t size, int prot);
WPRCPU_API int  wprcpu_is_mapped(wprcpu* cpu, uint32_t address);
// Host reads/writes ignore protection. A write invalidates translated code in the range.
WPRCPU_API int  wprcpu_read(wprcpu* cpu, uint32_t address, void* destination, uint32_t size);
WPRCPU_API int  wprcpu_write(wprcpu* cpu, uint32_t address, const void* source, uint32_t size);
WPRCPU_API void wprcpu_invalidate(wprcpu* cpu, uint32_t address, uint32_t size);

// ---- registers ----------------------------------------------------------------------------
// Live pointers into the JIT's register file: r0-r15 (16 words) and the VFP bank (64 words,
// s0-s63 / d0-d31). Valid for the lifetime of the cpu. Read/write them between runs or from
// inside on_svc; they are not maintained mid-block.
WPRCPU_API uint32_t* wprcpu_regs(wprcpu* cpu);
WPRCPU_API uint32_t* wprcpu_extregs(wprcpu* cpu);
WPRCPU_API uint32_t  wprcpu_get_cpsr(wprcpu* cpu);
WPRCPU_API void      wprcpu_set_cpsr(wprcpu* cpu, uint32_t value);
WPRCPU_API uint32_t  wprcpu_get_fpscr(wprcpu* cpu);
WPRCPU_API void      wprcpu_set_fpscr(wprcpu* cpu, uint32_t value);
// CP15 c13,c0,3 (TPIDRURO, read-only to user code) and c13,c0,2 (TPIDRURW).
WPRCPU_API void      wprcpu_set_thread_pointers(wprcpu* cpu, uint32_t tpidruro, uint32_t tpidrurw);

// ---- execution ----------------------------------------------------------------------------
// Runs from the current PC/CPSR for at most `budget` instructions (checked at block ends, so
// it may overshoot by a block). Bit 0 of the PC is NOT a Thumb selector here - set the T bit
// through wprcpu_set_cpsr (0x20) and give an even PC.
WPRCPU_API int      wprcpu_run(wprcpu* cpu, uint64_t budget);
WPRCPU_API void     wprcpu_halt(wprcpu* cpu);
WPRCPU_API uint64_t wprcpu_instructions_retired(wprcpu* cpu);
// The message a callback left when it asked for WPRCPU_OUTCOME_STOPPED, or NULL.
WPRCPU_API void        wprcpu_stop(wprcpu* cpu, const char* message);
WPRCPU_API const char* wprcpu_stop_message(wprcpu* cpu);

#ifdef __cplusplus
}
#endif
