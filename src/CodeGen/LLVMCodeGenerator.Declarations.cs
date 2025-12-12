using Compilers.Shared.AST;
using Compilers.Shared.Errors;

namespace Compilers.Shared.CodeGen;

public partial class LLVMCodeGenerator
{
    /// <summary>
    /// Gets the full type name including generic arguments from a TypeExpression.
    /// For example, TypeExpression("Text", [TypeExpression("letter32")]) becomes "Text&lt;letter32&gt;"
    /// </summary>
    private string GetFullTypeName(TypeExpression type)
    {
        if (type.GenericArguments == null || type.GenericArguments.Count == 0)
        {
            return type.Name;
        }

        var argNames = type.GenericArguments.Select(selector: GetFullTypeName);
        return $"{type.Name}<{string.Join(separator: ", ", values: argNames)}>";
    }

    /// <summary>
    /// Checks if a type expression potentially references a generic type parameter.
    /// This is used to skip functions with uninstantiated generic types during core prelude loading.
    /// </summary>
    /// <param name="type">The type expression to check</param>
    /// <returns>True if the type might be a generic type parameter (single uppercase letter)</returns>
    private bool IsPotentiallyGenericType(TypeExpression type)
    {
        // Common generic type parameter names are single uppercase letters: T, I, K, V, etc.
        return type.Name.Length == 1 && char.IsUpper(type.Name[0]);
    }

    /// <summary>
    /// Sanitizes a type name for use in mangled names by replacing angle brackets and commas.
    /// For example, "BackIndex&lt;uaddr&gt;" becomes "BackIndex_uaddr"
    /// </summary>
    private static string SanitizeTypeNameForMangling(string typeName)
    {
        return typeName
            .Replace(oldValue: "<", newValue: "_")
            .Replace(oldValue: ">", newValue: "")
            .Replace(oldValue: ", ", newValue: "_")
            .Replace(oldValue: ",", newValue: "_");
    }

