using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ChronoLoad.Mcp;

/// <summary>
/// 앱 안에서 도는 MCP 서버 (§10.1). 앱이 살아 있는 동안에만 존재한다.
/// </summary>
/// <remarks>
/// <para>
/// <b>헤드리스 모드는 제공하지 않는다.</b> 앱 없이 수집하면 "리셋 기준 통계"도 "히스토리"도
/// 가질 수 없어 이 MCP 의 가치 대부분이 사라진다. 반쪽짜리 응답보다 명확한 실패가 낫다.
/// </para>
/// <para>
/// 보안은 세 겹이다 — 루프백 고정, 토큰, <c>Origin</c> 검증.
/// 마지막 것은 DNS 리바인딩 방어다: 브라우저가 공격자 페이지에서 <c>127.0.0.1</c> 로
/// 요청을 보내는 것을 막는다. 루프백에 묶는 것만으로는 이 경로가 닫히지 않는다.
/// </para>
/// </remarks>
public sealed class McpHost : IAsyncDisposable
{
    public const int DefaultPort = 7667;

    /// <summary>
    /// 포트가 이미 물려 있으면 번호를 하나씩 올려 다시 시도한다. 두 번째 인스턴스나,
    /// 앞선 인스턴스가 포트를 아직 놓지 못한 직후 재실행이 흔한 경우다.
    /// </summary>
    public const int PortAttempts = 10;

    private WebApplication? _app;

    public int Port { get; private set; }
    public bool IsRunning => _app is not null;

    /// <remarks>
    /// 포트가 막혀 있으면 <see cref="PortAttempts"/>번까지 다음 번호로 옮긴다.
    /// 실제로 잡은 포트는 토큰 파일에 적히므로 브리지는 번호가 바뀌어도 찾아온다(§10.2-1).
    /// <b>포트 문제가 아닌 실패에서는 옮기지 않는다</b> — 권한이나 설정 탓이면 번호를
    /// 열 번 바꿔 봐야 같은 이유로 열 번 실패할 뿐이다.
    /// </remarks>
    public static async Task<McpHost?> StartAsync(McpContext context, int port = DefaultPort)
    {
        var host = new McpHost();
        for (int attempt = 0; attempt < PortAttempts; attempt++)
        {
            switch (await host.TryStartAsync(context, port + attempt))
            {
                case StartOutcome.Started: return host;
                case StartOutcome.PortTaken: continue;
                default: return null;
            }
        }

        Sensors.SensorLog.Write(
            $"MCP 서버: {port}~{port + PortAttempts - 1} 이 모두 사용 중이라 서지 못했다.");
        return null;
    }

    private enum StartOutcome { Started, PortTaken, Failed }

    private async Task<StartOutcome> TryStartAsync(McpContext context, int port)
    {
        WebApplication? app = null;
        try
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();      // 모니터가 자기 로그로 디스크를 쓰지 않는다
            builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, port));

            builder.Services.AddSingleton(context);
            builder.Services
                .AddMcpServer(options => options.ServerInfo = new()
                {
                    Name = "chronoload",
                    Version = typeof(McpHost).Assembly.GetName().Version?.ToString(3) ?? "0.1.0",
                })
                .WithHttpTransport()
                .WithTools<ChronoLoadTools>()
                .WithTools<ProcessTools>()
                .WithResources<McpResources>()
                .WithPrompts<McpPrompts>();

            // 토큰은 메모리에서 먼저 만들고, 포트를 실제로 잡은 뒤에야 파일로 쓴다.
            // 순서를 바꾸면 두 번째 인스턴스가 포트 충돌로 실패하면서
            // 정상 동작 중인 첫 인스턴스의 토큰 파일을 지워버린다.
            string token = McpTokenFile.NewToken();

            app = builder.Build();
            app.Use(async (ctx, next) => await GuardAsync(ctx, next, token));
            app.MapMcp("/mcp");

            await app.StartAsync();

            McpTokenFile.Write(port, token);

            _app = app;
            Port = port;
            return StartOutcome.Started;
        }
        catch (Exception ex)
        {
            // 세우다 만 호스트는 반드시 버린다. 열 번까지 시도하므로 놔두면 그만큼 샌다.
            if (app is not null) await app.DisposeAsync();

            // 포트 충돌이나 권한 문제로 서버가 못 서도 앱 본체는 계속 돌아야 한다.
            // 여기서는 토큰 파일을 건드리지 않는다 — 그 파일은 우리 것이 아닐 수 있다.
            if (IsPortTaken(ex))
            {
                Sensors.SensorLog.Write($"MCP 서버: 포트 {port} 사용 중 — 다음 번호로 옮긴다.");
                return StartOutcome.PortTaken;
            }

            Sensors.SensorLog.Write($"MCP 서버 시작 실패: {ex.Message}");
            return StartOutcome.Failed;
        }
    }

    /// <summary>
    /// 포트가 이미 물려 있어서 실패한 것인가. Kestrel 은 <see cref="SocketException"/>을
    /// <see cref="IOException"/>으로 감싸 올리므로 안쪽까지 훑는다.
    /// </summary>
    internal static bool IsPortTaken(Exception? ex)
    {
        for (; ex is not null; ex = ex.InnerException)
            if (ex is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse })
                return true;
        return false;
    }

    /// <summary>토큰과 <c>Origin</c> 을 본다. 통과하지 못하면 본문 없이 거절한다.</summary>
    private static async Task GuardAsync(HttpContext ctx, RequestDelegate next, string token)
    {
        if (!IsOriginAllowed(ctx))
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        string? authorization = ctx.Request.Headers.Authorization;
        string? presented = authorization?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true
            ? authorization["Bearer ".Length..].Trim()
            : null;

        // 길이·내용 모두 상수 시간으로 비교한다. 문자열 == 는 첫 불일치에서 빠져나와
        // 응답 시간으로 토큰을 한 글자씩 알아낼 여지를 남긴다.
        if (presented is null ||
            !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(presented),
                System.Text.Encoding.UTF8.GetBytes(token)))
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        await next(ctx);
    }

    /// <summary>
    /// <c>Origin</c> 이 없으면(브리지 같은 비브라우저 클라이언트) 통과시키고,
    /// 있으면 루프백만 허용한다.
    /// </summary>
    private static bool IsOriginAllowed(HttpContext ctx)
    {
        string? origin = ctx.Request.Headers.Origin;
        if (string.IsNullOrEmpty(origin)) return true;

        return Uri.TryCreate(origin, UriKind.Absolute, out var uri) &&
               (uri.IsLoopback || uri.Host == "localhost");
    }

    public async ValueTask DisposeAsync()
    {
        // 우리가 서버를 세운 경우에만 지운다. 세우지 못했다면 그 파일은 남의 것이다.
        if (_app is null) return;

        McpTokenFile.Delete();

        using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        // ConfigureAwait(false) 를 빠뜨리면 이어받기가 호출한 스레드로 돌아가려 한다.
        // WPF 종료 경로에서 이것을 블로킹으로 기다리면 그 스레드가 이미 막혀 있어 영영 풀리지 않는다.
        try { await _app.StopAsync(stopping.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        await _app.DisposeAsync().ConfigureAwait(false);
        _app = null;
    }
}
