using System.Net;
using System.Net.Sockets;
using ChronoLoad.Core.Metrics;
using ChronoLoad.Core.Sampling;
using ChronoLoad.Mcp;

namespace ChronoLoad.Core.Tests;

/// <summary>
/// 종료 정리는 <b>호출한 스레드로 돌아오지 않아도</b> 끝나야 한다.
/// </summary>
/// <remarks>
/// <para>
/// WPF 의 <c>OnExit</c> 는 Dispatcher 스레드에서 돌고, 거기서 async 정리를 블로킹으로 기다렸다.
/// <c>await</c> 뒤의 이어받기가 그 Dispatcher 로 돌아가려 하는데 스레드는 이미 막혀 있고
/// Dispatcher 는 내려가는 중이라 영영 실행되지 않았다. 증상은 <b>창은 닫혔는데 프로세스만
/// 남는 것</b>이다 — 창 핸들 0 에 스레드 31개짜리가 실행할 때마다 쌓였다.
/// </para>
/// <para>
/// <b>라이브러리에 <c>ConfigureAwait(false)</c> 를 다는 것만으로는 모자랐다.</b> 우리 코드의
/// await 는 그렇게 막을 수 있지만 ASP.NET Core 의 종료 경로 안쪽까지는 우리 손이 닿지 않는다.
/// 실제로 앱을 살린 것은 호출부에서 <see cref="Task.Run(Func{Task})"/> 로 감싸 동기화 컨텍스트
/// 자체를 없앤 쪽이다. 여기서 검증하는 계약도 그것이다.
/// </para>
/// </remarks>
[Collection(LocalAppDataCollection.Name)]
public class McpShutdownTests
{
    /// <summary>이어받기를 받기만 하고 <b>실행하지 않는</b> 컨텍스트. 막힌 Dispatcher 를 흉내낸다.</summary>
    private sealed class DeadSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) { }
        public override void Send(SendOrPostCallback d, object? state) { }
    }

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static async Task<McpHost> StartAsync()
    {
        var registry = new MetricRegistry(64);
        var host = await McpHost.StartAsync(new McpContext(registry, new SampleEngine(registry)), FreePort());
        Assert.NotNull(host);
        return host;
    }

    /// <summary>
    /// 막힌 컨텍스트 위에서도 끝난다 — 단, <b>풀에서 돌릴 때</b>다.
    /// 이 감싸기를 지우면 이 테스트가 시간 초과로 무너진다.
    /// </summary>
    [Fact]
    [Trait("Category", "Hardware")]   // 실제 소켓을 연다
    public async Task Disposing_on_the_thread_pool_finishes_even_under_a_dead_context()
    {
        using var home = new TempLocalAppData();
        var host = await StartAsync();

        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DeadSynchronizationContext());
        try
        {
            // App.OnExit 의 Finish 와 같은 모양이다 — Task.Run 안에는 동기화 컨텍스트가 없다.
            var work = Task.Run(() => host.DisposeAsync().AsTask());

            // 여기서 await 하면 안 된다. 컨텍스트가 죽어 있는 동안의 await 는 <b>이 테스트
            // 메서드의</b> 이어받기를 거기로 보내 영영 돌아오지 않는다 — 실제로 그렇게 멎었다.
            // 블로킹 대기는 컨텍스트를 타지 않으므로 여기서는 이쪽이 맞다.
#pragma warning disable xUnit1031
            Assert.True(work.Wait(TimeSpan.FromSeconds(10)),
                "종료 정리가 풀에서도 끝나지 않았다.");
#pragma warning restore xUnit1031
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    [Fact]
    [Trait("Category", "Hardware")]
    public async Task Disposing_removes_the_token_file_so_the_bridge_stops_chasing_a_dead_app()
    {
        using var home = new TempLocalAppData();
        var host = await StartAsync();
        Assert.NotNull(McpTokenFile.TryRead());

        await host.DisposeAsync();
        Assert.Null(McpTokenFile.TryRead());
    }

    /// <summary>
    /// 세우지 못한 호스트를 버리는 것은 아무 일도 하지 않아야 한다. 특히 토큰 파일을
    /// 건드리면 안 된다 — 그 파일은 <b>정상 동작 중인 다른 인스턴스</b>의 것일 수 있다.
    /// </summary>
    [Fact]
    [Trait("Category", "Hardware")]
    public async Task A_host_that_never_started_leaves_someone_elses_token_alone()
    {
        using var home = new TempLocalAppData();
        var running = await StartAsync();
        var token = McpTokenFile.TryRead();
        Assert.NotNull(token);

        // 두 번째 인스턴스가 같은 포트 범위에서 전부 막혀 서지 못하는 상황.
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

            var registry = new MetricRegistry(64);
            Assert.Null(await McpHost.StartAsync(
                new McpContext(registry, new SampleEngine(registry)), port));
        }
        finally
        {
            foreach (var listener in squatters) listener.Stop();
        }

        // 첫 인스턴스의 토큰이 그대로여야 한다.
        Assert.Equal(token!.Value.Token, McpTokenFile.TryRead()?.Token);
        await running.DisposeAsync();
    }
}
