using System.Text;
using GhostShow.Core.Inputs;
using GhostShow.Inputs;

namespace GhostShow.Tests.Inputs;

public class SerialLineSplitterTests
{
    static IReadOnlyList<string> Add(SerialLineSplitter splitter, string text) => splitter.Add(Encoding.Latin1.GetBytes(text));

    [Fact]
    public void A_command_ends_at_CR_LF_or_both_and_blank_lines_and_spaces_are_dropped()
    {
        var splitter = new SerialLineSplitter();
        Assert.Equal(["channel 1 1", "channel 1 0", "GO", "STOP"], Add(splitter, "channel 1 1\rchannel 1 0\n  GO \r\n\r\n\nSTOP\r"));
    }

    [Fact]
    public void A_command_split_across_reads_is_joined()
    {
        var splitter = new SerialLineSplitter();
        Assert.Empty(Add(splitter, "chan"));
        Assert.Empty(Add(splitter, "nel 1"));
        Assert.Equal(["channel 1"], Add(splitter, "\r"));
    }

    [Fact]
    public void Without_a_line_ending_it_is_taken_when_the_line_goes_quiet_or_gets_too_long()
    {
        var splitter = new SerialLineSplitter();
        Add(splitter, "A");
        Assert.Equal("A", splitter.Flush());
        Assert.Null(splitter.Flush());

        var noise = Add(splitter, new string('x', SerialLineSplitter.MaxLength + 5));
        Assert.Equal(SerialLineSplitter.MaxLength, Assert.Single(noise).Length);
        Assert.Equal("xxxxx", splitter.Flush());
    }

    [Fact]
    public void Control_characters_are_shown_as_their_codes()
    {
        Assert.Equal("GO", InputText.Visible("GO"));
        Assert.Equal("GO[1B][00]", InputText.Visible("GO\u001b\0"));
    }
}
