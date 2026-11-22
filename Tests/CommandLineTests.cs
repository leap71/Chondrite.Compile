using System.Diagnostics;
using Chondrite.Compile;
using Xunit;

namespace Chondrite.Compile.Tests;

public sealed class CommandLineTests : IDisposable
{
    private const string c_strSource =
        "chondrite-schema 1\nschema Chondrite.Examples.Pascals\n{\n    0: fValue float64\n}\n";

    private readonly string m_strDirectory = Path.Combine(
        Path.GetTempPath(), "Chondrite compiler tests", Guid.NewGuid().ToString("N"));

    public CommandLineTests() => Directory.CreateDirectory(m_strDirectory);

    [Fact]
    public async Task HelpDescribesDirectInvocationAndInputLists()
    {
        var oResult = await oRun("--help");

        Assert.Equal(0, oResult.nExitCode);
        Assert.Contains("dotnet Chondrite.Compile.dll", oResult.strOutput);
        Assert.Contains("--inputs", oResult.strOutput);
        Assert.Empty(oResult.strError);
    }

    [Theory]
    [InlineData("--out")]
    [InlineData("--inputs")]
    [InlineData("--unknown")]
    public async Task InvalidOptionsReturnUsageError(string strOption)
    {
        var oResult = await oRun(strOption);

        Assert.Equal(2, oResult.nExitCode);
        Assert.Contains(strOption, oResult.strError);
        Assert.Contains("Usage:", oResult.strError);
    }

    [Fact]
    public async Task ExplicitInputAcceptsAnyFileExtension()
    {
        string strSource = Path.Combine(m_strDirectory, "Pascals source.txt");
        File.WriteAllText(strSource, c_strSource);
        string strOutput = Path.Combine(m_strDirectory, "generated code");

        var oResult = await oRun("--out", strOutput, strSource);

        Assert.True(oResult.nExitCode == 0, oResult.strError);
        Assert.True(File.Exists(Path.Combine(strOutput, "Chondrite.Examples.Pascals.g.cs")));
        Assert.Contains("Chondrite.Examples.Pascals", oResult.strOutput);
    }

    [Fact]
    public async Task InputListSupportsSpacesAndRemovingTheFinalSchema()
    {
        string strSource = Path.Combine(m_strDirectory, "Pascals source.chondrite");
        File.WriteAllText(strSource, c_strSource);
        string strInputs = Path.Combine(m_strDirectory, "schema inputs.txt");
        File.WriteAllText(strInputs, strSource + "\n");
        string strOutput = Path.Combine(m_strDirectory, "generated code");

        var oFirst = await oRun("--out", strOutput, "--inputs", strInputs);
        Assert.True(oFirst.nExitCode == 0, oFirst.strError);
        Assert.True(File.Exists(Path.Combine(strOutput, "Chondrite.Examples.Pascals.g.cs")));

        File.WriteAllText(strInputs, string.Empty);
        var oEmpty = await oRun("--out", strOutput, "--inputs", strInputs);
        Assert.True(oEmpty.nExitCode == 0, oEmpty.strError);
        Assert.False(File.Exists(Path.Combine(strOutput, "Chondrite.Examples.Pascals.g.cs")));
        Assert.Empty(File.ReadAllText(Path.Combine(strOutput, "catalog.txt")));
    }

    [Fact]
    public async Task InvalidSourceLeavesPriorGeneratedFilesIntact()
    {
        string strSource = Path.Combine(m_strDirectory, "Pascals.chondrite");
        File.WriteAllText(strSource, c_strSource);
        string strOutput = Path.Combine(m_strDirectory, "generated");
        var oFirst = await oRun("--out", strOutput, strSource);
        Assert.True(oFirst.nExitCode == 0, oFirst.strError);
        string strCode = Path.Combine(strOutput, "Chondrite.Examples.Pascals.g.cs");
        string strOriginal = File.ReadAllText(strCode);

        File.WriteAllText(strSource, "chondrite-schema 1\nschema Invalid { 0: bad Unresolved }");
        var oInvalid = await oRun("--out", strOutput, strSource);

        Assert.Equal(1, oInvalid.nExitCode);
        Assert.Contains("Unresolved", oInvalid.strError);
        Assert.Equal(strOriginal, File.ReadAllText(strCode));
    }

    private static async Task<(int nExitCode, string strOutput, string strError)> oRun(params string[] astrArguments)
    {
        ProcessStartInfo oStart = new(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        oStart.ArgumentList.Add(typeof(SchemaCompiler).Assembly.Location);
        foreach (string strArgument in astrArguments)
            oStart.ArgumentList.Add(strArgument);

        using Process oProcess = Process.Start(oStart) ?? throw new InvalidOperationException("Could not start compiler.");
        CancellationToken oCancellation = TestContext.Current.CancellationToken;
        Task<string> oOutput = oProcess.StandardOutput.ReadToEndAsync(oCancellation);
        Task<string> oError = oProcess.StandardError.ReadToEndAsync(oCancellation);
        try
        {
            await oProcess.WaitForExitAsync(oCancellation);
            return (oProcess.ExitCode, await oOutput, await oError);
        }
        finally
        {
            if (!oProcess.HasExited)
                oProcess.Kill(entireProcessTree: true);
        }
    }

    public void Dispose() => Directory.Delete(m_strDirectory, recursive: true);
}
