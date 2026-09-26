using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;

using ChronoLoad.Core.Settings;

namespace ChronoLoad.Mcp;

/// <summary>
/// 브리지가 앱을 찾고 인증하기 위한 토큰 파일 (§10.1).
/// </summary>
/// <remarks>
/// <para>
/// 파일의 존재 자체가 "앱이 실행 중"이라는 신호다. 브리지는 이 파일을 보고
/// 연결할지 <c>app_not_running</c> 을 돌려줄지 정한다.
/// </para>
/// <para>
/// <b>ACL 로 현재 사용자만 읽게 잠근다.</b> 루프백에 묶여 있어도 같은 기기의 다른 사용자가
/// 토큰을 읽으면 이 기기의 전체 관측 정보에 접근할 수 있다.
/// </para>
/// </remarks>
public static class McpTokenFile
{
    /// <summary>
    /// 설정 파일과 같은 폴더를 쓴다. <b>계산을 따로 하지 않고 한 곳에서 받는다</b> —
    /// 여기는 `Environment.GetFolderPath` 를 직접 불러 §11 의 격리(환경변수 우선)를 받지
    /// 못했고, 그래서 렌더·단위 테스트가 실행 중인 앱의 토큰 파일을 지울 수 있었다.
    /// 같은 실수를 두 번 하지 않으려면 출처가 하나여야 한다.
    /// </summary>
    public static string Directory => AppSettings.Directory;

    public static string Path => System.IO.Path.Combine(Directory, "mcp.token");

    public readonly record struct Contents(int Port, string Token, int ProcessId);

    /// <summary>새 토큰 값. 아직 파일로 쓰지는 않는다.</summary>
    public static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    /// <summary>
    /// 토큰을 파일로 남긴다. <b>서버가 실제로 포트를 잡은 뒤에</b> 부른다 —
    /// 먼저 쓰면 기동에 실패한 인스턴스가 남의 토큰을 덮어쓴다.
    /// </summary>
    public static Contents Write(int port, string token)
    {
        System.IO.Directory.CreateDirectory(Directory);

        var contents = new Contents(port, token, Environment.ProcessId);

        File.WriteAllLines(Path, [port.ToString(), token, Environment.ProcessId.ToString()]);
        Restrict(Path);

        return contents;
    }

    /// <summary>
    /// 이 파일을 현재 사용자만 읽게 잠근다.
    /// </summary>
    /// <remarks>
    /// <b>심층 방어다.</b> 상위 폴더인 <c>%LOCALAPPDATA%</c> 가 이미 사용자 전용이라
    /// 이것이 유일한 방어선은 아니다. 그래서 실패해도 서버를 세우는 일은 계속한다 —
    /// 추가 잠금이 안 됐다고 모니터링 전체를 포기하는 것은 균형이 맞지 않는다.
    /// </remarks>
    private static void Restrict(string path)
    {
        if (!OperatingSystem.IsWindows()) return;

        try
        {
            var file = new FileInfo(path);
            var security = file.GetAccessControl();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(
                WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl, AccessControlType.Allow));

            file.SetAccessControl(security);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException
                                      or PrivilegeNotHeldException)
        {
            Sensors.SensorLog.Write($"토큰 파일 ACL 강화 실패(무시): {ex.Message}");
        }
    }

    /// <summary>
    /// 읽는다. 파일이 없거나, 형식이 깨졌거나, <b>적힌 프로세스가 이미 죽었으면</b> null —
    /// 모두 "앱이 없다"는 뜻이다.
    /// </summary>
    /// <remarks>
    /// 프로세스 확인이 필요한 이유: 앱이 정상 종료하면 파일을 지우지만, 강제 종료되거나
    /// 죽으면 파일이 남는다. 그대로 두면 브리지가 없는 앱에 계속 연결을 시도하고,
    /// 사용자는 <c>app_not_running</c> 대신 알 수 없는 타임아웃만 보게 된다.
    /// </remarks>
    public static Contents? TryRead()
    {
        try
        {
            var lines = File.ReadAllLines(Path);
            if (lines.Length < 3) return null;
            if (!int.TryParse(lines[0], out int port)) return null;
            if (!int.TryParse(lines[2], out int pid)) return null;
            if (string.IsNullOrWhiteSpace(lines[1])) return null;

            if (!IsAlive(pid))
            {
                Delete();
                return null;
            }

            return new Contents(port, lines[1].Trim(), pid);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// 그 PID 의 프로세스가 아직 있는가. PID 재사용까지는 가리지 않는다 —
    /// 그 경우에도 토큰이 맞지 않아 연결이 거절되므로 여기서 완벽할 필요가 없다.
    /// </summary>
    private static bool IsAlive(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException) { return false; }      // 그런 프로세스가 없다
        catch (InvalidOperationException) { return false; }
    }

    /// <summary>앱 종료 시 지운다. 남아 있으면 브리지가 죽은 앱에 계속 붙으려 한다.</summary>
    public static void Delete()
    {
        try { File.Delete(Path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
