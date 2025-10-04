# Simple Cake program to test compilation
import stdlib/core

recipe greet(name: Text) -> Text:
    return f"Hello, {name}!"

recipe main():
    let message = greet("World")
    display_line(message)
