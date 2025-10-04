// runtime.c - Minimal operations for v0.0.1

// Memory operations
rf_MemorySlice rf_alloc(rf_usys size) {
    rf_MemorySlice slice;
    slice.data = malloc(size);
    slice.len = size;
    return slice;
}

rf_MemorySlice rf_alloc_stack(rf_usys size) {
    rf_MemorySlice slice;
    slice.data = alloca(size);
    slice.len = size;
    return slice;
}

void rf_free(rf_MemorySlice slice) {
    free(slice.data);
}

// MemorySlice operations
rf_u8 rf_slice_read_u8(rf_MemorySlice slice, rf_usys offset) {
    return slice.data[offset];
}

void rf_slice_write_u8(rf_MemorySlice slice, rf_usys offset, rf_u8 value) {
    slice.data[offset] = value;
}

rf_i32 rf_slice_read_i32(rf_MemorySlice slice, rf_usys offset) {
    return *(rf_i32*)(slice.data + offset);
}

void rf_slice_write_i32(rf_MemorySlice slice, rf_usys offset, rf_i32 value) {
    *(rf_i32*)(slice.data + offset) = value;
}

// Variant operations
rf_Variant rf_variant_new(rf_u32 tag, rf_MemorySlice data) {
    rf_Variant v;
    v.tag = tag;
    v.data = data;
    return v;
}

rf_bool rf_variant_is(rf_Variant v, rf_u32 tag) {
    return v.tag == tag;
}

// Basic I/O (no Text yet, just integers)
void rf_print_i32(rf_i32 value) {
    printf("%d\n", value);
}

rf_i32 rf_read_i32() {
    rf_i32 value;
    scanf("%d", &value);
    return value;
}