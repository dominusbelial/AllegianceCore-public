using System.Diagnostics;

namespace Allegiance.Core.Tests;

public class P674ParityScorecardTests
{
    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "scripts", "parity-scorecard.py")))
                return directory.FullName;
        throw new DirectoryNotFoundException("Repository parity-scorecard.py not found");
    }

    private static (int ExitCode, string Output) Run(params string[] arguments)
    {
        var start = new ProcessStartInfo("python3")
        {
            WorkingDirectory = RepositoryRoot(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(Path.Combine(start.WorkingDirectory, "scripts", "parity-scorecard.py"));
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(30_000), "Parity validation timed out");
        return (process.ExitCode, output.GetAwaiter().GetResult() + errors.GetAwaiter().GetResult());
    }

    [Fact]
    public void Both_repository_matrices_validate_and_print_scorecards()
    {
        var result = Run();
        Assert.True(result.ExitCode == 0, result.Output);
        Assert.Contains("parity-matrix.csv: validated", result.Output);
        Assert.Contains("sim-rules-matrix.csv: validated", result.Output);
        Assert.Contains("TOTAL |", result.Output);
    }

    [Theory]
    [InlineData("area,item,native,done,reason,5,extra", "columns")]
    [InlineData("area,item,native,done,reason, sort by score", "weight")]
    [InlineData("area,item,native,done,reason,NaN", "weight")]
    [InlineData("area,item,native,done,reason,0", "weight")]
    [InlineData("area,item,native,complete,reason,5", "status")]
    [InlineData("area,item,native,done,\"unclosed,5", "unexpected end")]
    public void Malformed_rows_fail_validation(string row, string diagnostic)
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "area,native_item,native_cite,port_status,port_cite_or_reason,weight\n" + row + "\n");
            var result = Run(path);
            Assert.Equal(1, result.ExitCode);
            Assert.Contains(diagnostic, result.Output);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Quoted_commas_partial_zero_credit_and_both_na_spellings_score_correctly()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "area,native_item,native_cite,port_status,port_cite_or_reason,weight\n" +
                "area,one,native,done,\"filter, sort by score\",2\n" +
                "area,two,native,implemented,reason,1\n" +
                "area,three,native,partial,reason,5\n" +
                "area,four,native,missing,reason,2\n" +
                "area,five,native,na,reason,100\n" +
                "area,six,native,n/a,reason,100\n" +
                "excluded,seven,native,na,reason,5\n");
            var result = Run(path);
            Assert.True(result.ExitCode == 0, result.Output);
            Assert.Contains("area | 30.00% (3/10) | 50.00% (2/4)", result.Output);
            Assert.Contains("TOTAL | 30.00% (3/10) | 50.00% (2/4)", result.Output);
            Assert.Contains("excluded | n/a (0/0) | n/a (0/0)", result.Output);
        }
        finally { File.Delete(path); }
    }
}
