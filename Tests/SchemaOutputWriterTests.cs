using Chondrite.Compile;
using Xunit;

namespace Chondrite.Compile.Tests;

public sealed class SchemaOutputWriterTests : IDisposable
{
    private const string c_strSource =
        "chondrite-schema 1\nschema Chondrite.Examples.Pascals\n{\n    0: fValue float64\n}\n";

    private readonly string m_strDirectory = Path.Combine(
        Path.GetTempPath(), "Chondrite.Compile.Tests", Guid.NewGuid().ToString("N"));

    public SchemaOutputWriterTests() => Directory.CreateDirectory(m_strDirectory);

    [Fact]
    public void ManifestTracksGeneratedFilesAndExcludesEditablePartials()
    {
        SchemaOutputWriter.Write(m_strDirectory, SchemaCompiler.Compile([c_strSource]), bEmitEditable: true);

        Assert.Equal(
            new[]
            {
                "Chondrite.Examples.Pascals.canonical.chondrite",
                "Chondrite.Examples.Pascals.g.cs",
                "catalog.txt"
            },
            File.ReadAllLines(Path.Combine(m_strDirectory, ".chondrite.outputs")));
    }

    [Fact]
    public void RenamedAndRemovedSchemasCleanOwnedFilesButPreserveOtherFiles()
    {
        SchemaOutputWriter.Write(m_strDirectory, SchemaCompiler.Compile([c_strSource]), bEmitEditable: true);
        string strEditable = Path.Combine(m_strDirectory, "Chondrite.Examples.Pascals.cs");
        File.WriteAllText(strEditable, "// CUSTOM\n");
        string strUnrelated = Path.Combine(m_strDirectory, "AnotherGenerator.g.cs");
        File.WriteAllText(strUnrelated, "// UNRELATED\n");

        SchemaOutputWriter.Write(m_strDirectory, SchemaCompiler.Compile([
            c_strSource.Replace("Chondrite.Examples.Pascals", "Chondrite.Examples.Pressure")]));

        Assert.False(File.Exists(Path.Combine(m_strDirectory, "Chondrite.Examples.Pascals.g.cs")));
        Assert.False(File.Exists(Path.Combine(m_strDirectory, "Chondrite.Examples.Pascals.canonical.chondrite")));
        Assert.True(File.Exists(Path.Combine(m_strDirectory, "Chondrite.Examples.Pressure.g.cs")));
        Assert.Equal("// CUSTOM\n", File.ReadAllText(strEditable));
        Assert.Equal("// UNRELATED\n", File.ReadAllText(strUnrelated));

        SchemaOutputWriter.Write(m_strDirectory, []);

        Assert.False(File.Exists(Path.Combine(m_strDirectory, "Chondrite.Examples.Pressure.g.cs")));
        Assert.False(File.Exists(Path.Combine(m_strDirectory, "Chondrite.Examples.Pressure.canonical.chondrite")));
        Assert.Empty(File.ReadAllText(Path.Combine(m_strDirectory, "catalog.txt")));
        Assert.Equal("catalog.txt\n", File.ReadAllText(Path.Combine(m_strDirectory, ".chondrite.outputs")));
        Assert.Equal("// CUSTOM\n", File.ReadAllText(strEditable));
        Assert.Equal("// UNRELATED\n", File.ReadAllText(strUnrelated));
    }

    [Fact]
    public void UnchangedOutputPreservesFileTimestamps()
    {
        IReadOnlyList<CompiledSchema> aSchemas = SchemaCompiler.Compile([c_strSource]);
        SchemaOutputWriter.Write(m_strDirectory, aSchemas, bEmitEditable: true);
        DateTime oTimestamp = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        string[] astrPaths = Directory.GetFiles(m_strDirectory);
        foreach (string strPath in astrPaths)
            File.SetLastWriteTimeUtc(strPath, oTimestamp);

        SchemaOutputWriter.Write(m_strDirectory, aSchemas, bEmitEditable: true);

        Assert.All(astrPaths, strPath => Assert.Equal(oTimestamp, File.GetLastWriteTimeUtc(strPath)));
    }

    [Theory]
    [InlineData("../outside.g.cs")]
    [InlineData("..\\outside.g.cs")]
    [InlineData("/outside.g.cs")]
    [InlineData("C:\\outside.g.cs")]
    [InlineData("Custom.cs")]
    public void InvalidManifestFailsBeforeChangingOutputs(string strInvalidName)
    {
        SchemaOutputWriter.Write(m_strDirectory, SchemaCompiler.Compile([c_strSource]));
        string strCode = Path.Combine(m_strDirectory, "Chondrite.Examples.Pascals.g.cs");
        File.WriteAllText(strCode, "// PRESERVE\n");
        string strCustom = Path.Combine(m_strDirectory, "Custom.cs");
        File.WriteAllText(strCustom, "// CUSTOM\n");
        File.WriteAllText(Path.Combine(m_strDirectory, ".chondrite.outputs"), strInvalidName + "\n");

        Assert.Throws<InvalidDataException>(() =>
            SchemaOutputWriter.Write(m_strDirectory, SchemaCompiler.Compile([c_strSource])));

        Assert.Equal("// PRESERVE\n", File.ReadAllText(strCode));
        Assert.Equal("// CUSTOM\n", File.ReadAllText(strCustom));
    }

    public void Dispose() => Directory.Delete(m_strDirectory, recursive: true);
}
