// FFI struct-embedded callback test helpers.
//
// A wgpu/GLFW-style C API hands you a STRUCT whose fields are a callback function pointer plus an
// opaque userdata pointer, and later invokes `cb(args, userdata)`. RazorForge represents a routine
// VALUE as a fat pair `{ ptr fn, ptr bound }` (captureless -> bound == NULL; capturing -> bound points
// at the heap payload of pre-bound captures). A record field of type `Routine[...]` therefore lays out
// in memory as `{ fn, bound }`, which maps DIRECTLY onto a C `{ cb, void* ud }` pair — so a record with
// a `Routine` field can be handed to such an API with no boxing.
//
// These helpers exist only to exercise that mapping end-to-end from the test suite (there is no libc
// function that takes a struct-with-callback). They are C-ABI plain functions, linked from
// razorforge_runtime like the rest of the runtime.

#include <stdint.h>

// Mirrors a userdata-pair callback struct: `cb` is invoked as `cb(arg, ud)`.
typedef struct
{
    int32_t (*cb)(int32_t, void*);
    void* ud;
} rf_cb_info;

// Invoke the embedded callback with the embedded userdata (the capturing-routine path: the RF fat
// value's `bound` arrives as `ud`, and the lifted body's trailing `ptr bound` param matches). A
// captureless RF routine's lifted body ignores the extra `ud` argument (register-ABI safe).
int32_t rf_test_invoke_cbinfo(rf_cb_info* info, int32_t arg)
{
    return info->cb(arg, info->ud);
}

// Same, but the struct is passed BY VALUE (wgpu passes several descriptor structs by value). Verifies
// the C struct-by-value ABI carries the `{ fn, bound }` field correctly.
int32_t rf_test_invoke_cbinfo_byval(rf_cb_info info, int32_t arg)
{
    return info.cb(arg, info.ud);
}
