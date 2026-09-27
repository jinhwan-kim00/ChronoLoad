using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ChronoLoad.Core.Devices;
using ChronoLoad.Core.Metrics;
using ChronoLoad.Core.Sampling;
using ChronoLoad.Mcp;

namespace ChronoLoad.Core.Tests;

/// <summary>
/// 실제 서버를 세우고 JSON-RPC 로 툴을 부른다. 툴 메서드를 직접 부르는 테스트는
/// <b>인자 바인딩과 응답 직렬화</b>를 거치지 않는다 — 거기서 나는 오류는 여기서만 보인다.
/// </summary>
/// <remarks>
/// 실사용 보고: <c>reset_stats(includePrevious=false)</c> 가 "An error occurred invoking" 을 돌려줬다.
/// 메서드를 직접 부른 테스트는 통과하고 있었다.
/// </remarks>
[Collection(LocalAppDataCollection.Name)]
[Trait("Category", "Hardware")]   // 실제 소켓을 연다
public class McpWireTests
{
    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private sealed class Session(HttpClient http, string? sessionId)
    {
        private int _id = 1;

        public static async Task<Session> OpenAsync(int port, string token)
        {
            var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            http.DefaultRequestHeaders.Accept.ParseAdd("text/event-stream");

            var init = await PostAsync(http, null, new
            {
                jsonrpc = "2.0", id = 0, method = "initialize",
                @params = new
                {
                    protocolVersion = "2025-06-18",
                    capabilities = new { },
                    clientInfo = new { name = "test", version = "1" },
                },
            });

            string? session = init.Response.Headers.TryGetValues("Mcp-Session-Id", out var values)
                ? values.First() : null;
            await PostAsync(http, session, new { jsonrpc = "2.0", method = "notifications/initialized" });
            return new Session(http, session);
        }

        public async Task<JsonElement> CallAsync(string tool, object arguments)
        {
            var (_, body) = await PostAsync(http, sessionId, new
            {
                jsonrpc = "2.0", id = _id++, method = "tools/call",
                @params = new { name = tool, arguments },
            });
            return body;
        }

        private static async Task<(HttpResponseMessage Response, JsonElement Body)> PostAsync(
            HttpClient http, string? session, object payload)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "mcp")
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
            };
            if (session is not null) request.Headers.Add("Mcp-Session-Id", session);

            var response = await http.SendAsync(request);
            string text = await response.Content.ReadAsStringAsync();

            // 스트리밍 응답이면 SSE 의 data: 줄에 JSON 이 들어 있다.
            string? json = text.TrimStart().StartsWith('{')
                ? text
                : text.Split('\n').Where(l => l.StartsWith("data:")).Select(l => l[5..].Trim()).LastOrDefault();

            return (response, string.IsNullOrWhiteSpace(json) ? default : JsonDocument.Parse(json).RootElement.Clone());
        }
    }

    /// <summary>툴 결과의 첫 텍스트 블록을 JSON 으로 푼다. 호출 자체가 실패했으면 그 메시지로 실패한다.</summary>
    private static JsonElement Payload(JsonElement rpc)
    {
        Assert.False(rpc.TryGetProperty("error", out var error), error.ToString());
        var result = rpc.GetProperty("result");
        string text = result.GetProperty("content")[0].GetProperty("text").GetString()!;
        Assert.False(result.TryGetProperty("isError", out var isError) && isError.GetBoolean(), text);
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static (McpContext Ctx, MetricRegistry Registry) Context()
    {
        var registry = new MetricRegistry(64);
        registry.Register(new DeviceInfo("cpu", DeviceClass.System, "CPU", "CPU", IconKind.Cpu), [MetricKind.CpuTotal]);
        registry.Register(
            new DeviceInfo("gpu:luid_1", DeviceClass.Gpu, "GPU", "GPU", IconKind.GpuNvidia, "NVIDIA"),
            [MetricKind.GpuUtil, MetricKind.GpuTemp]);
        for (int i = 0; i < 8; i++) registry.CommitAll([10f, 50f, 60f]);
        return (new McpContext(registry, new SampleEngine(registry)), registry);
    }

    [Fact]
    public async Task Reset_without_the_previous_interval_works_over_the_wire()
    {
        using var home = new TempLocalAppData();
        var (ctx, _) = Context();
        await using var host = await McpHost.StartAsync(ctx, FreePort());
        Assert.NotNull(host);

        var session = await Session.OpenAsync(host.Port, McpTokenFile.TryRead()!.Value.Token);

        var bare = Payload(await session.CallAsync("reset_stats", new { confirm = true, includePrevious = false }));
        Assert.Equal(JsonValueKind.Null, bare.GetProperty("previousInterval").ValueKind);

        var narrowed = Payload(await session.CallAsync("reset_stats",
            new { confirm = true, metric = "GpuUtil", deviceKey = "gpu:luid_1" }));
        Assert.Equal(JsonValueKind.Array, narrowed.GetProperty("previousInterval").ValueKind);
    }

    /// <summary>
    /// SDK 기본 직렬화는 null 필드를 생략하고 한글을 \uXXXX 로 바꾼다. 둘 다 전선 위에서만 보인다.
    /// </summary>
    [Fact]
    public async Task Missing_values_stay_null_and_text_is_not_escaped_over_the_wire()
    {
        using var home = new TempLocalAppData();
        var (ctx, _) = Context();
        await using var host = await McpHost.StartAsync(ctx, FreePort());
        Assert.NotNull(host);

        var session = await Session.OpenAsync(host.Port, McpTokenFile.TryRead()!.Value.Token);
        var rpc = await session.CallAsync("get_gpu_status", new { });

        var adapter = Payload(rpc).GetProperty("adapters")[0];
        Assert.Equal(JsonValueKind.Null, adapter.GetProperty("computePercent").ValueKind);
        Assert.Equal(JsonValueKind.Null, adapter.GetProperty("powerWatts").ValueKind);

        string text = rpc.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
        Assert.DoesNotContain("\\u", text);
        Assert.Contains("엔진", text);
    }

    [Fact]
    public async Task New_parameters_of_the_other_tools_bind_over_the_wire()
    {
        using var home = new TempLocalAppData();
        var (ctx, _) = Context();
        await using var host = await McpHost.StartAsync(ctx, FreePort());
        Assert.NotNull(host);

        var session = await Session.OpenAsync(host.Port, McpTokenFile.TryRead()!.Value.Token);

        Payload(await session.CallAsync("get_stats_since_reset", new { metric = "GpuUtil", saturationThreshold = 80.0 }));
        Payload(await session.CallAsync("get_metric_history", new { metric = "GpuUtil", deviceKey = "gpu:luid_1", maxPoints = 4 }));
        Payload(await session.CallAsync("get_gpu_status", new { verbose = true }));
        Payload(await session.CallAsync("describe_capabilities", new { }));
        Payload(await session.CallAsync("mark", new { label = "a" }));
        Payload(await session.CallAsync("mark", new { label = "b", note = "wire" }));
        Payload(await session.CallAsync("list_marks", new { }));
        Payload(await session.CallAsync("get_interval_stats", new { from = "a", metric = "GpuUtil" }));
        Payload(await session.CallAsync("compare_intervals", new { marks = new[] { "a", "b" }, untilNow = true }));

        // 프로세스 감시 툴은 기록기가 없는 문맥에서도 구조화된 오류로 답해야 한다(예외가 아니라).
        var watch = await session.CallAsync("watch_process", new { pid = Environment.ProcessId, durationSeconds = 5 });
        Assert.Equal("process_watch_unavailable", Payload(watch).GetProperty("error").GetString());
    }
}
