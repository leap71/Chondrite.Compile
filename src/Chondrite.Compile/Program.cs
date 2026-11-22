using Chondrite.Compile;

if (args.Contains("--help", StringComparer.Ordinal) || args.Contains("-h", StringComparer.Ordinal))
{
    PrintUsage(Console.Out);
    return 0;
}

try
{
    string? strOutput = null;
    bool bEditable = false;
    List<string> astrPaths = [];
    List<string> astrInputLists = [];
    for (int i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--out":
                if (strOutput is not null)
                    throw new ArgumentException("Specify --out only once.");
                strOutput = ReadValue(args, ref i);
                break;
            case "--inputs":
                astrInputLists.Add(ReadValue(args, ref i));
                break;
            case "--editable":
                bEditable = true;
                break;
            default:
                if (args[i].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException($"Unknown option '{args[i]}'.");
                astrPaths.Add(args[i]);
                break;
        }
    }

    if (string.IsNullOrWhiteSpace(strOutput))
        throw new ArgumentException("An output directory is required (--out <directory>).");
    if (astrPaths.Count == 0 && astrInputLists.Count == 0)
        throw new ArgumentException("Specify schema files or --inputs <file-list>.");

    // A file list is UTF-8 with one literal path per line, without quoting.
    // Relative paths have the same working-directory basis as explicit arguments.
    foreach (string strList in astrInputLists)
        astrPaths.AddRange(File.ReadAllLines(strList, SchemaConstants.c_oStrictUtf8)
            .Where(strPath => strPath.Length != 0));

    IReadOnlyList<CompiledSchema> aSchemas = SchemaCompiler.Compile(
        astrPaths.Select(strPath => File.ReadAllText(strPath, SchemaConstants.c_oStrictUtf8)));
    SchemaOutputWriter.Write(Path.GetFullPath(strOutput), aSchemas, bEditable);
    foreach (CompiledSchema oSchema in aSchemas)
        Console.WriteLine($"{oSchema.Id}  {oSchema.Name}");
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
    oWriter.WriteLine("       dotnet Chondrite.Compile.dll --out <directory> [--editable] --inputs <file-list>");
    oWriter.WriteLine("Input lists contain one UTF-8 path per line, relative to the working directory or absolute.");
    oWriter.WriteLine("An empty input list removes this compiler's previous generated outputs. Explicit filenames may use any extension.");
}
