#include <stdint.h>
#include <stdbool.h>
#include <stdio.h>

// RazorForge i8 type implementation
typedef int8_t rf_i8;

// Overflow-safe arithmetic operations
typedef struct
{
    rf_i8 value;
    bool overflow;
} rf_i8_result;

// Wrapping operations (modulo arithmetic)
rf_i8 rf_i8_add_wrap(rf_i8 a, rf_i8 b)
{
    return (rf_i8)((uint8_t)a + (uint8_t)b);
}

rf_i8 rf_i8_sub_wrap(rf_i8 a, rf_i8 b)
{
    return (rf_i8)((uint8_t)a - (uint8_t)b);
}

rf_i8 rf_i8_mul_wrap(rf_i8 a, rf_i8 b)
{
    return (rf_i8)((uint8_t)a * (uint8_t)b);
}

// Saturating operations (clamp to min/max)
rf_i8 rf_i8_add_saturate(rf_i8 a, rf_i8 b)
{
    int16_t result = (int16_t)a + (int16_t)b;
    if (result > INT8_MAX) return INT8_MAX;
    if (result < INT8_MIN) return INT8_MIN;
    return (rf_i8)result;
}

rf_i8 rf_i8_sub_saturate(rf_i8 a, rf_i8 b)
{
    int16_t result = (int16_t)a - (int16_t)b;
    if (result > INT8_MAX) return INT8_MAX;
    if (result < INT8_MIN) return INT8_MIN;
    return (rf_i8)result;
}

// Checked operations (return overflow flag)
rf_i8_result rf_i8_add_checked(rf_i8 a, rf_i8 b)
{
    rf_i8_result res;
    int16_t temp = (int16_t)a + (int16_t)b;
    res.overflow = (temp > INT8_MAX || temp < INT8_MIN);
    res.value = (rf_i8)temp;
    return res;
}

rf_i8_result rf_i8_sub_checked(rf_i8 a, rf_i8 b)
{
    rf_i8_result res;
    int16_t temp = (int16_t)a - (int16_t)b;
    res.overflow = (temp > INT8_MAX || temp < INT8_MIN);
    res.value = (rf_i8)temp;
    return res;
}

rf_i8_result rf_i8_mul_checked(rf_i8 a, rf_i8 b)
{
    rf_i8_result res;
    int16_t temp = (int16_t)a * (int16_t)b;
    res.overflow = (temp > INT8_MAX || temp < INT8_MIN);
    res.value = (rf_i8)temp;
    return res;
}

// Unchecked operations (undefined behavior on overflow - for danger mode)
rf_i8 rf_i8_add_unchecked(rf_i8 a, rf_i8 b)
{
    return a + b; // Let C handle it (UB on overflow)
}

rf_i8 rf_i8_sub_unchecked(rf_i8 a, rf_i8 b)
{
    return a - b;
}

rf_i8 rf_i8_mul_unchecked(rf_i8 a, rf_i8 b)
{
    return a * b;
}
