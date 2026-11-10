using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace PepperDash.Essentials.Plugins.Lg.Display.Tests;

/// <summary>
/// Source-level guards for the display's feedback list. The controller needs a processor to run,
/// so these pin what a later edit could undo without anything else failing: monitoring consumers
/// read only keyed feedbacks on <c>Feedbacks</c>, and subscribe to the list once after activation.
/// </summary>
public class FeedbackListSourceTests
{
    // Keys the display base classes already put on the list.
    private static readonly string[] BaseKeys = { "IsCoolingDown", "IsWarmingUp", "currentInput", "PowerOnFeedback" };

    private static string Source()
    {
        var path = Path.Combine(AssemblyFixture.SourceDirectory, "LgDisplayController.cs");
        File.Exists(path).Should().BeTrue($"the source should be at {path}");
        return File.ReadAllText(path);
    }

    /// <summary>The text from the opening brace after <paramref name="signature"/> to its match.</summary>
    private static string Body(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        start.Should().BeGreaterThan(-1, $"'{signature}' should be in the source");

        var open = source.IndexOf('{', start);
        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source.Substring(open, i - open + 1);
        }

        throw new InvalidOperationException($"unbalanced braces after '{signature}'");
    }

    [Theory]
    [InlineData("MuteFeedback", "BoolFeedback", "muteOn")]
    [InlineData("VolumeLevelFeedback", "IntFeedback", "volume")]
    [InlineData("VideoMuteIsOn", "BoolFeedback", "videoMuteOn")]
    [InlineData("InputNumberFeedback", "IntFeedback", "inputNumber")]
    public void Feedback_Is_Keyed(string property, string type, string key)
    {
        Source().Should().MatchRegex($@"{property}\s*=\s*new {type}\(""{key}""");
    }

    [Fact]
    public void Keys_Are_Unique_And_Do_Not_Clash_With_The_Base_List()
    {
        // The list rejects a second feedback with the same key, case-insensitively, and the
        // constructor would throw.
        var keys = Regex.Matches(Source(), @"new (?:Bool|Int|String)Feedback\(""([^""]+)""")
            .Select(m => m.Groups[1].Value)
            .ToList();

        keys.Should().NotBeEmpty();
        keys.Should().OnlyHaveUniqueItems(k => k.ToLowerInvariant());
        keys.Should().NotContain(k => BaseKeys.Contains(k, StringComparer.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("MuteFeedback")]
    [InlineData("VolumeLevelFeedback")]
    [InlineData("VideoMuteIsOn")]
    [InlineData("InputNumberFeedback")]
    public void Feedback_Is_Added_To_The_List_During_Init(string property)
    {
        var init = Body(Source(), "private void Init()");

        init.Should().Contain($"Feedbacks.Add({property});");
    }

    [Fact]
    public void Feedbacks_Are_Only_Added_During_Init()
    {
        // Init runs from the constructor; anything added later is missed by a consumer that
        // has already subscribed.
        var source = Source();
        var init = Body(source, "private void Init()");

        var everywhere = Regex.Matches(source, @"Feedbacks\.Add\(").Count;
        var inInit = Regex.Matches(init, @"Feedbacks\.Add\(").Count;

        inInit.Should().BeGreaterThan(0);
        everywhere.Should().Be(inInit);
    }

    [Fact]
    public void Init_Is_Called_From_The_Constructor()
    {
        var ctor = Body(Source(),
            "public LgDisplayController(string key, string name, LgDisplayPropertiesConfig config, IBasicCommunication comms)");

        ctor.Should().Contain("Init();");
    }

    [Fact]
    public void Display_Does_Not_Use_The_Base_Bridge_Link()
    {
        // The base bridge link adds its own "inputNumber" feedback to the list, which would clash
        // with the one added here.
        Source().Should().NotContain("LinkDisplayToApi(");
    }
}
