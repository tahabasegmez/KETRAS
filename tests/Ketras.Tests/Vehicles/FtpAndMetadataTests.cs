using System.Text;
using Ketras.Mavlink.Protocols;

namespace Ketras.Tests.Vehicles;

public sealed class FtpAndMetadataTests
{
    [Fact]
    public async Task A_file_larger_than_one_packet_is_read_whole()
    {
        var content = Enumerable.Range(0, 1000).Select(i => (byte)(i * 7)).ToArray();
        await using var link = await TestLink.StartAsync(configure: v => v.Files["/fs/data.bin"] = content);

        var read = await link.Session.Ftp.ReadFileAsync("mftp://fs/data.bin");

        Assert.Equal(content, read);
    }

    [Fact]
    public async Task A_missing_file_is_reported_with_the_vehicles_error_code()
    {
        await using var link = await TestLink.StartAsync();

        var error = await Assert.ThrowsAsync<FtpException>(() => link.Session.Ftp.ReadFileAsync("/fs/missing"));

        Assert.Equal(10, error.ErrorCode);
    }

    [Fact]
    public async Task Metadata_is_read_from_the_vehicle_and_then_from_the_crc_cache()
    {
        const string general = """{"version":1,"metadataTypes":[{"type":1,"uri":"mftp://etc/parameters.json","fileCrc":1234}]}""";
        const string parameters = """{"version":1,"parameters":[]}""";
        await using var link = await TestLink.StartAsync(configure: v =>
        {
            v.ComponentMetadata = ("mftp://etc/general.json", 99);
            v.Files["/etc/general.json"] = Encoding.UTF8.GetBytes(general);
            v.Files["/etc/parameters.json"] = Encoding.UTF8.GetBytes(parameters);
        });
        var cache = Directory.CreateTempSubdirectory("ketras-metadata-").FullName;
        try
        {
            var first = await link.Session.Metadata.LoadAsync(cache);
            link.Vehicle.Files.Remove("/etc/parameters.json");
            var second = await link.Session.Metadata.LoadAsync(cache);

            Assert.Equal(parameters, first.ParametersJson);
            Assert.Equal(parameters, second.ParametersJson);
            Assert.Null(first.ActuatorsJson);
        }
        finally
        {
            Directory.Delete(cache, recursive: true);
        }
    }
}
