// wprcpu - dynarmic behind a C ABI. See wprcpu.h for the contract.
#include "wprcpu.h"

#include <array>
#include <atomic>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <memory>
#include <optional>
#include <string>
#include <vector>

#ifdef _WIN32
#include <windows.h>
#else
#include <sys/mman.h>
#endif

#include <dynarmic/interface/A32/a32.h>
#include <dynarmic/interface/A32/config.h>
#include <dynarmic/interface/A32/coprocessor.h>
#include <dynarmic/interface/halt_reason.h>
#include <dynarmic/interface/exclusive_monitor.h>
#include <dynarmic/interface/A32/coprocessor_util.h>
using Dynarmic::A32::CoprocReg;

namespace {

constexpr uint32_t kPageBits = 12;
constexpr uint32_t kPageSize = 1u << kPageBits;
constexpr size_t kPages = size_t{1} << (32 - kPageBits);

// One 32-bit address space: a host pointer per page, a permission byte per page, and which
// pages the JIT may reach without asking. A page is on the fast path only when it is RWX
// and unwatched; everything else - the trap page, the null page, nothing at all - goes
// through the callbacks below, where a decision can be made.
struct Memory {
    std::vector<uint8_t*> host;                                   // per page, null = unmapped
    std::vector<uint8_t>  prot;                                   // per page
    std::array<uint8_t*, kPages>* fast;                           // dynarmic's page table
    std::vector<std::pair<uint8_t*, size_t>> blocks;              // what host points into

    Memory() : host(kPages, nullptr), prot(kPages, 0), fast(new std::array<uint8_t*, kPages>()) {
        fast->fill(nullptr);
    }
    ~Memory() {
        for (auto& [base, size] : blocks) release(base, size);
        delete fast;
    }

    // Zero-filled pages straight from the OS, committed only when first touched. The guest
    // heap is a gigabyte; new[] + memset would make every byte of it resident at start-up,
    // which a phone answers by killing the process.
    static uint8_t* reserve(size_t size) {
#ifdef _WIN32
        return static_cast<uint8_t*>(VirtualAlloc(nullptr, size, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE));
#else
        void* p = mmap(nullptr, size, PROT_READ | PROT_WRITE, MAP_PRIVATE | MAP_ANONYMOUS | MAP_NORESERVE, -1, 0);
        return p == MAP_FAILED ? nullptr : static_cast<uint8_t*>(p);
#endif
    }

    static void release(uint8_t* base, size_t size) {
#ifdef _WIN32
        (void)size;
        VirtualFree(base, 0, MEM_RELEASE);
#else
        munmap(base, size);
#endif
    }

    static uint32_t page(uint32_t address) { return address >> kPageBits; }

    void refresh(uint32_t p) {
        bool onFastPath = host[p] != nullptr && prot[p] == (WPRCPU_PROT_READ | WPRCPU_PROT_WRITE | WPRCPU_PROT_EXEC);
        (*fast)[p] = onFastPath ? host[p] : nullptr;
    }
};

// TPIDRURO / TPIDRURW: the only CP15 traffic a user-mode WP8 image makes, and the words that
// make its TEB reachable. dynarmic compiles a coprocessor access into a direct read of the
// pointer we hand back, so the host just keeps two words.
class ThreadPointers final : public Dynarmic::A32::Coprocessor {
public:
    uint32_t tpidrurw = 0;
    uint32_t tpidruro = 0;

    std::optional<Callback> CompileInternalOperation(bool, unsigned, CoprocReg, CoprocReg, CoprocReg, unsigned) override {
        return std::nullopt;
    }
    CallbackOrAccessOneWord CompileSendOneWord(bool two, unsigned opc1, CoprocReg CRn, CoprocReg CRm, unsigned opc2) override {
        if (!two && opc1 == 0 && CRn == CoprocReg::C13 && CRm == CoprocReg::C0) {
            if (opc2 == 2) return &tpidrurw;
            if (opc2 == 3) return &tpidruro;
        }
        return std::monostate{};
    }
    CallbackOrAccessTwoWords CompileSendTwoWords(bool, unsigned, CoprocReg) override { return std::monostate{}; }
    CallbackOrAccessOneWord CompileGetOneWord(bool two, unsigned opc1, CoprocReg CRn, CoprocReg CRm, unsigned opc2) override {
        if (!two && opc1 == 0 && CRn == CoprocReg::C13 && CRm == CoprocReg::C0) {
            if (opc2 == 2) return &tpidrurw;
            if (opc2 == 3) return &tpidruro;
        }
        return std::monostate{};
    }
    CallbackOrAccessTwoWords CompileGetTwoWords(bool, unsigned, CoprocReg) override { return std::monostate{}; }
    std::optional<Callback> CompileLoadWords(bool, bool, CoprocReg, std::optional<std::uint8_t>) override { return std::nullopt; }
    std::optional<Callback> CompileStoreWords(bool, bool, CoprocReg, std::optional<std::uint8_t>) override { return std::nullopt; }
};

struct Cpu;

class Callbacks final : public Dynarmic::A32::UserCallbacks {
public:
    explicit Callbacks(Cpu& cpu) : cpu_(cpu) {}

