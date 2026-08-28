// emutls_runtime.c — emulated thread-local storage ABI for the in-process ORC JIT.
//
// When RazorForge is JIT'd in-process (the dev-loop `buildandrun` path with RAZORFORGE_JIT=1), LLVM's
// ORC LLJIT lowers the emitted `thread_local` globals (the per-thread trace shadow-stack:
// `_rf_trace_stack` / `_rf_trace_depth`) to the emulated-TLS ABI, which needs a runtime helper
// `__emutls_get_address`. In an AOT build clang links that helper from compiler-rt; the JIT resolves it
// from THIS runtime DLL instead (via ORC's process-wide symbol search). The control variables
// `__emutls_v.*` are synthesized into the JIT'd module by LLVM's emutls lowering, so only the accessor
// lives here.
//
// This is a faithful, minimal reimplementation of compiler-rt's emutls.c accessor. It is compiled
// natively into the runtime DLL, so it may freely use the platform's OWN (native) thread-local storage
// for its per-thread object array — the emulation is only for the JIT'd code, not for us.

#include <stdint.h>
#include <stdlib.h>
#include <string.h>

#include "rf_sync.h"

// Native TLS keyword for the runtime DLL itself (compiled by clang/MSVC, so real TLS is available).
#if defined(_MSC_VER) && !defined(__clang__)
#define RF_TLS __declspec(thread)
#else
#define RF_TLS _Thread_local
#endif

// LLVM/compiler-rt control block for one emulated TLS variable. Layout MUST match compiler-rt:
//   size, align, {index | address} union, template value.
typedef struct rf_emutls_control
{
    size_t size;   // object size in bytes
    size_t align;  // object alignment in bytes
    union
    {
        uintptr_t index;  // 1-based slot: per-thread array[index-1] holds this object
        void* address;    // (single-thread fast path — unused here)
    } object;
    void* value;  // initialization template, or NULL for zero-init
} rf_emutls_control;

// Per-thread growable array of object pointers, indexed by (control->object.index - 1).
typedef struct rf_emutls_array
{
    uintptr_t size;  // number of slots currently allocated
    void** data;     // slots (each NULL until first access allocates the object)
} rf_emutls_array;

static RF_TLS rf_emutls_array* rf_emutls_tls = NULL;

// One global counter hands out monotonically increasing indices; guarded by a statically-initialized
// exclusive lock (no constructor / init-once dance needed).
static rf_rwlock rf_emutls_lock = RF_RWLOCK_INIT;
static uintptr_t rf_emutls_next_index = 0;

static void* rf_emutls_alloc(size_t size, size_t align)
{
    if (align < sizeof(void*))
    {
        align = sizeof(void*);
    }

    void* p = NULL;
#ifdef _WIN32
    p = _aligned_malloc(size, align);
#else
    if (posix_memalign(&p, align, size) != 0)
    {
        p = NULL;
    }
#endif
    return p;
}

// The emutls ABI accessor: return this thread's address for the object described by `control`,
// allocating (and template/zero-initializing) it on first touch for the thread.
void* __emutls_get_address(void* control_ptr)
{
    rf_emutls_control* control = (rf_emutls_control*)control_ptr;

    // Resolve (or lazily assign) this variable's global slot index.
    uintptr_t index = __atomic_load_n(&control->object.index, __ATOMIC_ACQUIRE);
    if (index == 0)
    {
        rf_rwlock_lock_exclusive(&rf_emutls_lock);
        index = control->object.index;
        if (index == 0)
        {
            index = ++rf_emutls_next_index;
            __atomic_store_n(&control->object.index, index, __ATOMIC_RELEASE);
        }
        rf_rwlock_unlock_exclusive(&rf_emutls_lock);
    }

    // Ensure this thread's array is large enough to hold slot `index`.
    rf_emutls_array* array = rf_emutls_tls;
    if (array == NULL)
    {
        array = (rf_emutls_array*)calloc(1, sizeof(rf_emutls_array));
        rf_emutls_tls = array;
    }
    if (array->size < index)
    {
        void** grown = (void**)realloc(array->data, index * sizeof(void*));
        memset(&grown[array->size], 0, (index - array->size) * sizeof(void*));
        array->data = grown;
        array->size = index;
    }

    // Allocate + initialize this thread's copy on first touch.
    void* obj = array->data[index - 1];
    if (obj == NULL)
    {
        obj = rf_emutls_alloc(control->size, control->align);
        if (control->value != NULL)
        {
            memcpy(obj, control->value, control->size);
        }
        else
        {
            memset(obj, 0, control->size);
        }
        array->data[index - 1] = obj;
    }
    return obj;
}
