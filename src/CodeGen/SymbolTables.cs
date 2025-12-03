namespace Compilers.Shared.CodeGen;

/// <summary>
/// Manages compile-time lookup tables for runtime error reporting.
/// These tables are embedded in the output binary and accessed via intrinsics.
/// </summary>
public class SymbolTables
{
    private readonly Dictionary<string, uint> _fileToId = new();
    private readonly List<string> _idToFile = [];

    private readonly Dictionary<string, uint> _routineToId = new();
    private readonly List<string> _idToRoutine = [];

    private readonly Dictionary<string, uint> _typeToId = new();
    private readonly List<string> _idToType = [];

    /// <summary>
    /// Registers a source file and returns its ID.
    /// Returns existing ID if file was already registered.
    /// </summary>
    /// <param name="filePath">The source file path</param>
    /// <returns>The file ID (u32)</returns>
    public uint RegisterFile(string filePath)
    {
        if (_fileToId.TryGetValue(key: filePath, value: out uint existingId))
        {
            return existingId;
        }

        uint id = (uint)_idToFile.Count;
        _fileToId[key: filePath] = id;
        _idToFile.Add(item: filePath);
        return id;
    }

    /// <summary>
    /// Registers a routine and returns its ID.
    /// Routine names should include generic parameters, e.g., "List&lt;s32&gt;.push".
    /// Returns existing ID if routine was already registered.
    /// </summary>
    /// <param name="routineName">The fully qualified routine name</param>
    /// <returns>The routine ID (u32)</returns>
    public uint RegisterRoutine(string routineName)
    {
        if (_routineToId.TryGetValue(key: routineName, value: out uint existingId))
        {
            return existingId;
        }

        uint id = (uint)_idToRoutine.Count;
        _routineToId[key: routineName] = id;
        _idToRoutine.Add(item: routineName);
        return id;
    }

    /// <summary>
    /// Registers a type (record/entity/resident) and returns its ID.
    /// Type names should include generic parameters, e.g., "List&lt;s32&gt;".
    /// Returns existing ID if type was already registered.
    /// </summary>
    /// <param name="typeName">The fully qualified type name</param>
    /// <returns>The type ID (u32)</returns>
    public uint RegisterType(string typeName)
    {
        if (_typeToId.TryGetValue(key: typeName, value: out uint existingId))
        {
            return existingId;
        }

        uint id = (uint)_idToType.Count;
        _typeToId[key: typeName] = id;
        _idToType.Add(item: typeName);
        return id;
    }

    /// <summary>
    /// Gets the file path for a given file ID.
    /// </summary>
    public string GetFileName(uint fileId)
    {
        if (fileId >= _idToFile.Count)
        {
            return $"<unknown file:{fileId}>";
        }

        return _idToFile[index: (int)fileId];
    }

    /// <summary>
    /// Gets the routine name for a given routine ID.
    /// </summary>
    public string GetRoutineName(uint routineId)
    {
        if (routineId >= _idToRoutine.Count)
        {
            return $"<unknown routine:{routineId}>";
        }

        return _idToRoutine[index: (int)routineId];
    }

    /// <summary>
    /// Gets the type name for a given type ID.
    /// </summary>
    public string GetTypeName(uint typeId)
    {
        if (typeId >= _idToType.Count)
        {
            return $"<unknown type:{typeId}>";
        }

        return _idToType[index: (int)typeId];
    }

    /// <summary>
    /// Gets all registered files as an ordered list.
    /// Index corresponds to file ID.
    /// </summary>
    public IReadOnlyList<string> Files => _idToFile;

    /// <summary>
    /// Gets all registered routines as an ordered list.
    /// Index corresponds to routine ID.
    /// </summary>
    public IReadOnlyList<string> Routines => _idToRoutine;

    /// <summary>
    /// Gets all registered types as an ordered list.
    /// Index corresponds to type ID.
    /// </summary>
    public IReadOnlyList<string> Types => _idToType;

    /// <summary>
    /// Total number of registered files.
    /// </summary>
    public int FileCount => _idToFile.Count;

    /// <summary>
    /// Total number of registered routines.
    /// </summary>
    public int RoutineCount => _idToRoutine.Count;

    /// <summary>
    /// Total number of registered types.
    /// </summary>
    public int TypeCount => _idToType.Count;
}
