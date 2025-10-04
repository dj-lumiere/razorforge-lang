# Test file for all new Cake language features

# Redefine and using statements
redefine old_function as new_function
using System.Collections.Generic as Collections

# Chained comparisons
var x = 10
var y = 8
var z = 12

if 5 < x <= 10 < z < 15:
    display("Values are in ascending order within ranges")

# Conditional expressions (if A then B else C)
var message = if x > 5 then "big" else "small"

# Memory and time literals with Cake syntax
var memory_size = 4gb + 512mb
var duration = 30m + 45s + 500ms

# Multi-encoding text literals
var text8 = t8"Hello in 8-bit"
var text16 = t16"Hello in 16-bit"
var text32 = t32"Hello in 32-bit" 
var formatted = f"x is {x}, y is {y}"

recipe main():
    display(message)
    display(formatted)
    display("Memory: " + memory_size)
    display("Duration: " + duration)
    
    # Sweet mode features
    for i in 1 to 10 step 2:
        display(f"Sweet iteration: {i}")