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
/// <summary>
/// One resolved, immutable schema snapshot. Canonical text, object, and identity
/// are calculated together and cannot be supplied independently by callers.
/// </summary>
public sealed class CompiledSchema
{
    public string Name { get; }
    public IReadOnlyList<SchemaField> Fields { get; }
    public string CanonicalText { get; }
    public StorageObject SchemaObject { get; }
    public StorageObjectId Id { get; }
    public IReadOnlyList<string> Dependencies { get; }

    internal CompiledSchema(string strName, IEnumerable<SchemaField> aFields,
        IReadOnlyDictionary<string, CompiledSchema> oResolved,
        IEnumerable<string> astrDependencies)
    {
        Name = strName;
        Fields = ImmutableArray.CreateRange(aFields);
        Dependencies = ImmutableArray.CreateRange(astrDependencies);
        CanonicalText = SchemaCompiler.strFormatCanonical(Name, Fields, oResolved);
        SchemaObject = new(StorageType.idIntrinsicSchema,
            ImmutableArray.CreateRange(SchemaConstants.c_oStrictUtf8.GetBytes(CanonicalText)));
        Id = SchemaObject.idCalculateIdentity();
    }
}

public static class SchemaConstants
{
    public const string c_strHeader = "chondrite-schema 1";

    /// <summary>
    /// Intrinsic, reserved type ID for canonical schema definitions (31 zero bytes, then 01).
    /// Zero is invalid as a type identity. This is a schema-layer convention.
    /// </summary>
    public static readonly StorageType c_idSchemaType = StorageType.idIntrinsicSchema;

    public static readonly UTF8Encoding c_oStrictUtf8 = new(false, true);
}
