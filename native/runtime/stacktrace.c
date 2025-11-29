/*
 * RazorForge Runtime - Stack Trace Support
 * Provides runtime stack frame tracking for error reporting
 */

#include <stdlib.h>
#include <string.h>
#include <stdint.h>
#include <stdbool.h>
#include <stdio.h>

// ============================================================================
// Type Definitions
// ============================================================================

typedef uint32_t rf_u32;
typedef uintptr_t rf_uaddr;

// StackFrame record - matches RazorForge layout (5 x u32 = 20 bytes)
typedef struct {
    rf_u32 file_id;
    rf_u32 routine_id;
    rf_u32 type_id;
    rf_u32 line_no;
    rf_u32 column_no;
} rf_StackFrame;

// StackTrace record - 10 frames + depth (10 x 20 + 4 = 204 bytes)
#define RF_STACK_TRACE_MAX_FRAMES 10

typedef struct {
    rf_StackFrame frames[RF_STACK_TRACE_MAX_FRAMES];
    rf_u32 depth;
} rf_StackTrace;

// MessageHandle record - 1 x uaddr = 8 bytes on 64-bit
typedef struct {
    rf_uaddr ptr;
} rf_MessageHandle;

// Error record - matches RazorForge layout
typedef struct {
    rf_MessageHandle message_handle;
    rf_StackTrace stack_trace;
    rf_u32 file_id;
    rf_u32 routine_id;
    rf_u32 line_no;
    rf_u32 column_no;
} rf_Error;

// ============================================================================
// Thread-Local Stack Storage
// ============================================================================

// Maximum runtime call stack depth for tracking
#define RF_RUNTIME_STACK_MAX 256

// Thread-local storage for the runtime call stack
#ifdef _WIN32
    #define RF_THREAD_LOCAL __declspec(thread)
#else
    #define RF_THREAD_LOCAL __thread
#endif

static RF_THREAD_LOCAL rf_StackFrame rf_runtime_stack[RF_RUNTIME_STACK_MAX];
static RF_THREAD_LOCAL rf_u32 rf_runtime_stack_depth = 0;

// ============================================================================
// Symbol Tables (set by compiler-generated code)
// ============================================================================

// Pointers to compiler-generated symbol tables
static const char** rf_file_table = NULL;
static rf_u32 rf_file_table_count = 0;

static const char** rf_routine_table = NULL;
static rf_u32 rf_routine_table_count = 0;

static const char** rf_type_table = NULL;
static rf_u32 rf_type_table_count = 0;

// Initialize symbol tables (called by compiler-generated code at startup)
void __rf_init_symbol_tables(
    const char** file_table, rf_u32 file_count,
    const char** routine_table, rf_u32 routine_count,
    const char** type_table, rf_u32 type_count)
{
    rf_file_table = file_table;
    rf_file_table_count = file_count;
    rf_routine_table = routine_table;
    rf_routine_table_count = routine_count;
    rf_type_table = type_table;
    rf_type_table_count = type_count;
}

// ============================================================================
// Stack Frame Push/Pop
// ============================================================================

// Push a stack frame at routine entry
void __rf_stack_push(rf_u32 file_id, rf_u32 routine_id, rf_u32 type_id, rf_u32 line_no, rf_u32 column_no)
{
    if (rf_runtime_stack_depth >= RF_RUNTIME_STACK_MAX)
    {
        // Stack overflow - print error and exit
        fprintf(stderr, "\nRazorForge Runtime Error: Stack overflow (depth > %d)\n", RF_RUNTIME_STACK_MAX);
        fflush(stderr);
        exit(1);
    }

    rf_StackFrame* frame = &rf_runtime_stack[rf_runtime_stack_depth];
    frame->file_id = file_id;
    frame->routine_id = routine_id;
    frame->type_id = type_id;
    frame->line_no = line_no;
    frame->column_no = column_no;

    rf_runtime_stack_depth++;
}

// Pop a stack frame at routine exit
void __rf_stack_pop(void)
{
    if (rf_runtime_stack_depth > 0)
    {
        rf_runtime_stack_depth--;
    }
}

// ============================================================================
// Stack Trace Capture
// ============================================================================

// Capture current stack into a StackTrace record
void __rf_stack_capture(rf_StackTrace* out_trace)
{
    if (!out_trace) return;

    // Copy up to RF_STACK_TRACE_MAX_FRAMES frames from the runtime stack
    rf_u32 frames_to_copy = rf_runtime_stack_depth;
    if (frames_to_copy > RF_STACK_TRACE_MAX_FRAMES)
    {
        frames_to_copy = RF_STACK_TRACE_MAX_FRAMES;
    }

    // Copy frames in reverse order (most recent first)
    for (rf_u32 i = 0; i < frames_to_copy; i++)
    {
        rf_u32 src_idx = rf_runtime_stack_depth - 1 - i;
        out_trace->frames[i] = rf_runtime_stack[src_idx];
    }

    // Zero out remaining frames
    for (rf_u32 i = frames_to_copy; i < RF_STACK_TRACE_MAX_FRAMES; i++)
    {
        memset(&out_trace->frames[i], 0, sizeof(rf_StackFrame));
    }

    out_trace->depth = frames_to_copy;
}

// ============================================================================
// Symbol Lookup
// ============================================================================

