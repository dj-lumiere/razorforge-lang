using Compilers.Shared.AST;

namespace Compilers.Shared.CodeGen;

public partial class LLVMCodeGenerator
{
    // Type declarations
    /// <summary>
    /// Generates LLVM IR for entity (class) declarations.
    /// For generic entities, stores the template for later instantiation.
    /// For non-generic entities, generates the struct type definition immediately.
    /// </summary>
    public string VisitClassDeclaration(ClassDeclaration node)
    {
        // Check if this is a generic entity
        if (node.GenericParameters != null && node.GenericParameters.Count > 0)
        {
            // Store the template for later instantiation
            _genericEntityTemplates[key: node.Name] = node;
            _output.AppendLine(
                handler:
                $"; Generic entity template: {node.Name}<{string.Join(separator: ", ", values: node.GenericParameters)}>");
            return "";
        }

        // Non-generic entity - generate type definition
        return GenerateEntityType(node: node, typeSubstitutions: null, mangledName: null);
    }

    /// <summary>
    /// Generates LLVM IR for record (struct) declarations.
    /// For generic records, stores the template for later instantiation.
    /// For non-generic records, generates the struct type definition immediately.
    /// </summary>
    public string VisitStructDeclaration(StructDeclaration node)
    {
        // Check if this is a generic record
        if (node.GenericParameters != null && node.GenericParameters.Count > 0)
        {
            // Store the template for later instantiation
            _genericRecordTemplates[key: node.Name] = node;
            _output.AppendLine(
                handler:
                $"; Generic record template: {node.Name}<{string.Join(separator: ", ", values: node.GenericParameters)}>");
            return "";
        }

        // Non-generic record - generate type definition
        return GenerateRecordType(node: node, typeSubstitutions: null, mangledName: null);
    }

    /// <summary>
    /// Generates LLVM struct type definition for an entity (class).
    /// </summary>
    private string GenerateEntityType(ClassDeclaration node,
        Dictionary<string, string>? typeSubstitutions, string? mangledName)
    {
        string typeName = mangledName ?? node.Name;

        // Avoid emitting the same type twice
        if (_emittedTypes.Contains(item: typeName))
        {
            return "";
        }

        _emittedTypes.Add(item: typeName);

        // Collect field types
        var fieldTypes = new List<string>();
        var fieldNames = new List<string>(); // For comments

        foreach (Declaration member in node.Members)
        {
            if (member is VariableDeclaration field)
            {
                string fieldType = field.Type != null
                    ? MapTypeWithSubstitution(typeName: field.Type.Name,
                        substitutions: typeSubstitutions)
                    : "i32";
                fieldTypes.Add(item: fieldType);
                fieldNames.Add(item: field.Name);
            }
        }

        // Generate LLVM struct type
        string fieldList = string.Join(separator: ", ", values: fieldTypes);
        _output.AppendLine(handler: $"; Entity type: {typeName}");
        _output.AppendLine(handler: $"%{typeName} = type {{ {fieldList} }}");

        // Add comment with field names for debugging
        if (fieldNames.Count > 0)
        {
            _output.AppendLine(
                handler:
                $"; Fields: {string.Join(separator: ", ", values: fieldNames.Select(selector: (n, i) => $"{n}: {fieldTypes[index: i]}"))}");
        }

        _output.AppendLine();

        return "";
    }

    /// <summary>
    /// Generates LLVM struct type definition for a record (struct).
    /// </summary>
    private string GenerateRecordType(StructDeclaration node,
        Dictionary<string, string>? typeSubstitutions, string? mangledName)
    {
        string typeName = mangledName ?? node.Name;

        // Avoid emitting the same type twice
        if (_emittedTypes.Contains(item: typeName))
        {
            return "";
        }

        _emittedTypes.Add(item: typeName);

        // Collect field types
        var fieldTypes = new List<string>();
        var fieldNames = new List<string>(); // For comments

        foreach (Declaration member in node.Members)
        {
            if (member is VariableDeclaration field)
            {
                string fieldType = field.Type != null
                    ? MapTypeWithSubstitution(typeName: field.Type.Name,
                        substitutions: typeSubstitutions)
                    : "i32";
                fieldTypes.Add(item: fieldType);
                fieldNames.Add(item: field.Name);
            }
        }

        // Generate LLVM struct type
        string fieldList = string.Join(separator: ", ", values: fieldTypes);
        _output.AppendLine(handler: $"; Record type: {typeName}");
        _output.AppendLine(handler: $"%{typeName} = type {{ {fieldList} }}");

        // Add comment with field names for debugging
        if (fieldNames.Count > 0)
        {
            _output.AppendLine(
                handler:
                $"; Fields: {string.Join(separator: ", ", values: fieldNames.Select(selector: (n, i) => $"{n}: {fieldTypes[index: i]}"))}");
        }

        _output.AppendLine();

        // Track record fields for constructor detection
        var fields = new List<(string Name, string Type)>();
        for (int i = 0; i < fieldNames.Count; i++)
        {
            fields.Add(item: (fieldNames[index: i], fieldTypes[index: i]));
        }

        _recordFields[key: typeName] = fields;

        return "";
    }

