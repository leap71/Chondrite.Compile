using Chondrite.Compile;
using Xunit;

namespace Chondrite.Compile.Tests;

public sealed class EditableScaffolderTests : IDisposable
{
    private const string c_strSchema = "chondrite-schema 1\nschema Example.Pascals\n{\n    0: fValue float64\n}\n";
    private readonly string m_strDirectory = Path.Combine(Path.GetTempPath(), "Chondrite.Editable.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void CreatesUsablePublicPartialInSeparateSourceDirectory()
    {
        IReadOnlyList<CompiledSchema> aSchemas = SchemaCompiler.Compile([c_strSchema]);
        string strGenerated = Path.Combine(m_strDirectory, "obj");
        string strEditable = Path.Combine(m_strDirectory, "Records");
        SchemaOutputWriter.Write(strGenerated, aSchemas);

        string strFile = Assert.Single(EditableScaffolder.Ensure(strEditable, aSchemas, []));

        Assert.Equal(Path.Combine(strEditable, "Example.Pascals.cs"), strFile);
        string strCode = File.ReadAllText(strFile);
        Assert.Contains("public Pascals(", strCode);
        Assert.Contains("public double fValue => m_oData.fValue;", strCode);
        Assert.False(File.Exists(Path.Combine(strGenerated, "Example.Pascals.cs")));
        Assert.Contains("LayoutKind.Auto", File.ReadAllText(Path.Combine(strGenerated, "Example.Pascals.g.cs")));
        Assert.DoesNotContain("LayoutKind.Auto", strCode);
        Assert.Empty(EditableScaffolder.Ensure(strEditable, aSchemas, [strFile]));
    }

    [Fact]
    public void MovedRenamedAndEditedSourceIsPreservedWithoutReplacement()
    {
        IReadOnlyList<CompiledSchema> aSchemas = SchemaCompiler.Compile([c_strSchema]);
        string strEditable = Path.Combine(m_strDirectory, "Records");
        string strOriginal = Assert.Single(EditableScaffolder.Ensure(strEditable, aSchemas, []));
        string strMoved = Path.Combine(m_strDirectory, "Physics", "Pressure.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(strMoved)!);
        File.Move(strOriginal, strMoved);
        File.AppendAllText(strMoved, "\n// User customization remains here.\n");
        byte[] abyExpected = File.ReadAllBytes(strMoved);

        Assert.Empty(EditableScaffolder.Ensure(strEditable, aSchemas, [strMoved]));

        Assert.False(File.Exists(strOriginal));
        Assert.Equal(abyExpected, File.ReadAllBytes(strMoved));
    }

    [Fact]
    public void StandaloneDiscoveryFindsCustomFilenamesWithinEditableDirectory()
    {
        string strEditable = Path.Combine(m_strDirectory, "Records");
        string strFile = strWrite("Records/Units/Pressure.cs",
            "namespace Example; public readonly partial record struct Pascals { }");
        Assert.Empty(EditableScaffolder.Ensure(strEditable, SchemaCompiler.Compile([c_strSchema])));
        Assert.Equal([strFile], Directory.GetFiles(strEditable, "*.cs", SearchOption.AllDirectories));
    }

    [Fact]
    public void ExistingDestinationIsPreservedAndIncludedWhenCreatedByAnotherBuild()
    {
        IReadOnlyList<CompiledSchema> aSchemas = SchemaCompiler.Compile([c_strSchema]);
        string strEditable = Path.Combine(m_strDirectory, "Records");
        string strFile = Assert.Single(EditableScaffolder.Ensure(strEditable, aSchemas, []));
        File.AppendAllText(strFile, "\n// Existing developer edit.\n");
        byte[] abyExpected = File.ReadAllBytes(strFile);

        Assert.Equal(strFile, Assert.Single(EditableScaffolder.Ensure(
            strEditable, aSchemas, [], astrPreexistingPaths: [])));
        Assert.Equal(abyExpected, File.ReadAllBytes(strFile));
    }

    [Fact]
    public void OccupiedDestinationIsNeverOverwrittenOrPartlyScaffolded()
    {
        string strEditable = Path.Combine(m_strDirectory, "Records");
        string strOccupied = strWrite("Records/Example.Pascals.cs", "// User-owned file.\n");
        IReadOnlyList<CompiledSchema> aSchemas = SchemaCompiler.Compile([
            c_strSchema, "chondrite-schema 1\nschema Example.Another { 0: nValue int32 }"
        ]);

        Assert.Throws<InvalidDataException>(() => EditableScaffolder.Ensure(strEditable, aSchemas, []));

        Assert.Equal("// User-owned file.\n", File.ReadAllText(strOccupied));
        Assert.False(File.Exists(Path.Combine(strEditable, "Example.Another.cs")));
    }

    [Fact]
    public void PreexistingExcludedDestinationIsNotSilentlyIncluded()
    {
        string strFile = strWrite("Records/Example.Pascals.cs",
            "namespace Example; public readonly partial record struct Pascals { }");
        byte[] abyExpected = File.ReadAllBytes(strFile);

        InvalidDataException oError = Assert.Throws<InvalidDataException>(() =>
            EditableScaffolder.Ensure(Path.Combine(m_strDirectory, "Records"),
                SchemaCompiler.Compile([c_strSchema]), [], astrPreexistingPaths: [strFile]));

        Assert.Contains("not included", oError.Message);
        Assert.Contains("Compile", oError.Message);
        Assert.Equal(abyExpected, File.ReadAllBytes(strFile));
    }

    [Fact]
    public void ConcurrentDestinationContainingSeveralTypesDoesNotDuplicateThem()
    {
        string strFile = strWrite("Records/Example.Pascals.cs",
            "namespace Example; public readonly partial record struct Pascals { } " +
            "public readonly partial record struct Another { }");
        IReadOnlyList<CompiledSchema> aSchemas = SchemaCompiler.Compile([
            c_strSchema, "chondrite-schema 1\nschema Example.Another { 0: nValue int32 }"
        ]);

        Assert.Equal(strFile, Assert.Single(EditableScaffolder.Ensure(
            Path.Combine(m_strDirectory, "Records"), aSchemas, [], astrPreexistingPaths: [])));
        Assert.False(File.Exists(Path.Combine(m_strDirectory, "Records", "Example.Another.cs")));
    }

    [Fact]
    public void ExplicitProvidedTypeSuppressesScaffolding()
    {
        string strEditable = Path.Combine(m_strDirectory, "Records");
        IReadOnlyList<CompiledSchema> aSchemas = SchemaCompiler.Compile([c_strSchema]);

        Assert.Empty(EditableScaffolder.Ensure(strEditable, aSchemas, [], ["Example.Pascals"]));
        Assert.False(Directory.Exists(strEditable));
    }

    [Fact]
    public void UnqualifiedSchemaUsesTheGeneratedFallbackNamespace()
    {
        string strFile = strWrite("Custom.cs",
            "namespace Chondrite.Generated; public readonly partial record struct Pascals { }");
        IReadOnlyList<CompiledSchema> aSchemas = SchemaCompiler.Compile([
            "chondrite-schema 1\nschema Pascals { 0: fValue float64 }"
        ]);

        Assert.Empty(EditableScaffolder.Ensure(Path.Combine(m_strDirectory, "Records"), aSchemas, [strFile]));
    }

    [Fact]
    public void EmptySchemaSetNeverRemovesEditableSources()
    {
        string strEditable = Path.Combine(m_strDirectory, "Records");
        string strFile = Assert.Single(EditableScaffolder.Ensure(
            strEditable, SchemaCompiler.Compile([c_strSchema]), []));
        byte[] abyExpected = File.ReadAllBytes(strFile);

        Assert.Empty(EditableScaffolder.Ensure(strEditable, [], [strFile]));
        Assert.Equal(abyExpected, File.ReadAllBytes(strFile));
    }

    [Fact]
    public void ParseFailureDoesNotCreateEditableFiles()
    {
        string strFile = strWrite("Broken.cs", "namespace Example { public partial record struct Pascals(");
        string strEditable = Path.Combine(m_strDirectory, "Records");

        Assert.Throws<FormatException>(() => EditableScaffolder.Ensure(
            strEditable, SchemaCompiler.Compile([c_strSchema]), [strFile]));
        Assert.False(Directory.Exists(strEditable));
    }

    private string strWrite(string strRelative, string strContent)
    {
        string strPath = Path.GetFullPath(Path.Combine(m_strDirectory, strRelative));
        Directory.CreateDirectory(Path.GetDirectoryName(strPath)!);
        File.WriteAllText(strPath, strContent);
        return strPath;
    }

    public void Dispose()
    {
        if (Directory.Exists(m_strDirectory))
            Directory.Delete(m_strDirectory, recursive: true);
    }
}
