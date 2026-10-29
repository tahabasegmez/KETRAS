using System.Collections.Concurrent;
using Ketras.Mavlink.Messages;

namespace Ketras.Tests.Vehicles;

public sealed class StatusTextTests
{
    [Fact]
    public async Task Long_texts_arrive_whole_and_short_ones_unchanged()
    {
        await using var link = await TestLink.StartAsync();
        var texts = new ConcurrentQueue<(MavSeverity Severity, string Text)>();
        link.Session.Status.StatusText += (severity, text) => texts.Enqueue((severity, text));
        var longText = "Arming denied: Resolve system health failures first\t";
        var exactly100 = new string('a', 50) + new string('b', 50);

        link.Vehicle.SendStatusText(MavSeverity.Critical, longText);
        link.Vehicle.SendStatusText(MavSeverity.Info, "Takeoff detected");
        link.Vehicle.SendStatusText(MavSeverity.Warning, exactly100);

        // The 100-character text has no short last chunk; it is finished by the link check.
        await TestLink.WaitForAsync(() => texts.Count == 3);
        Assert.Equal([(MavSeverity.Critical, longText), (MavSeverity.Info, "Takeoff detected"), (MavSeverity.Warning, exactly100)], texts);
    }
}
