using WinFWManager.Core.Models;
using WinFWManager.Core.Services;
using System.Net;

namespace WinFWManager.Tests.Services;

public class FirewallLogParserTests
{
    private readonly FirewallLogParser _parser = new();

    [Fact]
    public async Task ParseFileAsync_ValidLog_ParsesAllEntries()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestData", "sample-firewall.log");
        var events = await _parser.ParseFileAsync(path);

        events.Should().HaveCount(3);
    }

    [Fact]
    public async Task ParseFileAsync_SkipsCommentLines()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestData", "sample-firewall.log");
        var events = await _parser.ParseFileAsync(path);

        events.Should().NotContain(e => e.SourcePort == 0 && e.DestinationPort == 0);
    }

    [Fact]
    public async Task ParseFileAsync_ParsesTcpAllow()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestData", "sample-firewall.log");
        var events = await _parser.ParseFileAsync(path);

        var first = events[0];
        first.Action.Should().Be(TrafficAction.Allow);
        first.Protocol.Should().Be(TransportProtocol.TCP);
        first.SourceAddress.Should().Be(IPAddress.Parse("192.168.1.100"));
        first.SourcePort.Should().Be(54321);
        first.DestinationAddress.Should().Be(IPAddress.Parse("10.0.0.1"));
        first.DestinationPort.Should().Be(443);
        first.Direction.Should().Be(TrafficDirection.Outbound);
    }

    [Fact]
    public async Task ParseFileAsync_ParsesUdpDrop()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestData", "sample-firewall.log");
        var events = await _parser.ParseFileAsync(path);

        var second = events[1];
        second.Action.Should().Be(TrafficAction.Drop);
        second.Protocol.Should().Be(TransportProtocol.UDP);
        second.SourceAddress.Should().Be(IPAddress.Parse("172.28.0.5"));
        second.Direction.Should().Be(TrafficDirection.Inbound);
    }

    [Fact]
    public async Task ParseFileAsync_ReportsProgress()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestData", "sample-firewall.log");
        var progressValues = new List<int>();
        var progress = new Progress<int>(v => progressValues.Add(v));

        await _parser.ParseFileAsync(path, progress);

        // Give Progress<T> callback time to fire (it posts to SynchronizationContext)
        await Task.Delay(100);
        progressValues.Should().NotBeEmpty();
    }

    [Fact]
    public async Task ParseFileAsync_CancellationToken_Cancels()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestData", "sample-firewall.log");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => _parser.ParseFileAsync(path, cancellationToken: cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
