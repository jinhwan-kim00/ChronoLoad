using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ChronoLoad.Mcp;

// chronoload-mcp — stdio ↔ HTTP 브리지 (§10.1)
//
// MCP 클라이언트는 stdio 로 말하고, ChronoLoad 앱은 자기 프로세스 안에서 HTTP 로 듣는다.
// 이 프로그램이 그 사이를 잇는다.
//
// 존재 이유는 두 가지다.
//   1) stdio 만 지원하는 클라이언트를 붙인다.
//   2) **앱이 꺼져 있을 때 제대로 실패한다.** 클라이언트는 자기 기동 시점에 서버를 띄우는데
//      그때 앱이 없으면 연결 자체가 실패해 "이 MCP 서버는 고장났다"로 남는다.
//      브리지는 살아남아 토큰 파일을 다시 확인하고, 그동안의 호출에는
//      app_not_running 을 돌려준다. 앱을 켜면 클라이언트를 다시 시작할 필요가 없다.

var stdout = Console.OpenStandardOutput();
using var writer = new StreamWriter(stdout, new UTF8Encoding(false)) { AutoFlush = true };
using var reader = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

// 한 줄에 JSON 하나. MCP stdio 전송의 규약이다.
while (await reader.ReadLineAsync() is { } line)
{
    if (string.IsNullOrWhiteSpace(line)) continue;

    JsonNode? request;
    try { request = JsonNode.Parse(line); }
    catch (JsonException) { continue; }          // 우리가 고칠 수 없는 입력은 조용히 흘린다
    if (request is null) continue;

    // 알림(id 없음)에는 응답하지 않는다. 앱이 없을 때도 마찬가지다 —
    // 응답을 만들어 보내면 클라이언트가 규약 위반으로 본다.
    bool isNotification = request["id"] is null;

    string? response = await ForwardAsync(http, line);

    if (response is not null) { await writer.WriteLineAsync(response); continue; }
    if (isNotification) continue;

    await writer.WriteLineAsync(AppNotRunning(request));
}

return;

/// <summary>앱으로 넘긴다. 앱이 없거나 닿지 않으면 null.</summary>
static async Task<string?> ForwardAsync(HttpClient http, string payload)
{
    if (McpTokenFile.TryRead() is not { } token) return null;

    try
    {
        using var message = new HttpRequestMessage(
            HttpMethod.Post, $"http://127.0.0.1:{token.Port}/mcp")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };

        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var reply = await http.SendAsync(message);
        if (!reply.IsSuccessStatusCode) return null;

        string body = await reply.Content.ReadAsStringAsync();
        return Unwrap(body);
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
    {
        // 앱이 방금 종료됐다. 브리지는 죽지 않는다 — 다음 호출에서 토큰 파일을 다시 본다.
        return null;
    }
}

/// <summary>
/// Streamable HTTP 는 응답을 SSE 로 감싸 보낼 수 있다. stdio 쪽은 JSON 한 줄만 받으므로 벗긴다.
/// </summary>
static string? Unwrap(string body)
{
    body = body.Trim();
    if (body.Length == 0) return null;
    if (body.StartsWith('{')) return body.ReplaceLineEndings(" ");

    foreach (string line in body.Split('\n'))
    {
        string trimmed = line.Trim();
        if (trimmed.StartsWith("data:", StringComparison.Ordinal))
            return trimmed["data:".Length..].Trim();
    }

    return null;
}

/// <summary>
/// 앱이 없을 때의 응답. <b>JSON-RPC 오류가 아니라 정상 결과에 <c>isError</c></b> 로 돌려준다 —
/// 전송 오류로 보내면 클라이언트가 서버를 죽은 것으로 간주하고 재연결을 포기한다.
/// </summary>
static string AppNotRunning(JsonNode request)
{
    string method = request["method"]?.GetValue<string>() ?? string.Empty;
    var id = request["id"]?.DeepClone();

    // initialize 만은 성공해야 한다. 여기서 실패하면 클라이언트가 세션 자체를 포기해,
    // 나중에 앱을 켜도 다시 시작하기 전까지 붙지 않는다.
    JsonNode result = method switch
    {
        "initialize" => new JsonObject
        {
            ["protocolVersion"] = "2025-06-18",
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
            ["serverInfo"] = new JsonObject
            {
                ["name"] = "chronoload",
                ["version"] = "0.0.0",
            },
            ["instructions"] = "ChronoLoad 앱이 실행 중이 아니다. 앱을 실행하면 툴이 바로 살아난다.",
        },
        "tools/list" or "resources/list" or "prompts/list" or "resources/templates/list" =>
            new JsonObject { [ListKey(method)] = new JsonArray() },
        _ => new JsonObject
        {
            ["isError"] = true,
            ["content"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = """{"error":"app_not_running","hint":"ChronoLoad를 실행한 뒤 다시 시도하세요"}""",
                },
            },
        },
    };

    return new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["result"] = result,
    }.ToJsonString();
}

static string ListKey(string method) => method switch
{
    "tools/list" => "tools",
    "resources/list" => "resources",
    "prompts/list" => "prompts",
    _ => "resourceTemplates",
};
