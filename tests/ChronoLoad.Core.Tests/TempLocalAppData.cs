namespace ChronoLoad.Core.Tests;

/// <summary>
/// <c>LOCALAPPDATA</c> 를 임시 폴더로 돌린다. 설정 파일(§11)과 MCP 토큰 파일이 모두 이 폴더를
/// 쓰므로, 격리하지 않으면 테스트가 <b>실행 중인 앱의</b> 파일을 덮어쓰고 끝에 지운다.
/// </summary>
/// <remarks>
/// 실제로 그랬다 — 사용자의 창 위치가 지워지고 렌더 테스트의 합성 장치 키가 실제 파일에 남았다.
/// </remarks>
public sealed class TempLocalAppData : IDisposable
{
    private readonly string? _previous = Environment.GetEnvironmentVariable("LOCALAPPDATA");
    private readonly string _path =
        Path.Combine(Path.GetTempPath(), "chronoload-test-" + Guid.NewGuid().ToString("N"));

    public TempLocalAppData()
    {
        Directory.CreateDirectory(_path);
        Environment.SetEnvironmentVariable("LOCALAPPDATA", _path);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("LOCALAPPDATA", _previous);
        try { Directory.Delete(_path, recursive: true); } catch (IOException) { }
    }
}

/// <summary>
/// 환경변수는 <b>프로세스 전체</b>의 것이다. 두 테스트 클래스가 동시에 돌면서 각자
/// <c>LOCALAPPDATA</c> 를 바꾸면 서로의 폴더를 들여다보게 되고, 실패가 실행할 때마다 달라진다.
/// 같은 컬렉션에 묶어 직렬로 돌린다.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LocalAppDataCollection
{
    public const string Name = "LOCALAPPDATA";
}