    std::uint8_t  MemoryRead8(std::uint32_t a) override { return read<std::uint8_t>(a); }
    std::uint16_t MemoryRead16(std::uint32_t a) override { return read<std::uint16_t>(a); }
    std::uint32_t MemoryRead32(std::uint32_t a) override { return read<std::uint32_t>(a); }
    std::uint64_t MemoryRead64(std::uint32_t a) override { return read<std::uint64_t>(a); }
    void MemoryWrite8(std::uint32_t a, std::uint8_t v) override { write(a, v); }
    void MemoryWrite16(std::uint32_t a, std::uint16_t v) override { write(a, v); }
    void MemoryWrite32(std::uint32_t a, std::uint32_t v) override { write(a, v); }
    void MemoryWrite64(std::uint32_t a, std::uint64_t v) override { write(a, v); }

    // Exclusive stores: single core, no contention - a plain store that always succeeds.
    bool MemoryWriteExclusive8(std::uint32_t a, std::uint8_t v, std::uint8_t) override { write(a, v); return true; }
    bool MemoryWriteExclusive16(std::uint32_t a, std::uint16_t v, std::uint16_t) override { write(a, v); return true; }
    bool MemoryWriteExclusive32(std::uint32_t a, std::uint32_t v, std::uint32_t) override { write(a, v); return true; }
    bool MemoryWriteExclusive64(std::uint32_t a, std::uint64_t v, std::uint64_t) override { write(a, v); return true; }

    // Instruction fetch at translation time. This is where "the image jumped into data"
    // becomes visible - a page without execute permission, or no page at all - and the one
    // place a fetch can be told apart from a data read.
    std::optional<std::uint32_t> MemoryReadCode(std::uint32_t vaddr) override;

    void InterpreterFallback(std::uint32_t pc, size_t) override;
    void CallSVC(std::uint32_t swi) override;
    void ExceptionRaised(std::uint32_t pc, Dynarmic::A32::Exception exception) override;
    void AddTicks(std::uint64_t ticks) override;
    std::uint64_t GetTicksRemaining() override;

private:
    template <typename T> T read(std::uint32_t address);
    template <typename T> void write(std::uint32_t address, T value);
    Cpu& cpu_;
};

struct Cpu {
    wprcpu_callbacks host{};
    Memory memory;
    std::shared_ptr<ThreadPointers> cp15 = std::make_shared<ThreadPointers>();
    std::unique_ptr<Callbacks> callbacks;
    std::unique_ptr<Dynarmic::A32::Jit> jit;

    // ldrex/strex - the CRT's interlocked operations - need an exclusive monitor even on
    // one core; dynarmic asserts on its absence the first time one runs.
    Dynarmic::ExclusiveMonitor monitor{1};

    std::uint64_t ticksLeft = 0;
    std::uint64_t retired = 0;
    std::atomic<int> outcome{WPRCPU_OUTCOME_BUDGET};
    std::string stopMessage;
    bool stopped = false;

