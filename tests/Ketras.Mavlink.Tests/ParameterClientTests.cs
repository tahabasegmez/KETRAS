using Ketras.Mavlink.Autopilots;
using Ketras.Mavlink.Messages;
using Ketras.Mavlink.Testing;

namespace Ketras.Mavlink.Tests;

public sealed class ParameterClientTests
{
    private static FakeVehicleOptions WithParameters(int count) => new() { Parameters = TestLink.Parameters(count) };

    [Fact]
    public async Task The_full_list_completes_after_re_requesting_missing_indices_and_skips_the_hash_check()
    {
        await using var link = await TestLink.StartAsync(WithParameters(120), configure: v =>
        {
            v.DropFromParameterList.Add(3);
            v.DropFromParameterList.Add(77);
        });

        var list = await link.Session.Parameters.RefreshAllAsync();

        Assert.True(list.IsComplete);
        Assert.Equal(120, list.Expected);
        Assert.Equal(120, list.Values.Count);
        Assert.DoesNotContain("_HASH_CHECK", list.Values.Keys);
        Assert.Equal(new ParameterValue(MavParamType.Int32, 76), list.Values["PARAM_076"].Value);
        Assert.Equal(77.5, list.Values["PARAM_077"].Value.Value, 3);
    }

    [Fact]
    public async Task Integer_and_float_writes_are_confirmed_by_the_echo()
    {
        await using var link = await TestLink.StartAsync(WithParameters(10));

        var integer = await link.Session.Parameters.WriteAsync("PARAM_004", new ParameterValue(MavParamType.Int32, 123456));
        var real = await link.Session.Parameters.WriteAsync("PARAM_005", new ParameterValue(MavParamType.Real32, 0.125));

        Assert.True(integer.IsApplied);
        Assert.True(real.IsApplied);
        Assert.Equal(123456, link.Vehicle.Parameter("PARAM_004"));
        Assert.Equal(0.125, link.Vehicle.Parameter("PARAM_005"));
    }

    [Fact]
    public async Task A_write_the_vehicle_keeps_its_old_value_for_is_not_applied()
    {
        await using var link = await TestLink.StartAsync(WithParameters(10), configure: v => v.ReadOnlyParameters.Add("PARAM_002"));

        var result = await link.Session.Parameters.WriteAsync("PARAM_002", new ParameterValue(MavParamType.Int32, 9));

        Assert.False(result.IsApplied);
        Assert.Equal(2, result.Echoed!.Value.Value);
    }

    [Fact]
    public async Task Concurrent_reads_of_one_parameter_all_get_the_value()
    {
        await using var link = await TestLink.StartAsync(WithParameters(10));

        var reads = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => link.Session.Parameters.ReadAsync("PARAM_006")));

        Assert.All(reads, entry => Assert.Equal(6, entry!.Value.Value));
    }

    [Fact]
    public async Task An_unknown_parameter_reads_as_null()
    {
        await using var link = await TestLink.StartAsync(WithParameters(3));

        Assert.Null(await link.Session.Parameters.ReadAsync("NO_SUCH_PARAM"));
    }
}
