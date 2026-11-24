using System.Collections.Immutable;
using System.Text;
using Chondrite.Compile;
using Chondrite.Core;
using Xunit;

namespace Chondrite.Compile.Tests;

public sealed class SchemaCompilerTests
{
    private const string c_strChondrite =
        "chondrite-schema 1\nschema Chondrite.Examples.Pascals\n{\n    0: value float64\n}\n";

    [Fact]
    public void ChondriteHeaderIsCanonicalAndStable()
    {
        CompiledSchema oSchema = Assert.Single(SchemaCompiler.Compile([c_strChondrite]));
        Assert.Equal(c_strChondrite, oSchema.CanonicalText);
        Assert.Equal("3CE4258FD29DEBEC41CE8FD13A4DB3A0293E4E2F8A1DDC32DCBA743516C50319", oSchema.Id.ToString());
        Assert.Equal(oSchema.Id, oSchema.SchemaObject.idCalculateIdentity());
        Assert.Equal("Chondrite.Examples.Pascals", PublishedSchemaReader.Read(oSchema.SchemaObject).Name);
        StorageObject oObject = new(StorageType.idIntrinsicSchema,
            ImmutableArray.CreateRange(Encoding.UTF8.GetBytes(oSchema.CanonicalText)));
        Assert.Equal(oObject.idCalculateIdentity(), oSchema.Id);
    }

    [Fact]
    public void V1GoldenPascalsSchemaIdentityDoesNotDrift()
    {
        const string strSchema =
            "chondrite-schema 1\nschema Chondrite.Store.Tests.Records.Pascals\n" +
            "{\n    0: fValue float64\n}\n";
        CompiledSchema oSchema = Assert.Single(SchemaCompiler.Compile([strSchema]));
        Assert.Equal(strSchema, oSchema.CanonicalText);
        Assert.Equal("BEA2362A5F43FA260363BBBA4A50517461D0211774E358C8D1A7B4CB41B86274",
            oSchema.Id.ToString());
    }

    [Fact]
    public void WhitespaceAndCommentsDoNotChangeSchemaId()
    {
        CompiledSchema oA = Assert.Single(SchemaCompiler.Compile([c_strChondrite]));
        CompiledSchema oB = Assert.Single(SchemaCompiler.Compile([
            "chondrite-schema 1\nschema Chondrite.Examples.Pascals { 0: value float64 } // comment"
        ]));
        Assert.Equal(oA.Id, oB.Id);
    }

    [Theory]
    [InlineData("noyron-schema 1")]
    [InlineData("chondrite-schema 2")]
    public void RejectsUnsupportedSchemaHeader(string strHeader)
    {
        string strSource = c_strChondrite.Replace("chondrite-schema 1", strHeader);
        Assert.Throws<FormatException>(() => SchemaCompiler.Compile([strSource]));
    }

    [Fact]
    public void DependenciesResolveByExactSchemaIdentityInOrder()
    {
        const string strParent = """
            chondrite-schema 1
            schema Chondrite.Examples.Sample
            {
                0: value Chondrite.Examples.Pascals
                1: positions vector3f[]
            }
            """;
        IReadOnlyList<CompiledSchema> aSchemas = SchemaCompiler.Compile([strParent, c_strChondrite]);
        Assert.Equal(2, aSchemas.Count);
        CompiledSchema oPascals = aSchemas[0];
        CompiledSchema oSample = aSchemas[1];
        Assert.Equal("Chondrite.Examples.Pascals", oPascals.Name);
        Assert.Contains("@" + oPascals.Id, oSample.CanonicalText);
        Assert.Equal(oPascals.Name, Assert.Single(oSample.Dependencies));
        Assert.IsType<SchemaIdType>(PublishedSchemaReader.Read(oSample.SchemaObject).Fields[0].Type);
        Assert.Contains("global::Chondrite.Examples.Pascals.EnsureSchema(oStore)",
            CSharpGenerator.Generate(oSample));
    }

    [Fact]
    public void GeneratedReadersGuardAllArraysBeforeCreatingBuilders()
    {
        const string strSchema = """
            chondrite-schema 1
            schema Example.Collection
            {
                0: aFixed int32[4]
                1: aVariable vector3f[]
                2: aOptional optional<utf8>[]
                3: aNested uint8[][]
            }
            """;
        string strCode = CSharpGenerator.Generate(
            Assert.Single(SchemaCompiler.Compile([strSchema])));
        Assert.Contains("oReader.GuardCollectionCount(nCount0, 4);", strCode);
        Assert.Contains("oReader.GuardCollectionCount(nCount1, 12);", strCode);
        Assert.Contains("oReader.GuardCollectionCount(nCount2, 1);", strCode);
        // Two nested builders produce five guards in total.
        Assert.Equal(5, strCode.Split("GuardCollectionCount(").Length - 1);
        Assert.Equal(5, strCode.Split("ImmutableArray.CreateBuilder<").Length - 1);
    }

