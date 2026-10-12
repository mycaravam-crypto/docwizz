namespace DocWizz.Tests.Unit;

public class CliProgressTests
{
    [Fact]
    public void Redirected_progress_has_stable_lines_without_terminal_control_codes()
    {
        var output = new StringWriter();
        using (var bar = new CliProgress(output, 3))
        {
            bar.Advance("scan");
            bar.Advance("analyze");
            bar.Advance("write");
        }
        var text = output.ToString();
        Assert.Contains("[========================] 3/3 write", text);
        Assert.Equal(3, text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.DoesNotContain("\r", text, StringComparison.Ordinal);
        // Ordinal: culture-aware comparison ignores control characters and "finds" ESC anywhere.
        Assert.DoesNotContain("\u001b", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Disabled_progress_keeps_output_empty()
    {
        var output = new StringWriter();
        using (var bar = new CliProgress(output, 2, enabled: false))
            bar.Advance("scan");
        Assert.Equal("", output.ToString());
    }

    [Fact]
    public void Terminal_progress_finishes_each_stage_with_newline()
    {
        var output = new StringWriter();
        using (var bar = new CliProgress(output, 2))
        {
            bar.Advance("scan");
            bar.Advance("write");
        }
        Assert.Contains("docwizz [========================] 2/2 write", output.ToString());
        Assert.DoesNotContain("\r", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b", output.ToString(), StringComparison.Ordinal);
        Assert.EndsWith(Environment.NewLine, output.ToString());
    }

    [Fact]
    public void Weighted_stages_fill_the_bar_by_share_of_work_and_carry_their_summary()
    {
        var output = new StringWriter();
        using (var bar = new CliProgress(output, [new("scan", 3), new("write", 1)]))
        {
            bar.Advance("scan", "12 files");
            bar.Advance("write");
        }
        var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("docwizz [==================------] 1/2 scan (", lines[0]);   // 3 of 4 weights: 18 of 24
        Assert.EndsWith(", 12 files)", lines[0]);
        Assert.StartsWith("docwizz [========================] 2/2 write", lines[1]);
    }

    [Fact]
    public void A_long_stage_says_when_it_starts_so_a_log_is_not_silent_until_it_ends()
    {
        var output = new StringWriter();
        using (var bar = new CliProgress(output, [new("scan", 8, "Scanning code"), new("write", 2)]))
        {
            bar.Begin("scan");
            bar.Report(5, 10, "C# parsing 5/10");
            bar.Advance("scan");
            bar.Begin("write");   // a short stage: no start line
            bar.Advance("write");
        }
        var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(["docwizz Scanning code…"], lines.Where(l => !l.StartsWith("docwizz [")));
        Assert.Equal(3, lines.Length);
    }

    [Fact]
    public void Durations_are_recorded_even_when_nothing_is_drawn()
    {
        using var bar = new CliProgress(TextWriter.Null, [new("scan"), new("write")], enabled: false);
        bar.Begin("scan");
        bar.Advance("scan");
        bar.Advance("write");
        Assert.Equal(["scan", "write"], bar.Durations.Select(d => d.Stage));
    }

    [Fact]
    public void A_nested_progress_writes_nothing_itself()
    {
        var outer = new StringWriter();
        var inner = new StringWriter();
        using var parent = new CliProgress(outer, [new("generate")]);
        using (var child = new CliProgress(inner, [new("scan", 1, "Scanning code"), new("write")], parent: parent))
        {
            child.Begin("scan", "C# parsing 1/2");
            child.Advance("scan");
        }
        Assert.Equal("", inner.ToString());
        Assert.Equal("", outer.ToString());   // the parent shows the child's detail only on its live bar
    }

    [Theory]
    [InlineData(0.42, "0.4s")]
    [InlineData(12.4, "12s")]
    [InlineData(75, "1m 15s")]
    public void Durations_read_at_a_glance(double seconds, string expected) =>
        Assert.Equal(expected, CliProgress.Duration(TimeSpan.FromSeconds(seconds)));
}
