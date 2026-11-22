using System.Text;
using Chondrite.Core;

namespace Chondrite.Compile;

/// <summary>
/// Reads a persisted canonical schema without needing its generated C# type or
/// its original authoring source. Nested references are resolved hash identities.
/// </summary>
public static class PublishedSchemaReader
{
    public static SchemaSource Read(StorageObject oSchema)
    {
        ArgumentNullException.ThrowIfNull(oSchema);
        if (oSchema.idType != SchemaConstants.c_idSchemaType)
            throw new InvalidDataException("Object is not an intrinsic schema definition.");
        string strText = SchemaConstants.c_oStrictUtf8.GetString(oSchema.abyData.AsSpan());
        SchemaSource oParsed = AssertSingle(SchemaParser.Parse(strText));
        // Canonical schema objects must not carry source aliases, comments, CRLF,
        // inconsistent field layout, or references resolved by mutable names.
        string strCanonical = FormatCanonical(oParsed);
        if (!string.Equals(strText, strCanonical, StringComparison.Ordinal))
            throw new InvalidDataException("Schema definition is not in canonical form.");
        return oParsed;
    }

    private static SchemaSource AssertSingle(IReadOnlyList<SchemaSource> aSchemas)
    {
        if (aSchemas.Count != 1)
            throw new InvalidDataException("A schema StorageObject must define exactly one schema.");
        return aSchemas[0];
    }

    private static string FormatCanonical(SchemaSource oSchema)
    {
        StringBuilder o = new();
        o.Append(SchemaConstants.c_strHeader).Append("\nschema ").Append(oSchema.Name).Append("\n{\n");
        foreach (SchemaField oField in oSchema.Fields)
        {
            o.Append("    ").Append(oField.Index.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Append(": ").Append(oField.Name).Append(' ')
                .Append(FormatType(oField.Type)).Append('\n');
        }
        o.Append("}\n");
        return o.ToString();
    }

    private static string FormatType(SchemaType oType) => oType switch
    {
        PrimitiveType oPrimitive => oPrimitive.Name,
        SchemaIdType oId => "@" + oId.Id,
        ArrayType oArray => FormatType(oArray.Element) +
            (oArray.FixedCount is null ? "[]" : "[" + oArray.FixedCount.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) + "]"),
        OptionalType oOptional => "optional<" + FormatType(oOptional.Inner) + ">",
        _ => throw new InvalidDataException("Canonical schemas may not contain unresolved names.")
    };
}
