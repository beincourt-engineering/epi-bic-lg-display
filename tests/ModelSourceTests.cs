using FluentAssertions;
using Xunit;

namespace Epi.Display.Lg.Tests;

/// <summary>
/// Source guards for the display's model: read from its UPnP description in the background, whichever command set
/// controls it, carried on a DeviceInfo with a public Model telemetry reads by name.
/// </summary>
public class ModelSourceTests
{
    private static string Source(string file) =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "src", file)));

    [Fact]
    public void The_device_info_carries_a_model()
    {
        Source("LgDisplayDeviceInfo.cs").Should().Contain("public class LgDisplayDeviceInfo : DeviceInfo")
            .And.Contain("public string Model { get; set; }");
        Source("LgDisplayController.cs").Should().Contain("public DeviceInfo DeviceInfo { get; } = new LgDisplayDeviceInfo();");
    }

    [Fact]
    public void The_model_is_read_once_in_the_background_when_the_display_is_found()
    {
        var controller = Source("LgDisplayController.cs");

        controller.Should().Contain("LgUpnpModelReader.Read(host.Trim())");
        controller.Should().Contain("ModelReadFeedback = new StringFeedback(\"modelRead\", () => modelRead);");
        controller.Should().Contain("Feedbacks.Add(ModelReadFeedback);");
        controller.Should().Contain("Task.Run(");
        controller.Should().Contain("if (Interlocked.Exchange(ref modelReadRunning, 1) == 1) return;");
        // on the monitor going OK, and in UpdateDeviceInfo before the keycode check
        controller.Should().MatchRegex(@"UpdateDeviceInfoIfMissing\(\);\s*ReadModelIfMissing\(\);");
        controller.Should().MatchRegex(@"ReadModelIfMissing\(\);\s*if \(UsesIpControlKeycode\)");
    }
}
