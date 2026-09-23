using System.Diagnostics;

namespace ChronoLoad.Sensors;

/// <summary>
/// 센서 진단 로그. 네이티브 카운터는 조용히 실패하는 일이 잦아,
/// "왜 이 장치가 안 잡히는가"를 추적할 창구가 하나 있어야 한다.
/// </summary>
public static class SensorLog
{
    /// <summary>비워두면 <see cref="Debug"/> 로만 나간다. 하네스는 여기에 콘솔을 꽂는다.</summary>
    public static Action<string>? Sink { get; set; }

    public static void Write(string message)
    {
        Debug.WriteLine($"[ChronoLoad] {message}");
        Sink?.Invoke(message);
    }
}
