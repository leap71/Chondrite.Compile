using System.Globalization;
using System.Text.RegularExpressions;

namespace Chondrite.Compile;

/// <summary>Parser for Chondrite schema version 1.</summary>
public static class SchemaParser
{
    private static readonly Regex c_oSchema = new(
        @"^schema\s+(?<name>[A-Za-z_][A-Za-z0-9_.]*)\s*$", RegexOptions.CultureInvariant);
    private static readonly Regex c_oField = new(
        @"^(?<index>[0-9]+)\s*:\s*(?<name>[A-Za-z_][A-Za-z0-9_]*)\s+(?<type>.+)$",
        RegexOptions.CultureInvariant);

    public static IReadOnlyList<SchemaSource> Parse(string strSource)
    {
        ArgumentNullException.ThrowIfNull(strSource);
        string[] astrLines = strSource.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        List<string> astrClean = [];
        foreach (string strLine in astrLines)
        {
            // Comments have no significance for canonical schema identity.
            string str = strLine.Split("//", 2, StringSplitOptions.None)[0]
                .Replace("{", "\n{\n").Replace("}", "\n}\n")
                .Replace(";", "\n");
            foreach (string strPart in str.Split('\n'))
            {
                string strTrim = strPart.Trim();
                if (strTrim.Length != 0)
                    astrClean.Add(strTrim);
            }
        }
        if (astrClean.Count == 0 || astrClean[0] != SchemaConstants.c_strHeader)
            throw new FormatException($"Schema source must begin with '{SchemaConstants.c_strHeader}'.");

        List<SchemaSource> aSchemas = [];
        int nLine = 1;
        while (nLine < astrClean.Count)
        {
            Match oSchemaMatch = c_oSchema.Match(astrClean[nLine++]);
            if (!oSchemaMatch.Success)
                throw new FormatException("Expected a schema declaration.");
            string strName = oSchemaMatch.Groups["name"].Value;
            SchemaTypeParser.ValidateQualifiedName(strName);
            if (nLine >= astrClean.Count || astrClean[nLine++] != "{")
                throw new FormatException($"Schema {strName} must have an opening '{{' on its own line.");
            List<SchemaField> aFields = [];
            HashSet<string> astrNames = new(StringComparer.Ordinal);
            bool bClosed = false;
            while (nLine < astrClean.Count)
            {
                string str = astrClean[nLine++];
                if (str == "}")
                {
                    bClosed = true;
                    break;
                }
                Match oField = c_oField.Match(str);
                if (!oField.Success || !int.TryParse(oField.Groups["index"].Value,
                    NumberStyles.None, CultureInfo.InvariantCulture, out int nIndex))
                    throw new FormatException($"Invalid field in schema {strName}: {str}");
                if (nIndex != aFields.Count)
                    throw new FormatException($"Field positions in {strName} must be consecutive, starting at zero.");
                string strFieldName = oField.Groups["name"].Value;
                if (!astrNames.Add(strFieldName))
                    throw new FormatException($"Duplicate field name '{strFieldName}' in {strName}.");
                SchemaType oType = SchemaTypeParser.Parse(oField.Groups["type"].Value);
                aFields.Add(new SchemaField(nIndex, strFieldName, oType));
            }
            if (!bClosed)
                throw new FormatException($"Unterminated schema {strName}.");
            aSchemas.Add(new SchemaSource(strName, aFields));
        }
        return aSchemas;
    }
}

internal sealed class SchemaTypeParser
{
    private readonly string m_str;
    private int m_nPos;

    private SchemaTypeParser(string str) => m_str = str;

    internal static SchemaType Parse(string str)
    {
        SchemaTypeParser oParser = new(str);
        SchemaType oResult = oParser.ParseType();
        oParser.SkipSpace();
        if (oParser.m_nPos != oParser.m_str.Length)
            throw new FormatException($"Unexpected characters in type '{str}'.");
        return oResult;
    }

    internal static void ValidateQualifiedName(string strName)
    {
        string[] astrParts = strName.Split('.');
        if (astrParts.Any(x => !Regex.IsMatch(x, @"^[A-Za-z_][A-Za-z0-9_]*$")))
            throw new FormatException($"Invalid schema name '{strName}'.");
    }

    private void SkipSpace()
    {
        while (m_nPos < m_str.Length && char.IsWhiteSpace(m_str[m_nPos]))
            m_nPos++;
    }
    private bool Consume(char c)
    {
        SkipSpace();
        if (m_nPos < m_str.Length && m_str[m_nPos] == c)
        {
            m_nPos++;
            return true;
        }
        return false;
    }
    private void Expect(char c)
    {
        if (!Consume(c))
            throw new FormatException($"Expected '{c}' in '{m_str}'.");
    }
    private string Name()
    {
        SkipSpace();
        int nStart = m_nPos;
        while (m_nPos < m_str.Length &&
            ((m_str[m_nPos] >= 'A' && m_str[m_nPos] <= 'Z') ||
             (m_str[m_nPos] >= 'a' && m_str[m_nPos] <= 'z') ||
             (m_str[m_nPos] >= '0' && m_str[m_nPos] <= '9') ||
             m_str[m_nPos] is '_' or '.'))
            m_nPos++;
        if (nStart == m_nPos)
            throw new FormatException($"Expected a type name in '{m_str}'.");
        string strName = m_str[nStart..m_nPos];
        ValidateQualifiedName(strName);
        return strName;
    }

    private SchemaType ParseType()
    {
        SkipSpace();
        if (Consume('@'))
        {
            SkipSpace();
            int nStart = m_nPos;
            while (m_nPos < m_str.Length && Uri.IsHexDigit(m_str[m_nPos]))
                m_nPos++;
            if (m_nPos - nStart != 64)
                throw new FormatException("Schema reference must be exactly 64 hexadecimal digits.");
            SchemaType oHashed = new SchemaIdType(
                new Chondrite.Core.StorageType(new Chondrite.Core.StorageObjectId(
                    Convert.FromHexString(m_str[nStart..m_nPos]))));
            return ParseArraySuffixes(oHashed);
        }
        string strName = Name();
        SchemaType oType;
        if (strName == "optional" && Consume('<'))
        {
            SchemaType oInner = ParseType();
            Expect('>');
            if (oInner is OptionalType)
                throw new FormatException("Nested optional<optional<T>> is unsupported.");
            oType = new OptionalType(oInner);
        }
        else if (PrimitiveCatalog.Contains(strName))
            oType = new PrimitiveType(strName);
        else
            oType = new ReferenceType(strName);

        return ParseArraySuffixes(oType);
    }

    private SchemaType ParseArraySuffixes(SchemaType oType)
    {
        while (Consume('['))
        {
            SkipSpace();
            ulong? nLength = null;
            if (!Consume(']'))
            {
                int nStart = m_nPos;
                while (m_nPos < m_str.Length && m_str[m_nPos] is >= '0' and <= '9')
                    m_nPos++;
                if (!ulong.TryParse(m_str[nStart..m_nPos],
                        NumberStyles.None, CultureInfo.InvariantCulture, out ulong n))
                    throw new FormatException("Invalid fixed-array length.");
                nLength = n;
                Expect(']');
            }
            oType = new ArrayType(oType, nLength);
        }
        return oType;
    }
}