    /// <summary>
    /// Instantiates a generic record with concrete type arguments.
    /// </summary>
    public string InstantiateGenericRecord(string recordName, List<string> typeArguments)
    {
        if (!_genericRecordTemplates.TryGetValue(key: recordName,
                value: out StructDeclaration? template))
        {
            return recordName; // Not a generic record
        }

        // Generate mangled name
        string mangledName = $"{recordName}_{string.Join(separator: "_", values: typeArguments)}";

        // Check if already instantiated
        if (_emittedTypes.Contains(item: mangledName))
        {
            return mangledName;
        }

        // Track and queue for later generation
        if (!_genericTypeInstantiations.ContainsKey(key: recordName))
        {
            _genericTypeInstantiations[key: recordName] = new List<List<string>>();
        }

        _genericTypeInstantiations[key: recordName]
           .Add(item: typeArguments);
        _pendingRecordInstantiations.Add(
            item: $"{recordName}|{string.Join(separator: ",", values: typeArguments)}");

        return mangledName;
    }

    /// <summary>
    /// Instantiates a generic entity with concrete type arguments.
    /// </summary>
    public string InstantiateGenericEntity(string entityName, List<string> typeArguments)
    {
        if (!_genericEntityTemplates.TryGetValue(key: entityName,
                value: out ClassDeclaration? template))
        {
            return entityName; // Not a generic entity
        }

        // Generate mangled name
        string mangledName = $"{entityName}_{string.Join(separator: "_", values: typeArguments)}";

        // Check if already instantiated
        if (_emittedTypes.Contains(item: mangledName))
        {
            return mangledName;
        }

        // Track and queue for later generation
        if (!_genericTypeInstantiations.ContainsKey(key: entityName))
        {
            _genericTypeInstantiations[key: entityName] = new List<List<string>>();
        }

        _genericTypeInstantiations[key: entityName]
           .Add(item: typeArguments);
        _pendingEntityInstantiations.Add(
            item: $"{entityName}|{string.Join(separator: ",", values: typeArguments)}");

        return mangledName;
    }

    /// <summary>
    /// Generates all pending generic type instantiations.
    /// Should be called after all program code is generated.
    /// </summary>
    private void GeneratePendingTypeInstantiations()
    {
        // Process pending record instantiations
        foreach (string pending in _pendingRecordInstantiations)
        {
            string[] parts = pending.Split(separator: '|');
            string recordName = parts[0];
            var typeArguments = parts[1]
                               .Split(separator: ',')
                               .ToList();

            if (!_genericRecordTemplates.TryGetValue(key: recordName,
                    value: out StructDeclaration? template))
            {
                continue;
            }

            // Create type substitution map
            var substitutions = new Dictionary<string, string>();
            for (int i = 0;
                 i < Math.Min(val1: template.GenericParameters!.Count, val2: typeArguments.Count);
                 i++)
            {
                substitutions[key: template.GenericParameters[index: i]] = typeArguments[index: i];
            }

            // Generate mangled name
            string mangledName =
                $"{recordName}_{string.Join(separator: "_", values: typeArguments)}";

            // Generate the instantiated record type
            GenerateRecordType(node: template,
                typeSubstitutions: substitutions,
                mangledName: mangledName);
        }

        _pendingRecordInstantiations.Clear();

        // Process pending entity instantiations
        foreach (string pending in _pendingEntityInstantiations)
        {
            string[] parts = pending.Split(separator: '|');
            string entityName = parts[0];
            var typeArguments = parts[1]
                               .Split(separator: ',')
                               .ToList();

            if (!_genericEntityTemplates.TryGetValue(key: entityName,
                    value: out ClassDeclaration? template))
            {
                continue;
            }

            // Create type substitution map
            var substitutions = new Dictionary<string, string>();
            for (int i = 0;
                 i < Math.Min(val1: template.GenericParameters!.Count, val2: typeArguments.Count);
                 i++)
            {
                substitutions[key: template.GenericParameters[index: i]] = typeArguments[index: i];
            }

            // Generate mangled name
            string mangledName =
                $"{entityName}_{string.Join(separator: "_", values: typeArguments)}";

            // Generate the instantiated entity type
            GenerateEntityType(node: template,
                typeSubstitutions: substitutions,
                mangledName: mangledName);
        }

        _pendingEntityInstantiations.Clear();
    }
    public string VisitMenuDeclaration(MenuDeclaration node)
    {
        return "";
    }
    public string VisitVariantDeclaration(VariantDeclaration node)
    {
        return "";
    }
    public string VisitFeatureDeclaration(FeatureDeclaration node)
    {
        return "";
    }
    public string VisitImportDeclaration(ImportDeclaration node)
    {
        return "";
    }
    public string VisitNamespaceDeclaration(NamespaceDeclaration node)
    {
        // Namespace declarations are handled at a higher level for symbol resolution
        return "";
    }
    public string VisitRedefinitionDeclaration(RedefinitionDeclaration node)
    {
        return "";
    }
    public string VisitUsingDeclaration(UsingDeclaration node)
    {
        return "";
    }
    public string VisitImplementationDeclaration(ImplementationDeclaration node)
    {
        return "";
    }
}
