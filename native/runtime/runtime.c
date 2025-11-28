// runtime.c - Minimal operations for v0.0.1

// Memory operations
rf_MemorySlice rf_alloc(rf_usys size)
{
    rf_MemorySlice slice;
    slice.data = malloc(size);
    slice.len = size;
    return slice;
}

rf_MemorySlice rf_alloc_stack(rf_usys size)
{
    rf_MemorySlice slice;
    slice.data = alloca(size);
    slice.len = size;
    return slice;
}

void rf_free(rf_MemorySlice slice)
{
    free(slice.data);
}

// MemorySlice operations
rf_u8 rf_slice_read_u8(rf_MemorySlice slice, rf_usys offset)
{
    return slice.data[offset];
}

void rf_slice_write_u8(rf_MemorySlice slice, rf_usys offset, rf_u8 value)
{
    slice.data[offset] = value;
}

rf_i32 rf_slice_read_i32(rf_MemorySlice slice, rf_usys offset)
{
    return *(rf_i32*)(slice.data + offset);
}

void rf_slice_write_i32(rf_MemorySlice slice, rf_usys offset, rf_i32 value)
{
    *(rf_i32*)(slice.data + offset) = value;
}

// Variant operations
rf_Variant rf_variant_new(rf_u32 tag, rf_MemorySlice data)
{
    rf_Variant v;
    v.tag = tag;
    v.data = data;
    return v;
}

rf_bool rf_variant_is(rf_Variant v, rf_u32 tag)
{
    return v.tag == tag;
}

// Basic I/O (no Text yet, just integers)
void rf_print_i32(rf_i32 value)
{
    printf("%d\n", value);
}

rf_i32 rf_read_i32()
{
    rf_i32 value;
    scanf("%d", &value);
    return value;
}

// ============================================================================
// Console I/O Operations
// ============================================================================

// Output operations - print without newline
void rf_console_print_i8(rf_i8 value) { printf("%d", value); }
void rf_console_print_i16(rf_i16 value) { printf("%d", value); }
void rf_console_print_i32(rf_i32 value) { printf("%d", value); }
void rf_console_print_i64(rf_i64 value) { printf("%lld", (long long)value); }

void rf_console_print_u8(rf_u8 value) { printf("%u", value); }
void rf_console_print_u16(rf_u16 value) { printf("%u", value); }
void rf_console_print_u32(rf_u32 value) { printf("%u", value); }
void rf_console_print_u64(rf_u64 value) { printf("%llu", (unsigned long long)value); }

void rf_console_print_f32(rf_f32 value) { printf("%g", value); }
void rf_console_print_f64(rf_f64 value) { printf("%g", value); }

void rf_console_print_bool(rf_bool value) { printf("%s", value ? "true" : "false"); }
void rf_console_print_char(char c) { putchar(c); }

// Print C string (null-terminated)
void rf_console_print_cstr(const char* str) { printf("%s", str); }

// Output operations - print with newline
void rf_console_print_line_i8(rf_i8 value) { printf("%d\n", value); }
void rf_console_print_line_i16(rf_i16 value) { printf("%d\n", value); }
void rf_console_print_line_i32(rf_i32 value) { printf("%d\n", value); }
void rf_console_print_line_i64(rf_i64 value) { printf("%lld\n", (long long)value); }

void rf_console_print_line_u8(rf_u8 value) { printf("%u\n", value); }
void rf_console_print_line_u16(rf_u16 value) { printf("%u\n", value); }
void rf_console_print_line_u32(rf_u32 value) { printf("%u\n", value); }
void rf_console_print_line_u64(rf_u64 value) { printf("%llu\n", (unsigned long long)value); }

void rf_console_print_line_f32(rf_f32 value) { printf("%g\n", value); }
void rf_console_print_line_f64(rf_f64 value) { printf("%g\n", value); }

void rf_console_print_line_bool(rf_bool value) { printf("%s\n", value ? "true" : "false"); }
void rf_console_print_line_cstr(const char* str) { printf("%s\n", str); }

// Print empty line
void rf_console_print_line() { putchar('\n'); }

// Input operations - read single character
char rf_console_read_char()
{
    return getchar();
}

// Input operations - read line (allocates buffer, returns null-terminated string)
char* rf_console_read_line()
{
    char* buffer = malloc(1024);  // Initial buffer
    if (!buffer) return NULL;

    if (fgets(buffer, 1024, stdin))
    {
        // Remove trailing newline if present
        size_t len = strlen(buffer);
        if (len > 0 && buffer[len - 1] == '\n')
            buffer[len - 1] = '\0';
        return buffer;
    }

    free(buffer);
    return NULL;
}