static const char* get_file_name(rf_u32 file_id)
{
    if (rf_file_table && file_id < rf_file_table_count)
    {
        return rf_file_table[file_id];
    }
    return "<unknown file>";
}

static const char* get_routine_name(rf_u32 routine_id)
{
    if (rf_routine_table && routine_id < rf_routine_table_count)
    {
        return rf_routine_table[routine_id];
    }
    return "<unknown routine>";
}

static const char* get_type_name(rf_u32 type_id)
{
    if (type_id == 0)
    {
        return NULL;  // Free function, no type
    }
    if (rf_type_table && type_id < rf_type_table_count)
    {
        return rf_type_table[type_id];
    }
    return "<unknown type>";
}

// ============================================================================
// Error Printing
// ============================================================================

// Print a single stack frame
static void print_stack_frame(const rf_StackFrame* frame, int index)
{
    const char* file = get_file_name(frame->file_id);
    const char* routine = get_routine_name(frame->routine_id);
    const char* type = get_type_name(frame->type_id);

    if (type)
    {
        fprintf(stderr, "  %d: at %s.%s (%s:%u:%u)\n",
                index, type, routine, file, frame->line_no, frame->column_no);
    }
    else
    {
        fprintf(stderr, "  %d: at %s (%s:%u:%u)\n",
                index, routine, file, frame->line_no, frame->column_no);
    }
}

// Print a full stack trace
void __rf_print_stack_trace(const rf_StackTrace* trace)
{
    if (!trace || trace->depth == 0)
    {
        fprintf(stderr, "  <no stack trace available>\n");
        return;
    }

    fprintf(stderr, "Stack trace:\n");
    for (rf_u32 i = 0; i < trace->depth; i++)
    {
        print_stack_frame(&trace->frames[i], i);
    }
}

// Print current runtime stack (for debugging)
void __rf_print_current_stack(void)
{
    if (rf_runtime_stack_depth == 0)
    {
        fprintf(stderr, "  <stack empty>\n");
        return;
    }

    fprintf(stderr, "Current stack (depth=%u):\n", rf_runtime_stack_depth);
    for (rf_u32 i = 0; i < rf_runtime_stack_depth; i++)
    {
        rf_u32 idx = rf_runtime_stack_depth - 1 - i;
        print_stack_frame(&rf_runtime_stack[idx], i);
    }
}

// ============================================================================
// Throw Functions
// ============================================================================

// Throw an error with message and captured stack trace
void __rf_throw(const char* error_type, const char* message)
{
    fprintf(stderr, "\n%s: %s\n", error_type ? error_type : "Error", message ? message : "");
    __rf_print_current_stack();
    fprintf(stderr, "\n");
    fflush(stderr);
    exit(1);
}

// Throw AbsentValueError
void __rf_throw_absent(void)
{
    __rf_throw("AbsentValueError", "Attempted to access an absent value");
}

// Throw DivisionByZeroError
void __rf_throw_division_by_zero(void)
{
    __rf_throw("DivisionByZeroError", "You tried to divide by zero, which is not allowed.");
}

// Throw IndexOutOfBoundsError
void __rf_throw_index_out_of_bounds(rf_u32 index, rf_u32 count)
{
    char buffer[128];
    snprintf(buffer, sizeof(buffer), "Index %u is out of bounds for collection with %u elements", index, count);
    __rf_throw("IndexOutOfBoundsError", buffer);
}

// Throw IntegerOverflowError with custom message
void __rf_throw_integer_overflow(const char* message)
{
    __rf_throw("IntegerOverflowError", message ? message : "An integer overflow occurred during arithmetic operation.");
}

// Throw EmptyCollectionError with operation name
void __rf_throw_empty_collection(const char* operation)
{
    char buffer[128];
    snprintf(buffer, sizeof(buffer), "Cannot %s on empty collection", operation ? operation : "perform operation");
    __rf_throw("EmptyCollectionError", buffer);
}

// Throw ElementNotFoundError
void __rf_throw_element_not_found(void)
{
    __rf_throw("ElementNotFoundError", "No matching element found");
}

// ============================================================================
// Error Record Creation
// ============================================================================

// Create an Error record with current stack trace
void __rf_create_error(
    rf_Error* out_error,
    const char* message,
    rf_u32 file_id,
    rf_u32 routine_id,
    rf_u32 line_no,
    rf_u32 column_no)
{
    if (!out_error) return;

    out_error->message_handle.ptr = (rf_uaddr)message;
    __rf_stack_capture(&out_error->stack_trace);
    out_error->file_id = file_id;
    out_error->routine_id = routine_id;
    out_error->line_no = line_no;
    out_error->column_no = column_no;
}

// Print an Error record
void __rf_print_error(const rf_Error* error)
{
    if (!error) return;

    const char* file = get_file_name(error->file_id);
    const char* routine = get_routine_name(error->routine_id);
    const char* message = (const char*)error->message_handle.ptr;

    fprintf(stderr, "\nError at %s:%u:%u in %s\n",
            file, error->line_no, error->column_no, routine);

    if (message)
    {
        fprintf(stderr, "  %s\n", message);
    }

    fprintf(stderr, "\n");
    __rf_print_stack_trace(&error->stack_trace);
}
