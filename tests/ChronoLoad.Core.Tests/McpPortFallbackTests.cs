using System.Net;
using System.Net.Sockets;
using ChronoLoad.Core.Metrics;
using ChronoLoad.Core.Sampling;
using ChronoLoad.Mcp;

namespace ChronoLoad.Core.Tests;

/// <summary>
/// 포트가 물려 있으면 번호를 올려 다시 선다(§13). 두 번째 인스턴스나, 앞선 인스턴스가
/// 포트를 아직 놓지 못한 직후 재실행이 흔한 경우다.
/// </summary>
[Collection(LocalAppDataCollection.Name)]
public class McpPortFallbackTests
{
    /// <summary>실제로 비어 있는 포트를 찾아 준다. 고정 번호를 쓰면 다른 프로세스와 부딪힌다.</summary>
    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static McpContext NewContext()
    {
        var registry = new MetricRegistry(64);
        return new McpContext(registry, new SampleEngine(registry));
    }

    [Fact]
    public void A_socket_in_use_is_recognised_even_when_wrapped()
    {
        var inner = new SocketException((int)SocketError.AddressAlreadyInUse);
        Assert.True(McpHost.IsPortTaken(inner));
        Assert.True(McpHost.IsPortTaken(new IOException("주소가 이미 쓰이고 있다", inner)));
        Assert.True(McpHost.IsPortTaken(
            new InvalidOperationException("겉", new IOException("속", inner))));
    }

    [Fact]
    public void Other_failures_are_not_mistaken_for_a_busy_port()
    {
        Assert.False(McpHost.IsPortTaken(null));
        Assert.False(McpHost.IsPortTaken(new UnauthorizedAccessException()));
        Assert.False(McpHost.IsPortTaken(
            new SocketException((int)SocketError.AccessDenied)));
    }

    [Fact]
    [Trait("Category", "Hardware")]   // 실제 소켓을 연다
    public async Task The_host_moves_to_the_next_port_when_the_first_is_taken()
    {
        using var home = new TempLocalAppData();
        int port = FreePort();
        using var squatter = new TcpListener(IPAddress.Loopback, port);
        squatter.Start();

        await using var host = await McpHost.StartAsync(NewContext(), port);

        Assert.NotNull(host);
        Assert.Equal(port + 1, host.Port);

        // 브리지는 번호를 모르고 토큰 파일만 본다. 거기 적힌 것이 실제로 잡은 포트여야 한다.
        Assert.Equal(host.Port, McpTokenFile.TryRead()?.Port);
    }

    [Fact]
    [Trait("Category", "Hardware")]
    public async Task It_gives_up_after_the_whole_range_is_taken()
    {
        using var home = new TempLocalAppData();
        int port = FreePort();
        var squatters = new List<TcpListener>();
        try
        {
            for (int i = 0; i < McpHost.PortAttempts; i++)
            {
                var listener = new TcpListener(IPAddress.Loopback, port + i);
                listener.Start();
                squatters.Add(listener);
            }

            Assert.Null(await McpHost.StartAsync(NewContext(), port));
        }
        finally
        {
            foreach (var listener in squatters) listener.Stop();
        }
    }
}
