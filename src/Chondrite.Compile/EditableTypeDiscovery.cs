using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Chondrite.Compile;

/// <summary>
/// Finds editable type declarations in authored C# source without compiling the project.
/// </summary>
public static class EditableTypeDiscovery
{
    /// <summary>
    /// Returns the expected types already declared as non-file-local partial record structs.
    /// </summary>
    /// <param name="astrSourcePaths">Authored Compile inputs, excluding Chondrite-generated files.</param>
    /// <param name="astrExpectedTypeNames">Namespace-qualified C# names using identifier values, without @ escapes.</param>
    /// <param name="strLanguageVersion">The project's C# language version, or null for the compiler default.</param>
    /// <param name="astrPreprocessorSymbols">The project's active conditional-compilation symbols.</param>
    /// <remarks>
    /// A matching empty declaration is sufficient. Members and referenced types need not
    /// resolve before schema generation. Discovery concerns only the supplied build context;
    /// it does not infer ownership in other configurations or source-generator output.
    /// </remarks>
    public static IReadOnlySet<string> FindProvidedTypes(
        IEnumerable<string> astrSourcePaths,
        IEnumerable<string> astrExpectedTypeNames,
        string? strLanguageVersion = null,
        IEnumerable<string>? astrPreprocessorSymbols = null)
    {
        ArgumentNullException.ThrowIfNull(astrSourcePaths);
        ArgumentNullException.ThrowIfNull(astrExpectedTypeNames);

        HashSet<string> astrExpected = new(StringComparer.Ordinal);
        foreach (string strTypeName in astrExpectedTypeNames)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(strTypeName);
            astrExpected.Add(strTypeName);
        }

        CSharpParseOptions oOptions = oCreateParseOptions(strLanguageVersion, astrPreprocessorSymbols);
        HashSet<string> astrProvided = new(StringComparer.Ordinal);
        HashSet<string> astrReadPaths = new(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        foreach (string strSourcePath in astrSourcePaths)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(strSourcePath);
            string strPath = Path.GetFullPath(strSourcePath);
            if (!astrReadPaths.Add(strPath))
                continue;

            string strSource;
            try
            {
                strSource = File.ReadAllText(strPath);
            }
            catch (Exception oException) when (oException is FileNotFoundException or DirectoryNotFoundException)
            {
                throw new FileNotFoundException(
                    $"Authored C# source '{strPath}' was not found during editable-type discovery.",
                    strPath, oException);
            }
            catch (Exception oException) when (oException is IOException or UnauthorizedAccessException)
            {
                throw new IOException(
                    $"Could not read authored C# source '{strPath}' during editable-type discovery: {oException.Message}",
                    oException);
            }

            SyntaxTree oTree = CSharpSyntaxTree.ParseText(strSource, oOptions, strPath);
            Diagnostic? oError = oTree.GetDiagnostics()
                .FirstOrDefault(oDiagnostic => oDiagnostic.Severity == DiagnosticSeverity.Error);
            if (oError is not null)
                throw oSourceError(strPath, oError.Location,
                    "Cannot determine editable-type ownership because the C# source contains an error: " +
                    $"{oError.Id}: {oError.GetMessage(CultureInfo.InvariantCulture)}");

            CompilationUnitSyntax oRoot = oTree.GetCompilationUnitRoot();
            FindInMembers(oRoot.Members, string.Empty, astrExpected, astrProvided, strPath);
        }

        return astrProvided;
    }

    private static CSharpParseOptions oCreateParseOptions(
        string? strLanguageVersion,
        IEnumerable<string>? astrPreprocessorSymbols)
    {
        LanguageVersion eLanguageVersion = LanguageVersion.Default;
        if (!string.IsNullOrWhiteSpace(strLanguageVersion) &&
            !LanguageVersionFacts.TryParse(strLanguageVersion, out eLanguageVersion))
            throw new ArgumentException(
                $"Unsupported C# language version '{strLanguageVersion}'.", nameof(strLanguageVersion));

        CSharpParseOptions oOptions = new(
            eLanguageVersion,
            DocumentationMode.Parse,
            SourceCodeKind.Regular,
            astrPreprocessorSymbols);
        Diagnostic? oError = oOptions.Errors.FirstOrDefault();
        if (oError is not null)
            throw new ArgumentException(
                "Invalid C# parse options for editable-type discovery: " +
                oError.GetMessage(CultureInfo.InvariantCulture), nameof(astrPreprocessorSymbols));
        return oOptions;
    }

    private static void FindInMembers(
        SyntaxList<MemberDeclarationSyntax> aMembers,
        string strNamespace,
        IReadOnlySet<string> astrExpected,
        HashSet<string> astrProvided,
        string strPath)
    {
        foreach (MemberDeclarationSyntax oMember in aMembers)
        {
            if (oMember is BaseNamespaceDeclarationSyntax oNamespace)
            {
                string strName = string.Join(".", oNamespace.Name.DescendantTokens()
                    .Where(oToken => oToken.IsKind(SyntaxKind.IdentifierToken))
                    .Select(oToken => oToken.ValueText));
                string strChildNamespace = strNamespace.Length == 0
                    ? strName
                    : strNamespace + "." + strName;
                FindInMembers(oNamespace.Members, strChildNamespace, astrExpected, astrProvided, strPath);
                continue;
            }

            SyntaxToken oIdentifier;
            SyntaxTokenList aModifiers;
            int nArity;
            switch (oMember)
            {
                case BaseTypeDeclarationSyntax oType:
                    oIdentifier = oType.Identifier;
                    aModifiers = oType.Modifiers;
                    nArity = oType is TypeDeclarationSyntax oGenericType
                        ? oGenericType.TypeParameterList?.Parameters.Count ?? 0
                        : 0;
                    break;
                case DelegateDeclarationSyntax oDelegate:
                    oIdentifier = oDelegate.Identifier;
                    aModifiers = oDelegate.Modifiers;
                    nArity = oDelegate.TypeParameterList?.Parameters.Count ?? 0;
                    break;
                default:
                    continue;
            }

            // Generic siblings are different types. Do not descend into type bodies:
            // a nested declaration cannot own the generated top-level schema type.
            if (nArity != 0)
                continue;
            string strTypeName = strNamespace.Length == 0
                ? oIdentifier.ValueText
                : strNamespace + "." + oIdentifier.ValueText;
            if (!astrExpected.Contains(strTypeName))
                continue;

            string? strProblem = null;
            if (aModifiers.Any(SyntaxKind.FileKeyword))
                strProblem = "a file-local type cannot combine with generated declarations in another file";
            else if (oMember is not RecordDeclarationSyntax oRecord ||
                     !oRecord.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword))
                strProblem = $"found an incompatible {oMember.Kind()} declaration";
            else if (!aModifiers.Any(SyntaxKind.PartialKeyword))
                strProblem = "the record struct declaration is missing the partial modifier";

            if (strProblem is not null)
                throw oSourceError(strPath, oIdentifier.GetLocation(),
                    $"Editable declaration '{strTypeName}' must be a non-file-local partial record struct: {strProblem}.");
            astrProvided.Add(strTypeName);
        }
    }

    private static FormatException oSourceError(string strPath, Location oLocation, string strMessage)
    {
        FileLinePositionSpan oPosition = oLocation.GetLineSpan();
        return new FormatException(
            $"{strPath}({oPosition.StartLinePosition.Line + 1},{oPosition.StartLinePosition.Character + 1}): {strMessage}");
    }
}