// Input operations - read word (whitespace-delimited)
char* rf_console_read_word()
{
    char* buffer = malloc(256);
    if (!buffer) return NULL;

    if (scanf("%255s", buffer) == 1)
        return buffer;

    free(buffer);
    return NULL;
}

// Input operations - read specific types
rf_i8 rf_console_read_i8() { rf_i32 v; scanf("%d", &v); return (rf_i8)v; }
rf_i16 rf_console_read_i16() { rf_i32 v; scanf("%d", &v); return (rf_i16)v; }
rf_i32 rf_console_read_i32() { rf_i32 v; scanf("%d", &v); return v; }
rf_i64 rf_console_read_i64() { rf_i64 v; scanf("%lld", &v); return v; }

rf_u8 rf_console_read_u8() { rf_u32 v; scanf("%u", &v); return (rf_u8)v; }
rf_u16 rf_console_read_u16() { rf_u32 v; scanf("%u", &v); return (rf_u16)v; }
rf_u32 rf_console_read_u32() { rf_u32 v; scanf("%u", &v); return v; }
rf_u64 rf_console_read_u64() { rf_u64 v; scanf("%llu", &v); return v; }

rf_f32 rf_console_read_f32() { rf_f32 v; scanf("%f", &v); return v; }
rf_f64 rf_console_read_f64() { rf_f64 v; scanf("%lf", &v); return v; }

// Flush output
void rf_console_flush()
{
    fflush(stdout);
}

// Clear screen (platform-specific)
void rf_console_clear()
{
#ifdef _WIN32
    system("cls");
#else
    system("clear");
#endif
}

// ============================================================================
// Threading Primitives for Shared<T, Policy>
// ============================================================================
// These are placeholder implementations for single-threaded use.
// Full multi-threaded support requires platform-specific threading libraries.

#ifdef _WIN32
#include <windows.h>
#else
#include <pthread.h>
#endif

// Shared<T, Policy> structure (simplified)
// In real implementation, this would be more complex with proper memory layout
typedef struct {
    void* data;           // Pointer to inner data
#ifdef _WIN32
    CRITICAL_SECTION mutex;
    SRWLOCK rwlock;
#else
    pthread_mutex_t mutex;
    pthread_rwlock_t rwlock;
#endif
    rf_u32 ref_count;     // Reference count (atomic in real impl)
} rf_Shared;

// Mutex lock - acquire exclusive access
void* razorforge_mutex_lock(void* shared_ptr)
{
    rf_Shared* shared = (rf_Shared*)shared_ptr;
#ifdef _WIN32
    EnterCriticalSection(&shared->mutex);
#else
    pthread_mutex_lock(&shared->mutex);
#endif
    return shared->data;
}

// Mutex unlock - release exclusive access
void razorforge_mutex_unlock(void* shared_ptr)
{
    rf_Shared* shared = (rf_Shared*)shared_ptr;
#ifdef _WIN32
    LeaveCriticalSection(&shared->mutex);
#else
    pthread_mutex_unlock(&shared->mutex);
#endif
}

// RwLock read lock - acquire shared read access
void* razorforge_rwlock_read_lock(void* shared_ptr)
{
    rf_Shared* shared = (rf_Shared*)shared_ptr;
#ifdef _WIN32
    AcquireSRWLockShared(&shared->rwlock);
#else
    pthread_rwlock_rdlock(&shared->rwlock);
#endif
    return shared->data;
}

// RwLock read unlock - release shared read access
void razorforge_rwlock_read_unlock(void* shared_ptr)
{
    rf_Shared* shared = (rf_Shared*)shared_ptr;
#ifdef _WIN32
    ReleaseSRWLockShared(&shared->rwlock);
#else
    pthread_rwlock_unlock(&shared->rwlock);
#endif
}

// RwLock write lock - acquire exclusive write access
void* razorforge_rwlock_write_lock(void* shared_ptr)
{
    rf_Shared* shared = (rf_Shared*)shared_ptr;
#ifdef _WIN32
    AcquireSRWLockExclusive(&shared->rwlock);
#else
    pthread_rwlock_wrlock(&shared->rwlock);
#endif
    return shared->data;
}

// RwLock write unlock - release exclusive write access
void razorforge_rwlock_write_unlock(void* shared_ptr)
{
    rf_Shared* shared = (rf_Shared*)shared_ptr;
#ifdef _WIN32
    ReleaseSRWLockExclusive(&shared->rwlock);
#else
    pthread_rwlock_unlock(&shared->rwlock);
#endif
}
