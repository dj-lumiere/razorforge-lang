// runtime.c - Minimal operations for v0.0.1

// Memory operations
#ifdef _WIN32
#include <windows.h>
#endif
#include "types.h"
#include <string.h>

void rf_runtime_init()
{
#ifdef _WIN32
    SetConsoleCP(65001);
    SetConsoleOutputCP(65001);
#endif
}

// ============================================================================
// DynamicSlice operations (memory functions are in memory.c)
// ============================================================================

// ============================================================================
// Text<letter8> / cstr conversion
// ============================================================================

// Convert cstr to Text<letter8> (List<letter8>)
// This creates a Text that wraps the cstr pointer - does NOT copy memory
rf_Text8 rf_text8_from_cstr(const char* cstr)
{
    rf_Text8 text;
    rf_u64 len = (rf_u64)strlen(cstr);

    text.data.starting_address = (rf_uaddr)cstr;
    text.data.allocated_bytes = len;
    text.count = len;
    text.capacity = len;

    return text;
}

// Convert Text<letter8> to cstr
// Returns pointer to the underlying data (must be null-terminated!)
const char* rf_cstr_from_text8(rf_Text8 text)
{
    return (const char*)text.data.starting_address;
}

// Get length of cstr
rf_uaddr rf_strlen(const char* cstr)
{
    return (rf_uaddr)strlen(cstr);
}
rf_uaddr rf_strcpy(char* dest, const char* src)
{
    return (rf_uaddr)strcpy(dest, src);
}
rf_s32 rf_strcmp(const char* s1, const char* s2)
{
    return (rf_s32)strcmp(s1, s2);
}

// ============================================================================
// Console I/O Operations
// ============================================================================

// Print C string to stdout (null-terminated)
void rf_console_print_cstr(const char* str) { printf("%s", str); }
void rf_console_print_line_cstr(const char* str) { printf("%s\n", str); }

// Aliases for text8 (same as cstr for now)
void rf_console_print_text8(const char* str) { printf("%s", str); }
void rf_console_print_line_text8(const char* str) { printf("%s\n", str); }
void rf_console_print_line_empty() { putchar('\n'); }

// Print C string to stderr (null-terminated)
void rf_console_alert_cstr(const char* str) { fprintf(stderr, "%s", str); }
void rf_console_alert_line_cstr(const char* str) { fprintf(stderr, "%s\n", str); }

// Aliases for text8 (same as cstr for now)
void rf_console_alert_text8(const char* str) { fprintf(stderr, "%s", str); }
void rf_console_alert_line_text8(const char* str) { fprintf(stderr, "%s\n", str); }
void rf_console_alert_line_empty() { fprintf(stderr, "\n"); }

// Print empty line to stdout
void rf_console_print_line() { putchar('\n'); }

// Input operations - get single character
char rf_console_get_char()
{
    return getchar();
}

// Input operations - get line (allocates buffer, returns null-terminated string)
char* rf_console_get_line()
{
    fflush(stdout);
    int c;
    // Skip leading line feeds first
    while ((c = getchar()) != EOF && (c == '\n' || c == '\r'))
    {
    }

    if (c == EOF) return NULL;

    size_t capacity = 256;
    size_t length = 0;
    char* buffer = malloc(capacity);
    if (!buffer) return NULL;
    buffer[length] = (char)c;
    length += 1;

    while ((c = getchar()) != EOF && c != '\n' && c != '\r')
    {
        if (length + 1 >= capacity)
        {
            capacity *= 2;
            char* new_buffer = realloc(buffer, capacity);
            if (!new_buffer)
            {
                free(buffer);
                return NULL;
            }
            buffer = new_buffer;
        }
        buffer[length] = (char)c;
        length += 1;
    }

    buffer[length] = '\0';

    // Handle EOF with no input
    if (c == EOF && length == 0)
    {
        free(buffer);
        return NULL;
    }

    return buffer;
}

// Input operations - get word (whitespace-delimited)
char* rf_console_get_word()
{
    fflush(stdout);
    int c;
    // Skip leading whitespaces first
    while ((c = getchar()) != EOF && (c == ' ' || c == '\t' || c == '\n' || c == '\r'))
    {
    }

    if (c == EOF) { return NULL; }

    size_t capacity = 256;
    size_t length = 0;
    char* buffer = malloc(capacity);
    if (!buffer) { return NULL; }
    buffer[length] = (char)c;
    length += 1;

    while ((c = getchar()) != EOF && c != '\n' && c != '\r' && c != '\t' && c != ' ')
    {
        if (length + 1 >= capacity)
        {
            capacity *= 2;
            char* new_buffer = realloc(buffer, capacity);
            if (!new_buffer)
            {
                free(buffer);
                return NULL;
            }
            buffer = new_buffer;
        }
        buffer[length] = (char)c;
        length += 1;
    }

    buffer[length] = '\0';

    // Handle EOF with no input
    if (c == EOF && length == 0)
    {
        free(buffer);
        return NULL;
    }

    return buffer;
}

// Input operations - get exactly n characters
char* rf_console_get_letters(rf_uaddr count)
{
    fflush(stdout);
    char* buffer = malloc(count + 1);
    if (!buffer) return NULL;

    size_t read = fread(buffer, 1, count, stdin);
    buffer[read] = '\0';
    return buffer;
}

// Input operations - get all input until EOF
char* rf_console_get_all()
{
    fflush(stdout);
    size_t capacity = 1024;
    size_t length = 0;
    char* buffer = malloc(capacity);
    if (!buffer) return NULL;

    int c;
    while ((c = getchar()) != EOF)
    {
        if (length + 1 >= capacity)
        {
            capacity *= 2;
            char* new_buffer = realloc(buffer, capacity);
            if (!new_buffer)
            {
                free(buffer);
                return NULL;
            }
            buffer = new_buffer;
        }
        buffer[length] = (char)c;
        length += 1;
    }
    buffer[length] = '\0';
    return buffer;
}

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
typedef struct
{
    void* data; // Pointer to inner data
#ifdef _WIN32
    CRITICAL_SECTION mutex;
    SRWLOCK rwlock;
#else
    pthread_mutex_t mutex;
    pthread_rwlock_t rwlock;
#endif
    rf_u32 ref_count; // Reference count (atomic in real impl)
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
