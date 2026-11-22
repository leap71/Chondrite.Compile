namespace Chondrite.Compile;

/// <summary>
/// Writes canonical schemas and their generated C# counterparts. The output manifest
/// tracks replaceable generated files; editable partials are created only when absent.
/// </summary>
public static class SchemaOutputWriter
{
    private const string c_strManifestName = ".chondrite.outputs";

    public static void Write(string strOutput, IReadOnlyList<CompiledSchema> aSchemas, bool bEmitEditable = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(strOutput);
        ArgumentNullException.ThrowIfNull(aSchemas);
        string strDirectory = Path.GetFullPath(strOutput);
        string strManifest = Path.Combine(strDirectory, c_strManifestName);
        string[] astrPrevious = File.Exists(strManifest)
            ? File.ReadAllLines(strManifest, SchemaConstants.c_oStrictUtf8)
            : [];

        // Validate the whole manifest before writing or deleting anything. In particular,
        // an edited manifest must never redirect cleanup outside this output directory.
        foreach (string strName in astrPrevious)
            ValidateGeneratedName(strName);

        Dictionary<string, string> oGenerated = new(StringComparer.Ordinal);
        Dictionary<string, string> oEditable = new(StringComparer.Ordinal);
        foreach (CompiledSchema oSchema in aSchemas)
        {
            string strCanonical = oSchema.Name + ".canonical.chondrite";
            string strCode = oSchema.Name + ".g.cs";
            ValidateGeneratedName(strCanonical);
            ValidateGeneratedName(strCode);
            oGenerated.Add(strCanonical, oSchema.CanonicalText);
            oGenerated.Add(strCode, CSharpGenerator.Generate(oSchema));
            if (bEmitEditable)
                oEditable.Add(oSchema.Name + ".cs", CSharpGenerator.GenerateEditable(oSchema));
        }
        oGenerated.Add("catalog.txt", aSchemas.Count == 0 ? string.Empty :
            string.Join("\n", aSchemas.OrderBy(oSchema => oSchema.Name, StringComparer.Ordinal)
                .Select(oSchema => $"{oSchema.Name} = {oSchema.Id}")) + "\n");

        // Generate all text first so a compilation/generation failure leaves old output intact.
        Directory.CreateDirectory(strDirectory);
        foreach ((string strName, string strContent) in oGenerated)
            WriteIfDifferent(Path.Combine(strDirectory, strName), strContent);
        foreach ((string strName, string strContent) in oEditable)
            WriteEditableIfAbsent(Path.Combine(strDirectory, strName), strContent);

        foreach (string strName in astrPrevious)
        {
            if (!oGenerated.ContainsKey(strName))
                File.Delete(Path.Combine(strDirectory, strName));
        }

        WriteIfDifferent(strManifest,
            string.Join("\n", oGenerated.Keys.OrderBy(strName => strName, StringComparer.Ordinal)) + "\n");
    }

    private static void ValidateGeneratedName(string strName)
    {
        bool bGenerated = strName == "catalog.txt" ||
            (strName.Length > ".g.cs".Length && strName.EndsWith(".g.cs", StringComparison.Ordinal)) ||
            (strName.Length > ".canonical.chondrite".Length &&
                strName.EndsWith(".canonical.chondrite", StringComparison.Ordinal));
        if (!bGenerated || strName.IndexOfAny(['/', '\\', ':', '\r', '\n']) >= 0 ||
            strName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || strName.StartsWith('.'))
            throw new InvalidDataException($"Invalid generated output name '{strName}' in {c_strManifestName}.");
    }

    private static void WriteIfDifferent(string strPath, string strContent)
    {
        byte[] abyContent = SchemaConstants.c_oStrictUtf8.GetBytes(strContent);
        if (File.Exists(strPath) && File.ReadAllBytes(strPath).AsSpan().SequenceEqual(abyContent))
            return;

        string strTemporary = strPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(strTemporary, abyContent);
            File.Move(strTemporary, strPath, overwrite: true);
        }
        finally
        {
            File.Delete(strTemporary);
        }
    }

    private static void WriteEditableIfAbsent(string strPath, string strContent)
    {
        if (File.Exists(strPath))
            return;

        string strTemporary = strPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(strTemporary, strContent, SchemaConstants.c_oStrictUtf8);
            try
            {
                File.Move(strTemporary, strPath);
            }
            catch (IOException) when (File.Exists(strPath))
            {
                // Another invocation created the editable partial first. Its contents win.
                // Write failures above are deliberately outside this catch.
            }
        }
        finally
        {
            File.Delete(strTemporary);
        }
    }
}
