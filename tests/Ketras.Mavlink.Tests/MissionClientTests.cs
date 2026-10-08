using Ketras.Mavlink.Messages;
using Ketras.Mavlink.Protocols;

namespace Ketras.Mavlink.Tests;

public sealed class MissionClientTests
{
    private static readonly IReadOnlyList<MissionItem> Plan =
    [
        MissionItem.At(MavFrame.GlobalRelativeAlt, MavCmd.NavTakeoff, 47.3977, 8.5456, 60, param1: 15, param4: float.NaN),
        MissionItem.At(MavFrame.GlobalRelativeAlt, MavCmd.NavWaypoint, 47.4010, 8.5490, 100),
        MissionItem.Action(MavCmd.DoChangeSpeed, 1, 17, -1),
        MissionItem.At(MavFrame.GlobalRelativeAlt, MavCmd.NavLand, 47.3978, 8.5457, 0, param4: float.NaN)
    ];

    [Fact]
    public async Task A_mission_uploads_and_downloads_unchanged_with_nan_parameters()
    {
        await using var link = await TestLink.StartAsync();

        var upload = await link.Session.Missions.UploadAsync(MavMissionType.Mission, Plan);
        var download = await link.Session.Missions.DownloadAsync(MavMissionType.Mission);

        Assert.True(upload.IsSuccess);
        Assert.True(download.IsSuccess);
        Assert.Equal(Plan.Count, download.Items!.Count);
        for (var i = 0; i < Plan.Count; i++)
        {
            Assert.Equal(Plan[i], download.Items[i]);
        }

        Assert.True(float.IsNaN(download.Items[0].Param4));
    }

    [Fact]
    public async Task Fences_are_kept_apart_from_the_mission_and_can_be_cleared()
    {
        await using var link = await TestLink.StartAsync();
        IReadOnlyList<MissionItem> fence =
        [
            MissionItem.At(MavFrame.Global, MavCmd.NavFenceCircleExclusion, 47.39, 8.54, 0, param1: 150, autoContinue: false)
        ];

        await link.Session.Missions.UploadAsync(MavMissionType.Mission, Plan);
        var uploaded = await link.Session.Missions.UploadAsync(MavMissionType.Fence, fence);
        var cleared = await link.Session.Missions.ClearAsync(MavMissionType.Fence);

        Assert.True(uploaded.IsSuccess);
        Assert.True(cleared.IsSuccess);
        Assert.Empty(link.Vehicle.Items(MavMissionType.Fence));
        Assert.Equal(Plan.Count, link.Vehicle.Items(MavMissionType.Mission).Count);
    }

    [Fact]
    public async Task The_vehicles_rejection_is_returned()
    {
        await using var link = await TestLink.StartAsync(configure: v => v.UploadResult = MavMissionResult.MavMissionInvalidParam1);

        var result = await link.Session.Missions.UploadAsync(MavMissionType.Mission, Plan);

        Assert.False(result.IsSuccess);
        Assert.Equal(MavMissionResult.MavMissionInvalidParam1, result.Result);
    }

    [Fact]
    public async Task An_empty_upload_clears_the_list()
    {
        await using var link = await TestLink.StartAsync();
        await link.Session.Missions.UploadAsync(MavMissionType.Mission, Plan);

        var result = await link.Session.Missions.UploadAsync(MavMissionType.Mission, []);

        Assert.True(result.IsSuccess);
        Assert.Empty(link.Vehicle.Items(MavMissionType.Mission));
    }
}
