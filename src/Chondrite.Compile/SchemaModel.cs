using System.Collections.Immutable;
using System.Text;
using Chondrite.Core;

namespace Chondrite.Compile;

/// <summary>Language-neutral compiler representation; not a runtime persistence object.</summary>
public abstract record SchemaType;
public sealed record PrimitiveType(string Name) : SchemaType;
public sealed record ReferenceType(string Name) : SchemaType;
public sealed record SchemaIdType(StorageType Id) : SchemaType;
public sealed record ArrayType(SchemaType Element, ulong? FixedCount) : SchemaType;
public sealed record OptionalType(SchemaType Inner) : SchemaType;

public sealed record SchemaField(int Index, string Name, SchemaType Type);
public sealed record SchemaSource(string Name, IReadOnlyList<SchemaField> Fields);
public sealed record CompiledSchema(
    string Name,
    IReadOnlyList<SchemaField> Fields,
    string CanonicalText,
    StorageObject SchemaObject,
    StorageObjectId Id,
    IReadOnlyList<string> Dependencies);

public static class SchemaConstants
{
    public const string c_strHeader = "chondrite-schema 1";

    /// <summary>
    /// Intrinsic, reserved type ID for canonical schema definitions (31 zero bytes, then 01).
    /// Zero is invalid as a type identity. This is a schema-layer convention.
    /// </summary>
    public static readonly StorageType c_idSchemaType = StorageType.idIntrinsicSchema;

    public static readonly UTF8Encoding c_oStrictUtf8 = new(false, true);

    public static readonly IReadOnlyDictionary<string, string> c_oPrimitiveToCSharp =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["bool"] = "bool", ["int8"] = "sbyte", ["uint8"] = "byte",
            ["int16"] = "short", ["uint16"] = "ushort", ["int32"] = "int",
            ["uint32"] = "uint", ["int64"] = "long", ["uint64"] = "ulong",
            ["float32"] = "float", ["float64"] = "double", ["utf8"] = "string",
            ["vector2f"] = "global::System.Numerics.Vector2",
            ["vector3f"] = "global::System.Numerics.Vector3",
            ["vector4f"] = "global::System.Numerics.Vector4",
            ["bytes"] = "System.Collections.Immutable.ImmutableArray<byte>",
            ["nonce"] = "global::Chondrite.Store.Nonce",
            ["guid"] = "System.Guid", ["objectid"] = "global::Chondrite.Core.StorageObjectId"
        };
}