    [Fact]
    public void CompiledSchemaOwnsOneCanonicalIdentity()
    {
        CompiledSchema oSchema = Assert.Single(SchemaCompiler.Compile([c_strChondrite]));
        Assert.Equal(oSchema.Id, oSchema.SchemaObject.idCalculateIdentity());
        Assert.Equal(oSchema.CanonicalText,
            Encoding.UTF8.GetString(oSchema.SchemaObject.abyData.AsSpan()));
        Assert.Equal(c_strChondrite, oSchema.CanonicalText);
    }

    [Fact]
    public void GeneratedCodeTargetsChondriteCoreAndStore()
    {
        CompiledSchema oSchema = Assert.Single(SchemaCompiler.Compile([c_strChondrite]));
        string strGenerated = CSharpGenerator.Generate(oSchema);
        Assert.Contains("using Chondrite.Core;", strGenerated);
        Assert.Contains("using Chondrite.Store;", strGenerated);
        Assert.Contains("using Chondrite.Store.Serialization;", strGenerated);
        Assert.Contains("namespace Chondrite.Examples;", strGenerated);
        Assert.Contains("record struct Pascals : IStorageRecord<Pascals>", strGenerated);
        Assert.Contains("private readonly SchemaData m_oData;", strGenerated);
        Assert.Contains("private static readonly ImmutableArray<byte> c_abyCanonicalSchema", strGenerated);
        Assert.Contains("oStore.idRegisterType(c_abyCanonicalSchema)", strGenerated);
        Assert.Contains("public static Pascals oDeserialize(StorageObject oObject)", strGenerated);
        Assert.Contains("public static Pascals oDeserialize(StorageObject oObject, StorageDecodeLimits oLimits)", strGenerated);
        Assert.Contains("public static Pascals oGet(IObjectStore oStore", strGenerated);
    }

    [Fact]
    public void BuiltinVectorsAndNonceMapToCorrectRuntimeTypes()
    {
        const string strSource = """
            chondrite-schema 1
            schema Chondrite.Examples.Sample
            {
                0: token nonce
                1: point vector3f
                2: bounds vector2f[2]
                3: color optional<vector4f>
                4: reference objectid
            }
            """;
        string strCode = CSharpGenerator.Generate(Assert.Single(SchemaCompiler.Compile([strSource])));
        Assert.Contains("global::Chondrite.Store.Nonce token", strCode);
        Assert.Contains("global::System.Numerics.Vector3 point", strCode);
        Assert.Contains("ImmutableArray<global::System.Numerics.Vector2> bounds", strCode);
        Assert.Contains("global::System.Numerics.Vector4? color", strCode);
        Assert.Contains("global::Chondrite.Core.StorageObjectId reference", strCode);
        Assert.Contains("oWriter.WriteNonce(oValue.token)", strCode);
        Assert.Contains("oReader.ReadVector3f()", strCode);
    }

    [Fact]
    public void GeneratedCodeEscapesCSharpKeywords()
    {
        const string strSource = "chondrite-schema 1\nschema namespace.class\n{\n    0: namespace bool\n}\n";
        CompiledSchema oSchema = Assert.Single(SchemaCompiler.Compile([strSource]));
        string strGenerated = CSharpGenerator.Generate(oSchema);
        string strEditable = CSharpGenerator.GenerateEditable(oSchema);
        Assert.Contains("namespace @namespace;", strGenerated);
        Assert.Contains("record struct @class : IStorageRecord<@class>", strGenerated);
        Assert.Contains("bool @namespace", strGenerated);
        Assert.Contains("public bool @namespace => m_oData.@namespace;", strEditable);
    }

    [Fact]
    public void VariableArraysUseUnsigned64BitLengthsAndInlineNestedCodecs()
    {
        const string strSource = """
            chondrite-schema 1
            schema Chondrite.Examples.Polyline
            {
                0: samples Chondrite.Examples.Pascals[]
                1: offsets optional<int32[]>
            }
            """;
        IReadOnlyList<CompiledSchema> aSchemas = SchemaCompiler.Compile([c_strChondrite, strSource]);
        CompiledSchema oSchema = aSchemas[^1];
        string strGenerated = CSharpGenerator.Generate(oSchema);
        Assert.Contains("oWriter.WriteUInt64((ulong)oValue.samples.Length)", strGenerated);
        Assert.Contains("global::Chondrite.Examples.Pascals.xWrite", strGenerated);
        Assert.Contains("oReader.ReadCount()", strGenerated);
        Assert.Contains(".MoveToImmutable()", strGenerated);
    }

