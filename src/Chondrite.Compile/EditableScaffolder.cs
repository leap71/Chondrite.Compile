namespace Chondrite.Compile;

/// <summary>
/// Creates public partials only for schema types not already supplied by authored C#.
/// Source files belong to the developer and are never overwritten or removed.
/// </summary>
public static class EditableScaffolder
{
    private static readonly StringComparer c_oPathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>
    /// Returns source paths that the current build must explicitly include. Matching
    /// declarations already present in the supplied source list require no new files.
    /// </summary>
    /// <remarks>
    /// A null source list scans the editable directory for standalone command-line use.
    /// MSBuild supplies its actual source list, including linked files. Conditional or
    /// externally generated implementations can be declared through astrProvidedTypes.
    /// </remarks>
    public static IReadOnlyList<string> Ensure(
        string strOutput,
        IReadOnlyList<CompiledSchema> aSchemas,
        IEnumerable<string>? astrSourcePaths = null,
        IEnumerable<string>? astrProvidedTypes = null,
        string? strLanguageVersion = null,
        IEnumerable<string>? astrPreprocessorSymbols = null,
        IEnumerable<string>? astrPreexistingPaths = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(strOutput);
        ArgumentNullException.ThrowIfNull(aSchemas);
        string strDirectory = Path.GetFullPath(strOutput);
        HashSet<string> astrProvided = new(
            (astrProvidedTypes ?? []).Select(strType => strType.Trim()), StringComparer.Ordinal);
        CompiledSchema[] aCandidates = aSchemas.Where(oSchema =>
            !astrProvided.Contains(oSchema.Name) &&
            !astrProvided.Contains(strCSharpName(oSchema.Name))).ToArray();
        if (aCandidates.Length == 0)
            return [];

        string[] astrSources = (astrSourcePaths ??
            (Directory.Exists(strDirectory)
                ? Directory.EnumerateFiles(strDirectory, "*.cs", SearchOption.AllDirectories)
                    .Where(strPath => !strPath.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase))
                : [])).ToArray();
        string[] astrSymbols = (astrPreprocessorSymbols ?? []).ToArray();
        string[] astrExpected = aCandidates.Select(oSchema => strCSharpName(oSchema.Name)).ToArray();
        HashSet<string> astrDeclared = new(EditableTypeDiscovery.FindProvidedTypes(
            astrSources, astrExpected, strLanguageVersion, astrSymbols), StringComparer.Ordinal);
        HashSet<string> astrIncludedPaths = new(astrSources.Select(Path.GetFullPath), c_oPathComparer);
        HashSet<string>? astrPreexisting = astrPreexistingPaths is null ? null :
            new(astrPreexistingPaths.Select(Path.GetFullPath), c_oPathComparer);
        List<string> astrAdditionalSources = [];

        // Check all destinations before writing anything. Existing source in the
        // configured output directory can come from another target-framework build.
        // An older file deliberately excluded from Compile must not be re-included.
        foreach (CompiledSchema oSchema in aCandidates.OrderBy(oSchema => oSchema.Name, StringComparer.Ordinal))
        {
            string strType = strCSharpName(oSchema.Name);
            if (astrDeclared.Contains(strType))
                continue;
            string strPath = Path.Combine(strDirectory, oSchema.Name + ".cs");
            if (!File.Exists(strPath))
                continue;
            if (astrSourcePaths is not null && !astrIncludedPaths.Contains(strPath) &&
                (astrPreexisting is null || astrPreexisting.Contains(strPath)))
                throw new InvalidDataException(
                    $"Editable destination '{strPath}' already exists but is not included in the project's Compile inputs. " +
                    "The excluded file was preserved. Include it explicitly, choose another editable directory, " +
                    "or declare the schema through ChondriteEditableProvided.");
            astrDeclared.UnionWith(ValidateExisting(strPath, strType, astrExpected, strLanguageVersion, astrSymbols));
            astrAdditionalSources.Add(strPath);
        }

        var aPending = aCandidates
            .Where(oSchema => !astrDeclared.Contains(strCSharpName(oSchema.Name)))
            .OrderBy(oSchema => oSchema.Name, StringComparer.Ordinal)
            .Select(oSchema => (strPath: Path.Combine(strDirectory, oSchema.Name + ".cs"),
                strType: strCSharpName(oSchema.Name), strContent: CSharpGenerator.GenerateEditable(oSchema)))
            .ToArray();
        if (aPending.Length == 0)
            return astrAdditionalSources;
        Directory.CreateDirectory(strDirectory);
        foreach ((string strPath, string strType, string strContent) in aPending)
        {
            if (astrDeclared.Contains(strType))
                continue;
            SchemaOutputWriter.WriteEditableIfAbsent(strPath, strContent);
            // Validate a competing creator's file as well; never overwrite it.
            astrDeclared.UnionWith(ValidateExisting(strPath, strType, astrExpected, strLanguageVersion, astrSymbols));
            astrAdditionalSources.Add(strPath);
        }
        return astrAdditionalSources;
    }

    private static IReadOnlySet<string> ValidateExisting(
        string strPath, string strType, string[] astrExpected,
        string? strLanguageVersion, string[] astrSymbols)
    {
        IReadOnlySet<string> astrDeclared = EditableTypeDiscovery.FindProvidedTypes(
            [strPath], astrExpected, strLanguageVersion, astrSymbols);
        if (!astrDeclared.Contains(strType))
            throw new InvalidDataException(
                $"Editable destination '{strPath}' already exists but does not declare '{strType}'. " +
                "The existing file was preserved. Choose another editable directory or explicitly provide this type.");
        return astrDeclared;
    }

    private static string strCSharpName(string strSchemaName) =>
        strSchemaName.Contains('.') ? strSchemaName : "Chondrite.Generated." + strSchemaName;
}