    void end(int why) {
        outcome.store(why);
        if (jit) jit->HaltExecution();
    }
};

// A page the fast path does not serve, resolved for one access. Returns null after asking the
// host and being refused, in which case the run is already ending.
uint8_t* resolve(Cpu& cpu, std::uint32_t address, int access, int size, int need) {
    for (int attempt = 0; attempt < 2; attempt++) {
        uint32_t p = Memory::page(address);
        uint8_t* h = cpu.memory.host[p];
        if (h != nullptr && (cpu.memory.prot[p] & need) == need) {
            return h + (address & (kPageSize - 1));
        }

        if (attempt == 1 || cpu.host.on_unmapped == nullptr ||
            !cpu.host.on_unmapped(cpu.host.user, access, address, size)) {
            break;
        }
        // The host mapped or permitted it; look again.
    }

    if (cpu.outcome.load() == WPRCPU_OUTCOME_BUDGET) {
        cpu.end(WPRCPU_OUTCOME_FAULT);
    }
    return nullptr;
}

template <typename T> T Callbacks::read(std::uint32_t address) {
    // A value straddling a page boundary is assembled a byte at a time; it is rare enough
    // that the simple path is the right one.
    if (((address & (kPageSize - 1)) + sizeof(T)) > kPageSize) {
        T value = 0;
        for (size_t i = 0; i < sizeof(T); i++) {
            value |= T(read<std::uint8_t>(address + uint32_t(i))) << (8 * i);
        }
        return value;
    }

    uint8_t* h = resolve(cpu_, address, WPRCPU_ACCESS_READ, int(sizeof(T)), WPRCPU_PROT_READ);
    if (h == nullptr) return 0;
    T value;
    std::memcpy(&value, h, sizeof(T));
    return value;
}

template <typename T> void Callbacks::write(std::uint32_t address, T value) {
    if (((address & (kPageSize - 1)) + sizeof(T)) > kPageSize) {
        for (size_t i = 0; i < sizeof(T); i++) {
            write<std::uint8_t>(address + uint32_t(i), std::uint8_t(value >> (8 * i)));
        }
        return;
    }

    uint8_t* h = resolve(cpu_, address, WPRCPU_ACCESS_WRITE, int(sizeof(T)), WPRCPU_PROT_WRITE);
    if (h == nullptr) return;
    std::memcpy(h, &value, sizeof(T));
}

// The last address the translator fetched code from, and how many fetches there have been.
// Diagnostic only: a run that hangs inside the JIT without retiring an instruction - and
// ignores a halt - is stuck translating, and this says where.
std::atomic<std::uint32_t> g_lastFetch{0};
std::atomic<std::uint64_t> g_fetches{0};

std::optional<std::uint32_t> Callbacks::MemoryReadCode(std::uint32_t vaddr) {
    g_lastFetch.store(vaddr, std::memory_order_relaxed);
    g_fetches.fetch_add(1, std::memory_order_relaxed);
    uint8_t* h = resolve(cpu_, vaddr, WPRCPU_ACCESS_FETCH, 4, WPRCPU_PROT_EXEC);
    if (h == nullptr) return std::nullopt;
    std::uint32_t value;
    if (((vaddr & (kPageSize - 1)) + 4) > kPageSize) {
        value = read<std::uint32_t>(vaddr);
    } else {
        std::memcpy(&value, h, 4);
    }
    return value;
}

void Callbacks::InterpreterFallback(std::uint32_t pc, size_t) {
    if (cpu_.host.on_exception) cpu_.host.on_exception(cpu_.host.user, pc, -1);
    cpu_.end(WPRCPU_OUTCOME_FAULT);
}

void Callbacks::CallSVC(std::uint32_t swi) {
    if (cpu_.host.on_svc) cpu_.host.on_svc(cpu_.host.user, cpu_.jit->Regs()[15], swi);
}

void Callbacks::ExceptionRaised(std::uint32_t pc, Dynarmic::A32::Exception exception) {
    if (cpu_.host.on_exception) cpu_.host.on_exception(cpu_.host.user, pc, int(exception));
    cpu_.end(WPRCPU_OUTCOME_FAULT);
}

void Callbacks::AddTicks(std::uint64_t ticks) {
    cpu_.retired += ticks;
    cpu_.ticksLeft = ticks > cpu_.ticksLeft ? 0 : cpu_.ticksLeft - ticks;
}

std::uint64_t Callbacks::GetTicksRemaining() {
    return cpu_.ticksLeft;
}

}  // namespace