    [Fact]
    public void SchemaNamesAndFieldLayoutAreIdentityBearing()
    {
        const string strA = "chondrite-schema 1\nschema Chondrite.Examples.P\n{\n0: x float32\n1: y float32\n}\n";
        const string strB = "chondrite-schema 1\nschema Chondrite.Examples.P\n{\n0: y float32\n1: x float32\n}\n";
        const string strC = "chondrite-schema 1\nschema Chondrite.Examples.Q\n{\n0: x float32\n1: y float32\n}\n";
        StorageObjectId idA = Assert.Single(SchemaCompiler.Compile([strA])).Id;
        Assert.NotEqual(idA, Assert.Single(SchemaCompiler.Compile([strB])).Id);
        Assert.NotEqual(idA, Assert.Single(SchemaCompiler.Compile([strC])).Id);
    }

    [Fact]
    public void RejectsInvalidSchemaDefinitions()
    {
        Assert.Throws<FormatException>(() => SchemaCompiler.Compile([
            "chondrite-schema 2\nschema Chondrite.Bad { 0: a int32 }"
        ]));
        Assert.Throws<FormatException>(() => SchemaCompiler.Compile([
            "chondrite-schema 1\nschema Chondrite.Bad { 1: a int32 }"
        ]));
        Assert.Throws<FormatException>(() => SchemaCompiler.Compile([
            "chondrite-schema 1\nschema Chondrite.Bad { 0: a Chondrite.Unknown }"
        ]));
        Assert.Throws<FormatException>(() => SchemaCompiler.Compile([
            "chondrite-schema 1\nschema Chondrite.A { 0: b Chondrite.B }\n" +
            "schema Chondrite.B { 0: a Chondrite.A }"
        ]));
    }

    [Fact]
    public void RejectsNonCanonicalPublishedSchema()
    {
        byte[] abyData = Encoding.UTF8.GetBytes(
            "chondrite-schema 1\r\nschema Chondrite.Examples.Pascals\r\n{\r\n    0: value float64\r\n}\r\n");
        StorageObject oObject = new(StorageType.idIntrinsicSchema, ImmutableArray.CreateRange(abyData));
        Assert.Throws<InvalidDataException>(() => PublishedSchemaReader.Read(oObject));
    }

    [Fact]
    public void DefaultOutputDoesNotWriteEditableSources()
    {
        string strPath = Path.Combine(Path.GetTempPath(), "Chondrite.Compile.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            IReadOnlyList<CompiledSchema> aSchemas = SchemaCompiler.Compile([c_strChondrite]);
            SchemaOutputWriter.Write(strPath, aSchemas);
            Assert.True(File.Exists(Path.Combine(strPath, "Chondrite.Examples.Pascals.g.cs")));
            Assert.True(File.Exists(Path.Combine(strPath, "Chondrite.Examples.Pascals.canonical.chondrite")));
            Assert.False(File.Exists(Path.Combine(strPath, "Chondrite.Examples.Pascals.cs")));
            Assert.Contains(aSchemas[0].Id.ToString(), File.ReadAllText(Path.Combine(strPath, "catalog.txt")));
        }
        finally
        {
            if (Directory.Exists(strPath)) Directory.Delete(strPath, recursive: true);
        }
    }

    [Fact]
    public void EditableSourcesAreOnlyCreatedOnce()
    {
        string strPath = Path.Combine(Path.GetTempPath(), "Chondrite.Compile.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            IReadOnlyList<CompiledSchema> aSchemas = SchemaCompiler.Compile([c_strChondrite]);
            SchemaOutputWriter.Write(strPath, aSchemas, bEmitEditable: true);
            string strEditable = Path.Combine(strPath, "Chondrite.Examples.Pascals.cs");
            Assert.Contains("m_oData", File.ReadAllText(strEditable));
            File.WriteAllText(strEditable, "// CUSTOM\n");
            SchemaOutputWriter.Write(strPath, aSchemas, bEmitEditable: true);
            Assert.Equal("// CUSTOM\n", File.ReadAllText(strEditable));
        }
        finally
        {
            if (Directory.Exists(strPath)) Directory.Delete(strPath, recursive: true);
        }
    }
}
