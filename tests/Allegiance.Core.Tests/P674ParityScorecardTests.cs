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

    [Fact]
    public void DoneVisual_rows_get_full_credit_and_appear_in_the_visual_column()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "area,native_item,native_cite,port_status,port_cite_or_reason,weight\n" +
                "area,one,native,done,reason,2\n" +
                "area,two,native,done-visual,\"VISUAL: font shade differs\",3\n" +
                "area,three,native,partial,reason,5\n");
            var result = Run(path);
            Assert.True(result.ExitCode == 0, result.Output);
            Assert.Contains("area | 50.00% (5/10) | 66.67% (2/3) | visual 3", result.Output);
            Assert.Contains("TOTAL | 50.00% (5/10) | 66.67% (2/3) | visual 3", result.Output);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void DoneVisual_without_VISUAL_prefix_is_rejected()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "area,native_item,native_cite,port_status,port_cite_or_reason,weight\n" +
                "area,one,native,done-visual,missing prefix,3\n");
            var result = Run(path);
            Assert.Equal(1, result.ExitCode);
            Assert.Contains("must start its reason with 'VISUAL:'", result.Output);
        }
        finally { File.Delete(path); }
    }
    [Fact]
    public void Nonproduction_implementations_remain_in_denominator_without_credit()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "area,native_item,native_cite,port_status,port_cite_or_reason,weight,reachability\n" +
                "area,one,native,implemented,reason,2,production\n" +
                "area,two,native,implemented,reason,3,config-only\n" +
                "area,three,native,implemented,reason,5,test-only\n");
            var result = Run(path);
            Assert.Equal(0, result.ExitCode);
            Assert.Contains("TOTAL | 20.00% (2/10) | 33.33% (1/3)", result.Output);
            Assert.Contains("config-only: 1 rows; 1 eligible; 1 implemented; implemented weight 3", result.Output);
            Assert.Contains("test-only: 1 rows; 1 eligible; 1 implemented; implemented weight 5", result.Output);
            File.AppendAllText(path, "area,four,native,implemented,reason,1,unknown\n");
            Assert.Contains("invalid reachability", Run(path).Output);
        }
        finally { File.Delete(path); }
    }

}
