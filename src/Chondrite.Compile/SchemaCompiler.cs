using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Chondrite.Core;

namespace Chondrite.Compile;

/// <summary>
/// Deterministically resolves schemas into canonical UTF-8 definitions and
/// published StorageObject identities. Compilation never writes to storage.
/// </summary>
public static class SchemaCompiler
{
    public static IReadOnlyList<CompiledSchema> Compile(IEnumerable<string> astrSources)
    {
        Dictionary<string, SchemaSource> oSources = new(StringComparer.Ordinal);
        foreach (string strSource in astrSources)
        foreach (SchemaSource oSchema in SchemaParser.Parse(strSource))
        {
            if (!oSources.TryAdd(oSchema.Name, oSchema))
                throw new FormatException($"Duplicate schema '{oSchema.Name}'.");
        }
        Dictionary<string, CompiledSchema> oCompiled = new(StringComparer.Ordinal);
        HashSet<string> oActive = new(StringComparer.Ordinal);
        List<CompiledSchema> aOrdered = [];

        CompiledSchema CompileOne(string strName)
        {
            if (oCompiled.TryGetValue(strName, out CompiledSchema? oDone))
                return oDone;
            if (!oSources.TryGetValue(strName, out SchemaSource? oSource))
                throw new FormatException($"Unknown referenced schema '{strName}'.");
            if (!oActive.Add(strName))
                throw new FormatException($"Circular schema dependency involving '{strName}'.");

            HashSet<string> oDependencies = new(StringComparer.Ordinal);
            foreach (SchemaField oField in oSource.Fields)
                GatherReferences(oField.Type, oDependencies);
            foreach (string strDep in oDependencies.OrderBy(x => x, StringComparer.Ordinal))
                CompileOne(strDep);

            CompiledSchema oResult = new(strName, oSource.Fields, oCompiled,
                oDependencies.OrderBy(x => x, StringComparer.Ordinal));
            oCompiled.Add(strName, oResult);
            aOrdered.Add(oResult);
            oActive.Remove(strName);
            return oResult;
        }

        foreach (string strName in oSources.Keys.OrderBy(x => x, StringComparer.Ordinal))
            CompileOne(strName);
        return aOrdered;
    }

    internal static string strFormatCanonical(string strName,
        IReadOnlyList<SchemaField> aFields,
        IReadOnlyDictionary<string, CompiledSchema> oResolved)
    {
        StringBuilder oCanonical = new();
        oCanonical.Append(SchemaConstants.c_strHeader).Append('\n');
        oCanonical.Append("schema ").Append(strName).Append("\n{\n");
        foreach (SchemaField oField in aFields)
        {
            oCanonical.Append("    ").Append(oField.Index.ToString(CultureInfo.InvariantCulture))
                .Append(": ").Append(oField.Name).Append(' ')
                .Append(FormatType(oField.Type, oResolved)).Append('\n');
        }
        oCanonical.Append("}\n");
        return oCanonical.ToString();
    }

    private static void GatherReferences(SchemaType oType, HashSet<string> oNames)
    {
        switch (oType)
        {
            case ReferenceType oRef: oNames.Add(oRef.Name); break;
            case SchemaIdType: throw new FormatException(
                "Direct schema hashes are reserved for canonical definitions; author with schema names.");
            case ArrayType oArray: GatherReferences(oArray.Element, oNames); break;
            case OptionalType oOptional: GatherReferences(oOptional.Inner, oNames); break;
        }
    }

    private static string FormatType(
        SchemaType oType, IReadOnlyDictionary<string, CompiledSchema> oCompiled) => oType switch
    {
        PrimitiveType oPrimitive => oPrimitive.Name,
        ReferenceType oReference => "@" + oCompiled[oReference.Name].Id.ToString(),
        SchemaIdType oId => "@" + oId.Id.ToString(),
        ArrayType oArray => FormatType(oArray.Element, oCompiled) +
            (oArray.FixedCount is null ? "[]" : $"[{oArray.FixedCount.Value}]"),
        OptionalType oOptional => "optional<" + FormatType(oOptional.Inner, oCompiled) + ">",
        _ => throw new NotSupportedException()
    };
}
