using Xunit;

namespace Chondrite.Compile.Tests;

public sealed class EditableTypeDiscoveryTests
{
    [Fact]
    public void DiscoveryFollowsTheDeclarationWhenItsFileIsRenamedAndMoved()
    {
        using SourceFixture oFixture = new();
        string strOriginal = oFixture.strWrite("Arbitrary.cs",
            "namespace Geometry; public readonly partial record struct Vertex { }");

        Assert.Equal("Geometry.Vertex", Assert.Single(EditableTypeDiscovery.FindProvidedTypes(
            [strOriginal], ["Geometry.Vertex"])));

        string strMoved = Path.Combine(oFixture.strDirectory, "Domain", "Position.Accessors.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(strMoved)!);
        File.Move(strOriginal, strMoved);

        Assert.Equal("Geometry.Vertex", Assert.Single(EditableTypeDiscovery.FindProvidedTypes(
            [strMoved], ["Geometry.Vertex"])));
    }

    [Fact]
    public void EmptyAndMultiplePartialDeclarationsEstablishOwnership()
    {
        using SourceFixture oFixture = new();
        string strEmpty = oFixture.strWrite("Empty.cs",
            "namespace Geometry; public readonly partial record struct Vertex { }");
        string strBehavior = oFixture.strWrite("Behavior.cs",
            "namespace Geometry; public readonly partial record struct Vertex { public int nDimension => 3; }");
        string strAlias = Path.Combine(oFixture.strDirectory, ".", "Empty.cs");

        IReadOnlySet<string> astrProvided = EditableTypeDiscovery.FindProvidedTypes(
            [strEmpty, strBehavior, strEmpty, strAlias], ["Geometry.Vertex"]);

        Assert.Equal("Geometry.Vertex", Assert.Single(astrProvided));
    }

    [Theory]
    [InlineData("namespace A.B; public partial record struct Vertex { }", "A.B.Vertex")]
    [InlineData("namespace A.B { public partial record struct Vertex { } }", "A.B.Vertex")]
    [InlineData("namespace A { namespace B.C { public partial record struct Vertex { } } }", "A.B.C.Vertex")]
    [InlineData("namespace @namespace.@event; public partial record struct @class { }", "namespace.event.class")]
    [InlineData("public partial record struct Vertex { }", "Vertex")]
    public void RecognizesNamespacesAndIdentifierValues(string strSource, string strExpected)
    {
        using SourceFixture oFixture = new();
        string strPath = oFixture.strWrite("Types.cs", strSource);

        Assert.Equal(strExpected, Assert.Single(EditableTypeDiscovery.FindProvidedTypes(
            [strPath], [strExpected])));
    }

    [Fact]
    public void CommentsStringsAndSimilarNamesDoNotClaimAType()
    {
        using SourceFixture oFixture = new();
        string strPath = oFixture.strWrite("Notes.cs", """
            namespace Geometry;
            // public partial record struct Vertex { }
            /* public partial record struct Vertex { } */
            public static class Notes
            {
                public const string strExample = "public partial record struct Vertex { }";
            }
            public partial record struct VertexSuffix { }
            public partial record struct vertex { }
            """);

        Assert.Empty(EditableTypeDiscovery.FindProvidedTypes([strPath], ["Geometry.Vertex"]));
    }

    [Fact]
    public void NestedAndGenericTypesDoNotOwnTheTopLevelNongenericSchema()
    {
        using SourceFixture oFixture = new();
        string strPath = oFixture.strWrite("OtherTypes.cs", """
            namespace Geometry;
            public class Vertex<T> { }
            public class Container
            {
                public partial record struct Vertex { }
            }
            """);

        Assert.Empty(EditableTypeDiscovery.FindProvidedTypes([strPath], ["Geometry.Vertex"]));
    }

    [Fact]
    public void GenericSiblingDoesNotBlockMatchingNongenericDeclaration()
    {
        using SourceFixture oFixture = new();
        string strPath = oFixture.strWrite("Types.cs", """
            namespace Geometry;
            public class Vertex<T> { }
            public partial record struct Vertex { }
            """);

        Assert.Equal("Geometry.Vertex", Assert.Single(EditableTypeDiscovery.FindProvidedTypes(
            [strPath], ["Geometry.Vertex"])));
    }

    [Theory]
    [InlineData("public class Vertex { }")]
    [InlineData("public partial class Vertex { }")]
    [InlineData("public struct Vertex { }")]
    [InlineData("public partial struct Vertex { }")]
    [InlineData("public partial record Vertex;")]
    [InlineData("public partial record class Vertex;")]
    [InlineData("public record struct Vertex { }")]
    [InlineData("public enum Vertex { Zero }")]
    [InlineData("public delegate void Vertex();")]
    [InlineData("file readonly partial record struct Vertex { }")]
    public void IncompatibleDeclarationsProduceAFileAndLineDiagnostic(string strDeclaration)
    {
        using SourceFixture oFixture = new();
        string strPath = oFixture.strWrite("Conflict.cs", "namespace Geometry;\n" + strDeclaration);

        FormatException oException = Assert.Throws<FormatException>(() =>
            EditableTypeDiscovery.FindProvidedTypes([strPath], ["Geometry.Vertex"]));

        Assert.Contains(strPath + "(2,", oException.Message);
        Assert.Contains("Geometry.Vertex", oException.Message);
        Assert.Contains("partial record struct", oException.Message);
    }

    [Fact]
    public void SyntaxErrorsFailDiscoveryEvenAfterEveryExpectedTypeWasFound()
    {
        using SourceFixture oFixture = new();
        string strValid = oFixture.strWrite("Valid.cs",
            "namespace Geometry; public partial record struct Vertex { }");
        string strBroken = oFixture.strWrite("Broken.cs",
            "namespace Unrelated;\npublic class Broken { public void Run( }");

        FormatException oException = Assert.Throws<FormatException>(() =>
            EditableTypeDiscovery.FindProvidedTypes([strValid, strBroken], ["Geometry.Vertex"]));

        Assert.Contains(strBroken + "(2,", oException.Message);
        Assert.Contains("source contains an error", oException.Message);
    }

    [Fact]
    public void MissingSourceIsReportedInsteadOfTreatedAsAnAbsentDeclaration()
    {
        using SourceFixture oFixture = new();
        string strMissing = Path.Combine(oFixture.strDirectory, "Missing.cs");

        FileNotFoundException oException = Assert.Throws<FileNotFoundException>(() =>
            EditableTypeDiscovery.FindProvidedTypes([strMissing], ["Geometry.Vertex"]));

        Assert.Equal(strMissing, oException.FileName);
        Assert.Contains(strMissing, oException.Message);
        Assert.Contains("editable-type discovery", oException.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConditionalSymbolsControlWhichDeclarationsAreActive(bool bEnabled)
    {
        using SourceFixture oFixture = new();
        string strPath = oFixture.strWrite("Conditional.cs", """
            namespace Geometry;
            #if OWN_VERTEX
            public partial record struct Vertex { }
            #else
            public partial record struct Other { }
            #endif
            """);

        IReadOnlySet<string> astrProvided = EditableTypeDiscovery.FindProvidedTypes(
            [strPath], ["Geometry.Vertex"], astrPreprocessorSymbols: bEnabled ? ["OWN_VERTEX"] : []);

        Assert.Equal(bEnabled, astrProvided.Contains("Geometry.Vertex"));
    }

    [Fact]
    public void IncompatibleDeclarationInAnInactiveBranchDoesNotBlockDiscovery()
    {
        using SourceFixture oFixture = new();
        string strPath = oFixture.strWrite("Conditional.cs", """
            namespace Geometry;
            #if OWN_VERTEX
            public partial record struct Vertex { }
            #else
            public class Vertex { }
            #endif
            """);

        Assert.Equal("Geometry.Vertex", Assert.Single(EditableTypeDiscovery.FindProvidedTypes(
            [strPath], ["Geometry.Vertex"], astrPreprocessorSymbols: ["OWN_VERTEX"])));
        Assert.Throws<FormatException>(() =>
            EditableTypeDiscovery.FindProvidedTypes([strPath], ["Geometry.Vertex"]));
    }

    [Fact]
    public void DiscoveryDoesNotRequireGeneratedFieldsOrResolvedReferencedTypes()
    {
        using SourceFixture oFixture = new();
        string strPath = oFixture.strWrite("Editable.cs", """
            namespace Geometry;
            public readonly partial record struct Vertex
            {
                public Vertex(int nValue)
                {
                    m_oData = new SchemaData(nValue);
                }
                public ExternalQuantity uValue => UnknownAdapter.Convert(m_oData.nValue);
            }
            """);

        Assert.Equal("Geometry.Vertex", Assert.Single(EditableTypeDiscovery.FindProvidedTypes(
            [strPath], ["Geometry.Vertex"], strLanguageVersion: "14.0")));
    }

    [Fact]
    public void InvalidLanguageVersionIsRejected()
    {
        ArgumentException oException = Assert.Throws<ArgumentException>(() =>
            EditableTypeDiscovery.FindProvidedTypes([], [], strLanguageVersion: "not-a-version"));

        Assert.Equal("strLanguageVersion", oException.ParamName);
        Assert.Contains("not-a-version", oException.Message);
    }

    [Fact]
    public void InvalidConditionalSymbolIsRejected()
    {
        ArgumentException oException = Assert.Throws<ArgumentException>(() =>
            EditableTypeDiscovery.FindProvidedTypes([], [], astrPreprocessorSymbols: ["FEATURE=1"]));

        Assert.Equal("astrPreprocessorSymbols", oException.ParamName);
    }

    private sealed class SourceFixture : IDisposable
    {
        public string strDirectory { get; } = Path.Combine(
            Path.GetTempPath(), "Chondrite.Compile.Discovery", Guid.NewGuid().ToString("N"));

        public SourceFixture()
        {
            Directory.CreateDirectory(strDirectory);
        }

        public string strWrite(string strRelativePath, string strSource)
        {
            string strPath = Path.Combine(strDirectory, strRelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(strPath)!);
            File.WriteAllText(strPath, strSource);
            return strPath;
        }

        public void Dispose()
        {
            Directory.Delete(strDirectory, recursive: true);
        }
    }
}
