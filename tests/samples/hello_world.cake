import system/console/write_line
redefinition A as B

# Entities, Records, Menus, and Variants in Cake

# Entity Example - Reference type with inheritance
entity Animal:
    public(family) var species: Text
    @[public get, private set]
    var name: Text

    public recipe __create__(name: Text, species: Text) -> Animal:
        me.name = name
        me.species = species
        return me

    public recipe speak() -> Text:
        return f"{me.name} makes a sound"

entity Dog from Animal:
    public var breed: Text

    public recipe __create__(name: Text, breed: Text) -> Dog:
        parent(name, "Canine")
        me.breed = breed
        return me

    public recipe speak() -> Text:
        return f"{me.name} barks: Woof!"

# Record Example - Value type for data
record Point:
    public var x: Decimal
    public var y: Decimal

    public recipe distance_from_origin() -> Decimal:
        return (me.x * me.x + me.y * me.y).sqrt()

    public recipe translate(dx: Decimal, dy: Decimal) -> Point:
        return Point(x: me.x + dx, y: me.y + dy)

# Choice (Enum) Example - Simple enumeration
choice Color:
    Red
    Green
    Blue

    public recipe to_hex() -> Text:
        when me:
            Color.Red => return "#FF0000"
            Color.Green => return "#00FF00"
            Color.Blue => return "#0000FF"

# Chimera Example - Tagged union
chimera Result<T>:
    Success(T)
    Error(Error)

    public recipe unwrap() -> T:
        when me:
            is Error error: crash(f"Unwrap failed: {error}")
            _: return _

    public recipe is_success() -> Bool:
        when me:
            is Error _: return false
            _: return true

chimera Maybe<T>:
    Valid(T)
    Invalid

    # This will be overridden as ?: operator.
    public recipe unwrap_or(default: T) -> T:
        when me:
            is Invalid: return default
            _: return _

# Feature Example - Interface/trait
feature Drawable:
    recipe draw() -> Text

# Implementing feature for a record
record Circle:
    public var radius: Decimal
    public var center: Point

    public recipe area() -> Decimal:
        return 3.14159 * me.radius * me.radius

# Making Circle drawable
Circle follows Drawable:
    recipe draw() -> Text:
        return f"Circle at ({me.center.x}, {me.center.y}) with radius {me.radius}"

# Usage example
recipe main():
    # Entity usage
    var dog = Dog("Buddy", "Golden Retriever")
    display(dog.speak())

    # Record usage
    var point = Point(x: 3.0, y: 4.0)
    var distance = point.distance_from_origin()
    display(f"Distance: {distance}")

    # Option usage
    var color = Color.Red
    display(f"Color: {color.to_hex()}")

    # Variant usage
    var success: Result<Integer> = 42
    var failure: Result<Integer> = Error("Something went wrong")

    when success:
        is Error error: display(f"Error: {error}")
        _: display(f"Got value: {_}")

    # Feature usage
    var circle = Circle(radius: 5.0, center: Point(x: 0.0, y: 0.0))
    display(circle.draw())

    for i in 1 to 10 step 2:
        display(i)

    var t8 = t8"aa"
    var t16 = t16"aa"
    var t32 = "aa"
    var t322 = t32"aa"
    var l8 = l8'\''
    var l16 = l16'a'
    var l32 = 'a'
