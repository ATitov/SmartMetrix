using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Text;
using System.Text.Json;
using SmartMetrix.LocalPositioningService;

namespace SmartMetrix.ArchitectureTests;

public sealed class PositioningStreamTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { IncludeFields = true };
    [Theory]
    [InlineData("valid")]
    [InlineData("wrong-clock")]
    [InlineData("oversize")]
    public async Task BridgeUsesBoundedFramesAndPinnedSourceClock(string scenario)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var source = new PositioningStreamSource("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port,
            "exc", PositionSourceType.Gnss, "gnss-1", "exposure-clock");
        var sample = new PositioningSample(PositionSourceType.Gnss, "gnss-1", 123456789,
            new Vector3(1, 2, 3), Quaternion.Identity, new double[36]);
        var wire = new PositioningWireSample(1, scenario == "wrong-clock" ? "other-clock" : "exposure-clock", sample);
        var bytes = Encoding.UTF8.GetBytes(scenario == "oversize" ? new string('x', 8193) + "\n" :
            JsonSerializer.Serialize(wire, Json) + "\n");
        var sending = SendAsync();
        var adapter = new JsonLinePositioningAdapter(source, 5);
        await using var enumerator = adapter.ReadAsync(timeout.Token).GetAsyncEnumerator(timeout.Token);
        if (scenario == "valid")
        {
            Assert.True(await enumerator.MoveNextAsync());
            Assert.Equal(sample.PositionMetres, enumerator.Current.PositionMetres);
            Assert.Equal("exposure-clock", enumerator.Current.ClockId);
            Assert.Equal("json-lines-v1", enumerator.Current.ProtocolVersion);
            Assert.Equal(123456789, enumerator.Current.HardwareTimestampNanoseconds);
        }
        else await Assert.ThrowsAsync<InvalidDataException>(async () => { await enumerator.MoveNextAsync(); });
        await sending;

        async Task SendAsync()
        {
            using var client = await listener.AcceptTcpClientAsync(timeout.Token);
            await client.GetStream().WriteAsync(bytes, timeout.Token);
        }
    }
}
