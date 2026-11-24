using System.Globalization;
using Xunit;

namespace Chondrite.Compile.Tests;

public sealed class PrimitiveCatalogTests
{
    [Fact]
    public void EveryV1PrimitiveHasAPackageProbeAndEmitsTheExpectedCodecAndWidth()
    {
        using Stream oFixture = typeof(PrimitiveCatalogTests).Assembly
            .GetManifestResourceStream("PackageSmoke.Primitives.chondrite")!;
        using StreamReader oReader = new(oFixture);
        Dictionary<string, CompiledSchema> oByToken = SchemaCompiler.Compile([oReader.ReadToEnd()])
            .ToDictionary(oSchema =>
            {
                ArrayType oArray = Assert.IsType<ArrayType>(Assert.Single(oSchema.Fields).Type);
                Assert.Equal(1UL, oArray.FixedCount);
                return Assert.IsType<PrimitiveType>(oArray.Element).Name;
            }, StringComparer.Ordinal);
        HashSet<string> astrTested = new(StringComparer.Ordinal);

        void Check(string strToken, string strCSharp, string strCodec, int nMinimumBytes)
        {
            Assert.True(astrTested.Add(strToken), $"Duplicate test for {strToken}");
            Assert.Equal(nMinimumBytes, PrimitiveCatalog.Get(strToken).MinimumEncodedBytes);
            CompiledSchema oSchema = oByToken[strToken];
            Assert.Equal($"chondrite-schema 1\nschema {oSchema.Name}\n{{\n    0: values {strToken}[1]\n}}\n",
                oSchema.CanonicalText);

            string strCode = CSharpGenerator.Generate(oSchema);
            Assert.Contains($"ImmutableArray<{strCSharp}> values", strCode);
            Assert.Contains($"{strCSharp} oElement0 = oReader.Read{strCodec}();", strCode);
            string strWriteArgument = strToken == "bytes" ? "oElement0.AsSpan()" : "oElement0";
            Assert.Contains($"oWriter.Write{strCodec}({strWriteArgument});", strCode);
            if (strToken == "bytes")
                Assert.Contains("if (oElement0.IsDefault)\n                throw new InvalidDataException(\"Uninitialized byte array.\");", strCode);
            Assert.Contains($"oReader.GuardCollectionCount(nCount0, {nMinimumBytes.ToString(CultureInfo.InvariantCulture)});", strCode);
        }

        Check("bool", "bool", "Bool", 1);
        Check("int8", "sbyte", "Int8", 1);
        Check("uint8", "byte", "UInt8", 1);
        Check("int16", "short", "Int16", 2);
        Check("uint16", "ushort", "UInt16", 2);
        Check("int32", "int", "Int32", 4);
        Check("uint32", "uint", "UInt32", 4);
        Check("int64", "long", "Int64", 8);
        Check("uint64", "ulong", "UInt64", 8);
        Check("float32", "float", "Float32", 4);
        Check("float64", "double", "Float64", 8);
        Check("utf8", "string", "Utf8", 8);
        Check("bytes", "System.Collections.Immutable.ImmutableArray<byte>", "Bytes", 8);
        Check("vector2f", "global::System.Numerics.Vector2", "Vector2f", 8);
        Check("vector3f", "global::System.Numerics.Vector3", "Vector3f", 12);
        Check("vector4f", "global::System.Numerics.Vector4", "Vector4f", 16);
        Check("guid", "System.Guid", "Guid", 16);
        Check("nonce", "global::Chondrite.Store.Nonce", "Nonce", 16);
        Check("objectid", "global::Chondrite.Core.StorageObjectId", "ObjectId", 32);

        Assert.Equal(PrimitiveCatalog.Tokens.Order(StringComparer.Ordinal), astrTested.Order(StringComparer.Ordinal));
        Assert.Equal(PrimitiveCatalog.Tokens.Order(StringComparer.Ordinal), oByToken.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void UnrecognizedPrimitiveFailsExplicitlyWhileNamesRemainOrdinal()
    {
        const string strUnknown = "Uint32";
        const string strSource = "chondrite-schema 1\nschema Example.Probe { 0: value Uint32 }";
        SchemaField oField = Assert.Single(Assert.Single(SchemaParser.Parse(strSource)).Fields);
        Assert.Equal(strUnknown, Assert.IsType<ReferenceType>(oField.Type).Name);

        // A fabricated model can bypass the parser; the generator must still reject it.
        CompiledSchema oSchema = new("Example.Probe", [new(0, "values", new ArrayType(new PrimitiveType(strUnknown), null))],
            new Dictionary<string, CompiledSchema>(), []);
        NotSupportedException oException = Assert.Throws<NotSupportedException>(() => CSharpGenerator.Generate(oSchema));
        Assert.Contains($"Unknown primitive '{strUnknown}'", oException.Message);
    }

    [Fact]
    public void ReferenceAndZeroLengthFixedArrayElementsKeepTheirZeroBounds()
    {
        const string strSource = """
            chondrite-schema 1
            schema Example.Empty { }
            schema Example.Probe
            {
                0: records Example.Empty[]
                1: emptyArrays uint32[0][]
                2: pairs uint32[2][]
            }
            """;
        string strCode = CSharpGenerator.Generate(SchemaCompiler.Compile([strSource])[^1]);
        Assert.Contains("oReader.GuardCollectionCount(nCount0, 0);", strCode);
        Assert.Contains("oReader.GuardCollectionCount(nCount1, 0);", strCode);
        Assert.Contains("oReader.GuardCollectionCount(nCount3, 8);", strCode);
    }
}