extern "C" {

WPRCPU_API int wprcpu_abi_version(void) { return 1; }

WPRCPU_API uint32_t wprcpu_last_fetch(void) { return g_lastFetch.load(std::memory_order_relaxed); }
WPRCPU_API uint64_t wprcpu_fetch_count(void) { return g_fetches.load(std::memory_order_relaxed); }

WPRCPU_API wprcpu* wprcpu_create(const wprcpu_callbacks* callbacks) {
    auto* cpu = new Cpu();
    if (callbacks) cpu->host = *callbacks;
    cpu->callbacks = std::make_unique<Callbacks>(*cpu);

    Dynarmic::A32::UserConfig config;
    config.callbacks = cpu->callbacks.get();
    config.arch_version = Dynarmic::A32::ArchVersion::v7;
    config.page_table = cpu->memory.fast;
    config.absolute_offset_page_table = false;
    config.detect_misaligned_access_via_page_table = 0;
    config.coprocessors[15] = cpu->cp15;
    config.global_monitor = &cpu->monitor;
    config.define_unpredictable_behaviour = true;
    config.enable_cycle_counting = true;
    config.always_little_endian = true;
    config.hook_isb = false;
    config.code_cache_size = 256 * 1024 * 1024;

    cpu->jit = std::make_unique<Dynarmic::A32::Jit>(config);
    return reinterpret_cast<wprcpu*>(cpu);
}

WPRCPU_API void wprcpu_destroy(wprcpu* handle) {
    delete reinterpret_cast<Cpu*>(handle);
}

WPRCPU_API int wprcpu_map(wprcpu* handle, uint32_t address, uint32_t size, int prot) {
    auto* cpu = reinterpret_cast<Cpu*>(handle);
    if ((address & (kPageSize - 1)) != 0 || (size & (kPageSize - 1)) != 0 || size == 0) return 0;
    uint64_t end = uint64_t(address) + size;
    if (end > (uint64_t{1} << 32)) return 0;

    uint32_t first = Memory::page(address), count = size >> kPageBits;
    for (uint32_t p = first; p < first + count; p++) {
        if (cpu->memory.host[p] != nullptr) return 0;   // overlapping an existing mapping
    }

    uint8_t* base = Memory::reserve(size);
    if (base == nullptr) return 0;
    cpu->memory.blocks.emplace_back(base, size);

    for (uint32_t p = first, i = 0; i < count; p++, i++) {
        cpu->memory.host[p] = base + size_t(i) * kPageSize;
        cpu->memory.prot[p] = uint8_t(prot);
        cpu->memory.refresh(p);
    }
    return 1;
}

WPRCPU_API int wprcpu_protect(wprcpu* handle, uint32_t address, uint32_t size, int prot) {
    auto* cpu = reinterpret_cast<Cpu*>(handle);
    if ((address & (kPageSize - 1)) != 0 || (size & (kPageSize - 1)) != 0) return 0;
    uint32_t first = Memory::page(address), count = size >> kPageBits;
    for (uint32_t p = first, i = 0; i < count; p++, i++) {
        if (cpu->memory.host[p] == nullptr) return 0;
        cpu->memory.prot[p] = uint8_t(prot);
        cpu->memory.refresh(p);
    }
    // Permission changes can move code on or off the fast path; drop what was compiled.
    cpu->jit->InvalidateCacheRange(address, size);
    return 1;
}

WPRCPU_API int wprcpu_is_mapped(wprcpu* handle, uint32_t address) {
    auto* cpu = reinterpret_cast<Cpu*>(handle);
    return cpu->memory.host[Memory::page(address)] != nullptr ? 1 : 0;
}

WPRCPU_API int wprcpu_read(wprcpu* handle, uint32_t address, void* destination, uint32_t size) {
    auto* cpu = reinterpret_cast<Cpu*>(handle);
    auto* out = static_cast<uint8_t*>(destination);
    while (size > 0) {
        uint32_t p = Memory::page(address);
        uint8_t* h = cpu->memory.host[p];
        if (h == nullptr) return 0;
        uint32_t offset = address & (kPageSize - 1);
        uint32_t chunk = kPageSize - offset;
        if (chunk > size) chunk = size;
        std::memcpy(out, h + offset, chunk);
        out += chunk; address += chunk; size -= chunk;
    }
    return 1;
}

WPRCPU_API int wprcpu_write(wprcpu* handle, uint32_t address, const void* source, uint32_t size) {
    auto* cpu = reinterpret_cast<Cpu*>(handle);
    auto* in = static_cast<const uint8_t*>(source);
    uint32_t start = address, total = size;
    while (size > 0) {
        uint32_t p = Memory::page(address);
        uint8_t* h = cpu->memory.host[p];
        if (h == nullptr) return 0;
        uint32_t offset = address & (kPageSize - 1);
        uint32_t chunk = kPageSize - offset;
        if (chunk > size) chunk = size;
        std::memcpy(h + offset, in, chunk);
        in += chunk; address += chunk; size -= chunk;
    }
    // Host writes may land on code the JIT already translated - but only a page with
    // execute permission can, and invalidating is not free: dynarmic answers it by halting
    // the run at the next block boundary so the cache can be dropped. Stubs write their
    // out-parameters to the (non-executable) stack on nearly every call, so this check is
    // the difference between a run and a stall.
    bool executable = false;
    for (uint32_t p = Memory::page(start), last = Memory::page(start + total - 1); p <= last; p++) {
        if (cpu->memory.prot[p] & WPRCPU_PROT_EXEC) { executable = true; break; }
    }
    if (executable) cpu->jit->InvalidateCacheRange(start, total);
    return 1;
}

WPRCPU_API void wprcpu_invalidate(wprcpu* handle, uint32_t address, uint32_t size) {
    reinterpret_cast<Cpu*>(handle)->jit->InvalidateCacheRange(address, size);
}

WPRCPU_API uint32_t* wprcpu_regs(wprcpu* handle) { return reinterpret_cast<Cpu*>(handle)->jit->Regs().data(); }
WPRCPU_API uint32_t* wprcpu_extregs(wprcpu* handle) { return reinterpret_cast<Cpu*>(handle)->jit->ExtRegs().data(); }
WPRCPU_API uint32_t wprcpu_get_cpsr(wprcpu* handle) { return reinterpret_cast<Cpu*>(handle)->jit->Cpsr(); }
WPRCPU_API void wprcpu_set_cpsr(wprcpu* handle, uint32_t value) { reinterpret_cast<Cpu*>(handle)->jit->SetCpsr(value); }
WPRCPU_API uint32_t wprcpu_get_fpscr(wprcpu* handle) { return reinterpret_cast<Cpu*>(handle)->jit->Fpscr(); }
WPRCPU_API void wprcpu_set_fpscr(wprcpu* handle, uint32_t value) { reinterpret_cast<Cpu*>(handle)->jit->SetFpscr(value); }

WPRCPU_API void wprcpu_set_thread_pointers(wprcpu* handle, uint32_t tpidruro, uint32_t tpidrurw) {
    auto* cpu = reinterpret_cast<Cpu*>(handle);
    cpu->cp15->tpidruro = tpidruro;
    cpu->cp15->tpidrurw = tpidrurw;
}

WPRCPU_API int wprcpu_run(wprcpu* handle, uint64_t budget) {
    auto* cpu = reinterpret_cast<Cpu*>(handle);
    cpu->ticksLeft = budget;
    cpu->outcome.store(WPRCPU_OUTCOME_BUDGET);
    cpu->stopped = false;
    cpu->stopMessage.clear();
    cpu->jit->ClearHalt();
    for (;;) {
        Dynarmic::HaltReason hr = cpu->jit->Run();

        // The JIT halts itself to drop translations after a host write into code. That is
        // not the run ending - unless something else ended it in the same breath.
        bool onlyInvalidation = Dynarmic::Has(hr, Dynarmic::HaltReason::CacheInvalidation)
            && !Dynarmic::Has(hr, Dynarmic::HaltReason::UserDefined1)
            && !Dynarmic::Has(hr, Dynarmic::HaltReason::UserDefined2)
            && !Dynarmic::Has(hr, Dynarmic::HaltReason::MemoryAbort)
            && !Dynarmic::Has(hr, Dynarmic::HaltReason::Step);
        if (onlyInvalidation && cpu->ticksLeft > 0 && cpu->outcome.load() == WPRCPU_OUTCOME_BUDGET) {
            cpu->jit->ClearHalt(Dynarmic::HaltReason::CacheInvalidation);
            continue;
        }
        break;
    }
    return cpu->outcome.load();
}

WPRCPU_API void wprcpu_halt(wprcpu* handle) {
    auto* cpu = reinterpret_cast<Cpu*>(handle);
    if (cpu->outcome.load() == WPRCPU_OUTCOME_BUDGET) cpu->outcome.store(WPRCPU_OUTCOME_HALTED);
    cpu->jit->HaltExecution();
}

WPRCPU_API void wprcpu_stop(wprcpu* handle, const char* message) {
    auto* cpu = reinterpret_cast<Cpu*>(handle);
    cpu->stopped = true;
    cpu->stopMessage = message ? message : "";
    cpu->outcome.store(WPRCPU_OUTCOME_STOPPED);
    cpu->jit->HaltExecution();
}

WPRCPU_API const char* wprcpu_stop_message(wprcpu* handle) {
    auto* cpu = reinterpret_cast<Cpu*>(handle);
    return cpu->stopped ? cpu->stopMessage.c_str() : nullptr;
}

WPRCPU_API uint64_t wprcpu_instructions_retired(wprcpu* handle) {
    return reinterpret_cast<Cpu*>(handle)->retired;
}

}  // extern "C"
