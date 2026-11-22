using Chondrite.Compile;

if (args.Contains("--help", StringComparer.Ordinal) || args.Contains("-h", StringComparer.Ordinal))
{
    PrintUsage(Console.Out);
    return 0;
}

try
{
    string? strOutput = null;
    string? strEditableOutput = null;
    string? strEditableManifest = null;
    string? strLanguageVersion = null;
    bool bEditable = false;
    bool bEditableOnly = false;
    List<string> astrPaths = [];
    List<string> astrInputLists = [];
    List<string> astrCSharpLists = [];
    List<string> astrProvidedLists = [];
    List<string> astrDefinesLists = [];
    List<string> astrPreexistingLists = [];
    for (int i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--out":
                ReadSingle(args, ref i, ref strOutput);
                break;
            case "--inputs":
                astrInputLists.Add(ReadValue(args, ref i));
                break;
            case "--editable":
                bEditable = true;
                break;
            case "--editable-out":
                ReadSingle(args, ref i, ref strEditableOutput);
                bEditable = true;
                break;
            case "--editable-only":
                bEditableOnly = bEditable = true;
                break;
            case "--csharp-inputs":
                astrCSharpLists.Add(ReadValue(args, ref i));
                break;
            case "--provided-types":
                astrProvidedLists.Add(ReadValue(args, ref i));
                break;
            case "--language-version":
                ReadSingle(args, ref i, ref strLanguageVersion);
                break;
            case "--defines-file":
                astrDefinesLists.Add(ReadValue(args, ref i));
                break;
            case "--editable-manifest":
                ReadSingle(args, ref i, ref strEditableManifest);
                break;
            case "--preexisting-files":
                astrPreexistingLists.Add(ReadValue(args, ref i));
                break;
            default:
                if (args[i].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException($"Unknown option '{args[i]}'.");
                astrPaths.Add(args[i]);
                break;
        }
    }

    if (!bEditableOnly && string.IsNullOrWhiteSpace(strOutput))
        throw new ArgumentException("An output directory is required (--out <directory>).");
    if (bEditableOnly && string.IsNullOrWhiteSpace(strEditableOutput))
        throw new ArgumentException("Editable-only generation requires --editable-out <directory>.");
    if (bEditableOnly && strOutput is not null)
        throw new ArgumentException("Use --editable-out, without --out, for editable-only generation.");
    if (!bEditable && (astrCSharpLists.Count != 0 || astrProvidedLists.Count != 0 ||
        astrDefinesLists.Count != 0 || astrPreexistingLists.Count != 0 ||
        strLanguageVersion is not null || strEditableManifest is not null))
        throw new ArgumentException("C# discovery options require --editable or --editable-out.");
    if (astrPaths.Count == 0 && astrInputLists.Count == 0)
        throw new ArgumentException("Specify schema files or --inputs <file-list>.");

    // A file list is UTF-8 with one literal path per line, without quoting.
    // Relative paths have the same working-directory basis as explicit arguments.
    astrPaths.AddRange(ReadLists(astrInputLists));

    IReadOnlyList<CompiledSchema> aSchemas = SchemaCompiler.Compile(
        astrPaths.Select(strPath => File.ReadAllText(strPath, SchemaConstants.c_oStrictUtf8)));
    if (bEditable)
    {
        string[]? astrSources = astrCSharpLists.Count == 0 ? null : ReadLists(astrCSharpLists).ToArray();
        string[] astrSymbols = ReadLists(astrDefinesLists)
            .SelectMany(strLine => strLine.Split([',', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .ToArray();
        IReadOnlyList<string> astrEditable = EditableScaffolder.Ensure(
            strEditableOutput ?? strOutput!, aSchemas, astrSources,
            ReadLists(astrProvidedLists), strLanguageVersion, astrSymbols,
            astrPreexistingLists.Count == 0 ? null : ReadLists(astrPreexistingLists));
        if (strEditableManifest is not null)
        {
            string strManifest = Path.GetFullPath(strEditableManifest);
            Directory.CreateDirectory(Path.GetDirectoryName(strManifest)!);
            SchemaOutputWriter.WriteIfDifferent(strManifest,
                astrEditable.Count == 0 ? string.Empty : string.Join('\n', astrEditable) + "\n");
        }
    }
    if (!bEditableOnly)
    {
        SchemaOutputWriter.Write(Path.GetFullPath(strOutput!), aSchemas);
        foreach (CompiledSchema oSchema in aSchemas)
            Console.WriteLine($"{oSchema.Id}  {oSchema.Name}");
    }
    return 0;
}
catch (ArgumentException oException)
{
    Console.Error.WriteLine(oException.Message);
    PrintUsage(Console.Error);
    return 2;
}
catch (Exception oException)
{
    Console.Error.WriteLine(oException.Message);
    return 1;
}

static IEnumerable<string> ReadLists(IEnumerable<string> astrLists) =>
    astrLists.SelectMany(strPath => File.ReadAllLines(strPath, SchemaConstants.c_oStrictUtf8))
        .Where(strLine => strLine.Length != 0);

static void ReadSingle(string[] astrArguments, ref int nIndex, ref string? strValue)
{
    if (strValue is not null)
        throw new ArgumentException($"Specify {astrArguments[nIndex]} only once.");
    strValue = ReadValue(astrArguments, ref nIndex);
}

static string ReadValue(string[] astrArguments, ref int nIndex)
{
    string strOption = astrArguments[nIndex];
    if (nIndex + 1 >= astrArguments.Length || astrArguments[nIndex + 1].StartsWith("--", StringComparison.Ordinal))
        throw new ArgumentException($"Option '{strOption}' requires a value.");
    return astrArguments[++nIndex];
}

static void PrintUsage(TextWriter oWriter)
{
    oWriter.WriteLine("Usage: dotnet Chondrite.Compile.dll --out <directory> [--editable] <file.chondrite>...");
    oWriter.WriteLine("       dotnet Chondrite.Compile.dll --out <directory> [--editable-out <source-directory>] --inputs <file-list>");
    oWriter.WriteLine("       dotnet Chondrite.Compile.dll --editable-only --editable-out <source-directory> --inputs <file-list>");
    oWriter.WriteLine("Editable discovery: --csharp-inputs <authored-source-list>, --provided-types <schema-name-list>,");
    oWriter.WriteLine("                    --language-version <version>, --defines-file <symbol-list>, --editable-manifest <output-list>.");
    oWriter.WriteLine("MSBuild creation coordination: --preexisting-files <project-evaluation-source-list>.");
    oWriter.WriteLine("Lists are UTF-8, one unquoted entry per line; relative paths resolve from the working directory.");
    oWriter.WriteLine("Without --csharp-inputs, editable discovery scans the editable directory, excluding *.g.cs.");
    oWriter.WriteLine("An empty schema list clears previous generated outputs, but never removes editable sources.");
}