    // Type declarations
    /// <summary>
    /// Generates LLVM IR for entity (class) declarations.
    /// For generic entities, stores the template for later instantiation.
    /// For non-generic entities, generates the struct type definition immediately.
    /// </summary>
    public string VisitEntityDeclaration(EntityDeclaration node)
    {
        UpdateLocation(node.Location);
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
    public string VisitRecordDeclaration(RecordDeclaration node)
    {
        UpdateLocation(node.Location);
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
    private string GenerateEntityType(EntityDeclaration node,
        Dictionary<string, string>? typeSubstitutions, string? mangledName)
    {
        string typeName = mangledName ?? node.Name;

        // Avoid emitting the same type twice
        if (!_emittedTypes.Add(item: typeName))
        {
            return "";
        }

        // Collect field types
        var fieldTypes = new List<string>();
        var fieldNames = new List<string>(); // For comments

        foreach (Declaration member in node.Members)
        {
            if (member is not VariableDeclaration field)
            {
                continue;
            }

            if (field.Type == null)
            {
                throw CodeGenError.TypeResolutionFailed(
                    typeName: field.Name,
                    context: $"entity '{typeName}' field must have a type annotation",
                    file: _currentFileName,
                    line: field.Location.Line,
                    column: field.Location.Column,
                    position: field.Location.Position);
            }
            string fieldType = MapTypeWithSubstitution(typeName: GetFullTypeName(type: field.Type),
                substitutions: typeSubstitutions);
            fieldTypes.Add(item: fieldType);
            fieldNames.Add(item: field.Name);
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

        // Track entity fields for constructor detection (LLVM types) - same as records
        var fields = new List<(string Name, string Type)>();
        for (int i = 0; i < fieldNames.Count; i++)
        {
            fields.Add(item: (fieldNames[index: i], fieldTypes[index: i]));
        }
        _recordFields[key: typeName] = fields;

        // Track entity fields with RazorForge types for method lookup
        var rfFields = new List<(string Name, string RfType)>();
        foreach (Declaration member in node.Members)
        {
            if (member is not VariableDeclaration field)
            {
                continue;
            }

            if (field.Type != null)
            {
                string rfFieldType = GetFullTypeName(type: field.Type);
                // Apply type substitutions to get the concrete RazorForge type
                if (typeSubstitutions != null)
                {
                    foreach (var kvp in typeSubstitutions)
                    {
                        rfFieldType = System.Text.RegularExpressions.Regex.Replace(
                            rfFieldType,
                            $"\\b{System.Text.RegularExpressions.Regex.Escape(kvp.Key)}\\b",
                            kvp.Value);
                    }
                }
                rfFields.Add(item: (field.Name, rfFieldType));
            }
        }
        _recordFieldsRfTypes[key: typeName] = rfFields;

        return "";
    }

    /// <summary>
    /// Generates LLVM struct type definition for a record (struct).
    /// </summary>
    private string GenerateRecordType(RecordDeclaration node,
        Dictionary<string, string>? typeSubstitutions, string? mangledName)
    {
        string typeName = mangledName ?? node.Name;

        // Avoid emitting the same type twice
        if (!_emittedTypes.Add(item: typeName))
        {
            return "";
        }

        // Collect field types
        var fieldTypes = new List<string>();
        var fieldNames = new List<string>(); // For comments

        foreach (Declaration member in node.Members)
        {
            if (member is not VariableDeclaration field)
            {
                continue;
            }

            if (field.Type == null)
            {
                throw CodeGenError.TypeResolutionFailed(
                    typeName: field.Name,
                    context: $"record '{typeName}' field must have a type annotation",
                    file: _currentFileName,
                    line: field.Location.Line,
                    column: field.Location.Column,
                    position: field.Location.Position);
            }
            string fieldType = MapTypeWithSubstitution(typeName: GetFullTypeName(type: field.Type),
                substitutions: typeSubstitutions);
            fieldTypes.Add(item: fieldType);
            fieldNames.Add(item: field.Name);
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

        // Track record fields for constructor detection (LLVM types)
        var fields = new List<(string Name, string Type)>();
        for (int i = 0; i < fieldNames.Count; i++)
        {
            fields.Add(item: (fieldNames[index: i], fieldTypes[index: i]));
        }
        _recordFields[key: typeName] = fields;

        // Track record fields with RazorForge types for method lookup
        var rfFields = new List<(string Name, string RfType)>();
        foreach (Declaration member in node.Members)
        {
            if (member is not VariableDeclaration field)
            {
                continue;
            }

            if (field.Type != null)
            {
                string rfFieldType = GetFullTypeName(type: field.Type);
                // Apply type substitutions to get the concrete RazorForge type
                if (typeSubstitutions != null && rfFieldType.Contains('<') && rfFieldType.Contains('>'))
                {
                    foreach (var kvp in typeSubstitutions)
                    {
                        rfFieldType = System.Text.RegularExpressions.Regex.Replace(
                            rfFieldType,
                            $@"\b{kvp.Key}\b",
                            kvp.Value);
                    }
                }
                else if (typeSubstitutions != null && typeSubstitutions.TryGetValue(rfFieldType, out string? substituted))
                {
                    rfFieldType = substituted;
                }
                rfFields.Add((field.Name, rfFieldType));
            }
        }
        _recordFieldsRfTypes[key: typeName] = rfFields;

        return "";
    }

    /// <summary>
    /// Instantiates a generic record with concrete type arguments.
    /// Immediately generates the type to ensure it's defined before being referenced.
    /// </summary>
    public string InstantiateGenericRecord(string recordName, List<string> typeArguments)
    {
        if (!_genericRecordTemplates.TryGetValue(key: recordName,
                value: out RecordDeclaration? template))
        {
            return recordName; // Not a generic record
        }

        // Generate mangled name (sanitize nested generics like BackIndex<uaddr> -> BackIndex_uaddr)
        var sanitizedArgs = typeArguments.Select(selector: SanitizeTypeNameForMangling);
        string mangledName = $"{recordName}_{string.Join(separator: "_", values: sanitizedArgs)}";

        // Check if already instantiated
        if (_emittedTypes.Contains(item: mangledName))
        {
            return mangledName;
        }

        // Track this instantiation
        if (!_genericTypeInstantiations.ContainsKey(key: recordName))
        {
            _genericTypeInstantiations[key: recordName] = new List<List<string>>();
        }
        _genericTypeInstantiations[key: recordName].Add(item: typeArguments);

        // Add to pending queue for generation at the right location in output
        _pendingRecordInstantiations.Add(item: $"{recordName}|{string.Join(separator: ",", values: typeArguments)}");

        return mangledName;
    }

    /// <summary>
    /// Instantiates a generic entity with concrete type arguments.
    /// Immediately generates the type to ensure it's defined before being referenced.
    /// </summary>
    public string InstantiateGenericEntity(string entityName, List<string> typeArguments)
    {
        if (!_genericEntityTemplates.TryGetValue(key: entityName,
                value: out EntityDeclaration? template))
        {
            return entityName; // Not a generic entity
        }

        // Generate mangled name (sanitize nested generics like BackIndex<uaddr> -> BackIndex_uaddr)
        var sanitizedArgs = typeArguments.Select(selector: SanitizeTypeNameForMangling);
        string mangledName = $"{entityName}_{string.Join(separator: "_", values: sanitizedArgs)}";

        // Check if already instantiated
        if (_emittedTypes.Contains(item: mangledName))
        {
            return mangledName;
        }

        // Track this instantiation
        if (!_genericTypeInstantiations.ContainsKey(key: entityName))
        {
            _genericTypeInstantiations[key: entityName] = new List<List<string>>();
        }
        _genericTypeInstantiations[key: entityName].Add(item: typeArguments);

        // Immediately generate the type to ensure it's defined before being referenced
        // Create type substitution map
        var substitutions = new Dictionary<string, string>();
        for (int i = 0;
             i < Math.Min(val1: template.GenericParameters!.Count, val2: typeArguments.Count);
             i++)
        {
            substitutions[key: template.GenericParameters[index: i]] = typeArguments[index: i];
        }

        // Generate the instantiated entity type immediately
        GenerateEntityType(node: template, typeSubstitutions: substitutions, mangledName: mangledName);

        return mangledName;
    }

    /// <summary>
    /// Generates all pending generic type instantiations.
    /// Should be called after all program code is generated.
    /// </summary>
    private void GeneratePendingTypeInstantiations()
    {
        // Process pending record instantiations
        // Keep processing until the queue is empty, since generating one type
        // might trigger the need for dependent types
        while (_pendingRecordInstantiations.Count > 0)
        {
            // Process current batch
            var batch = _pendingRecordInstantiations.ToList();
            _pendingRecordInstantiations.Clear();

            foreach (string pending in batch)
            {
                string[] parts = pending.Split(separator: '|');
                string recordName = parts[0];
                var typeArguments = parts[1]
                                   .Split(separator: ',')
                                   .ToList();

                if (!_genericRecordTemplates.TryGetValue(key: recordName,
                        value: out RecordDeclaration? template))
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

                // Generate mangled name (sanitize nested generics)
                var sanitizedArgs = typeArguments.Select(selector: SanitizeTypeNameForMangling);
                string mangledName = $"{recordName}_{string.Join(separator: "_", values: sanitizedArgs)}";

                // Generate the instantiated record type
                // This might add more types to _pendingRecordInstantiations
                GenerateRecordType(node: template,
                    typeSubstitutions: substitutions,
                    mangledName: mangledName);
            }
        }

        // Process pending entity instantiations
        foreach (string pending in _pendingEntityInstantiations)
        {
            string[] parts = pending.Split(separator: '|');
            string entityName = parts[0];
            var typeArguments = parts[1]
                               .Split(separator: ',')
                               .ToList();

            if (!_genericEntityTemplates.TryGetValue(key: entityName,
                    value: out EntityDeclaration? template))
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

            // Generate mangled name (sanitize nested generics)
            var sanitizedArgs = typeArguments.Select(selector: SanitizeTypeNameForMangling);
            string mangledName = $"{entityName}_{string.Join(separator: "_", values: sanitizedArgs)}";

            // Generate the instantiated entity type
            GenerateEntityType(node: template,
                typeSubstitutions: substitutions,
                mangledName: mangledName);
        }

        _pendingEntityInstantiations.Clear();
    }
    public string VisitChoiceDeclaration(ChoiceDeclaration node)
    {
        UpdateLocation(node.Location);
        return "";
    }
    public string VisitVariantDeclaration(VariantDeclaration node)
    {
        UpdateLocation(node.Location);
        return "";
    }
    public string VisitProtocolDeclaration(ProtocolDeclaration node)
    {
        UpdateLocation(node.Location);
        return "";
    }
    public string VisitImportDeclaration(ImportDeclaration node)
    {
        UpdateLocation(node.Location);
        return "";
    }
    public string VisitNamespaceDeclaration(NamespaceDeclaration node)
    {
        UpdateLocation(node.Location);
        // Namespace declarations are handled at a higher level for symbol resolution
        return "";
    }
    public string VisitDefineDeclaration(RedefinitionDeclaration node)
    {
        UpdateLocation(node.Location);
        return "";
    }
    public string VisitUsingDeclaration(UsingDeclaration node)
    {
        UpdateLocation(node.Location);
        return "";
    }
    public string VisitImplementationDeclaration(ImplementationDeclaration node)
    {
        UpdateLocation(node.Location);
        return "";
    }
}