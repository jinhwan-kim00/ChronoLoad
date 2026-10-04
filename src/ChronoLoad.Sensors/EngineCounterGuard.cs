namespace ChronoLoad.Sensors;

/// <summary>PDH 엔진 인스턴스 값 하나를 어떻게 쓸지.</summary>
internal enum EngineReading
{
    /// <summary>믿는다. 그룹 합에 넣는다.</summary>
    Trusted,

    /// <summary>
    /// 이번 값만 쓰지 않는다 — 한 구간에 낼 수 없는 크기다. 정상 카운터도 계상이 오래 몰리면 그렇다.
    /// 그 그룹은 이번 틱 측정 불가다.
    /// </summary>
    Skipped,

    /// <summary>카운터가 깨진 인스턴스다(누적값이 거꾸로 갔다). 그 그룹은 측정 불가이고, 왜인지를 알린다.</summary>
    Broken,
}

/// <summary>
/// PDH <c>GPU Engine</c> 인스턴스 중 <b>카운터가 깨진 것</b>을 가려 기억한다 (§5.4).
/// </summary>
/// <remarks>
/// <para>
/// 드라이버가 엔진의 누적 실행 시간을 거꾸로 돌리는 경우가 있다. Arc 130V(Lunar Lake 내장)에서 OpenVINO
/// 추론을 걸자 그 프로세스의 <c>engtype_Neural</c> 인스턴스 하나가 1초 사이에 +28.7e9, −13.8e9, +2.7e9 …
/// (100ns 단위)로 앞뒤로 흔들렸다. PDH 는 그것을 279,947% · 27,007% 로 계산하거나, 차분이 음수인 틱에는
/// <c>PDH_CSTATUS_INVALID_DATA</c> 를 준다. 같은 때 IGCL 하드웨어 카운터는 렌더+컴퓨트 96% 였다.
/// </para>
/// <para>
/// <b>값의 크기만으로는 깨짐을 가를 수 없다.</b> 같은 130V 에 ffmpeg OpenCL 부하를 걸자 정상 카운터(원시값 단조 증가)도
/// 계상이 몰려 1초에 376%·1,413% 를 냈다. 그래서 둘을 나눈다.
/// </para>
/// <list type="bullet">
/// <item>상한(<see cref="MaxPlausiblePercent"/>)을 넘는 값 — 그 값만 <see cref="EngineReading.Skipped"/>.</item>
/// <item>직전에 값을 낸 인스턴스가 상태 무효로 나옴 — 누적값이 거꾸로 갔다는 뜻이라 <see cref="EngineReading.Broken"/>.
/// 그 프로세스가 사는 동안 기억하고, 그럴듯한 값도 믿지 않는다. 쓰레기가 우연히 0~1000 사이에 떨어지면
/// 걸러지지 않고 100 으로 잘려 거짓 포화가 되기 때문이다. <see cref="ForgiveAfter"/> 번 연달아 정상 범위면 다시 믿는다.</item>
/// </list>
/// <para>
/// 상태 무효만으로는 깨졌다고 보지 않는다. 새로 생긴 인스턴스는 기준선이 없어 첫 수집에서 늘 무효다 —
/// 엔진 인스턴스가 1,200개를 넘고 프로세스가 수시로 뜨고 지므로, 그걸 깨짐으로 세면 측정 불가가 일상이 된다.
/// 그래서 "직전에 값이 있었는가"를 본다. 놀고 있는 인스턴스는 누적값이 움직이지 않아 거꾸로 갈 수도 없으므로,
/// 0 보다 큰 값을 낸 인스턴스만 기억한다 — 부하 중에도 수십 개다.
/// </para>
/// <para>
/// 어느 경우든 그 그룹을 0 으로 남기지 않는 것이 요점이다. 버리기만 하면 노는 다른 프로세스의 0 만 남아
/// "측정 불가"가 "0% 를 측정했다"로 둔갑한다 — 실제로 <c>GpuCompute</c> 가 30초 내내 0 이었고 포화 비율도 0 이었다.
/// </para>
/// <para>한 읽기는 <see cref="Begin"/> → <see cref="Read"/>·<see cref="Invalid"/> → <see cref="End"/> 순이다. 스레드 하나에서만 쓴다.</para>
/// </remarks>
internal sealed class EngineCounterGuard
{
    /// <summary>
    /// 한 인스턴스가 한 구간에 낼 수 있는 값의 상한(%). 정상 카운터도 계상이 몰리면 몇 배가 된다 —
    /// 130V 의 ffmpeg 부하에서 Compute 376%·1,413%, VideoDecode 559%. 깨진 카운터는 1.8e14(B580)·27,007~279,947%(130V).
    /// 넘는 값은 그 틱만 쓰지 않는다.
    /// </summary>
    public const double MaxPlausiblePercent = 1000;

    /// <summary>
    /// 깨졌다고 본 인스턴스가 이만큼 연달아 정상 범위의 값을 내면 다시 믿는다. 드라이버 재시작처럼 누적값이 한 번
    /// 되감긴 정상 카운터를 풀어 주기 위한 것이다. 깨진 카운터는 매번 수만% 거나 상태 무효라 여기까지 오지 못한다.
    /// </summary>
    public const int ForgiveAfter = 10;

    // 깨진 인스턴스 → (마지막으로 본 읽기 번호, 연달아 낸 정상 범위 값의 수). 보통 비어 있거나 한두 개다.
    private readonly Dictionary<string, (long Seen, int Streak)> _broken = new(StringComparer.Ordinal);

    // 직전 읽기에서 0 보다 큰 값을 낸 인스턴스 → 읽기 번호. 이것이 상태 무효로 나오면 누적값이 거꾸로 간 것이다.
    private readonly Dictionary<string, long> _moving = new(StringComparer.Ordinal);
    private long _pass;

    /// <summary>지금 기억하고 있는 깨진 인스턴스 수.</summary>
    public int Count => _broken.Count;

    /// <summary>지금 깨졌다고 보는 인스턴스 이름의 사본. 다른 스레드에 넘길 때 쓴다.</summary>
    public HashSet<string> SnapshotBroken() => new(_broken.Keys, StringComparer.Ordinal);

    /// <summary>
    /// 상태 무효를 알려 줄 필요가 있는가. 움직이던 인스턴스도 깨진 인스턴스도 없으면(유휴) 없다 —
    /// 그때는 무효 항목의 이름을 만드는 비용을 아낀다.
    /// </summary>
    public bool WantsInvalid => _broken.Count > 0 || _moving.Count > 0;

    /// <summary>값이 한 인스턴스가 한 구간에 낼 수 있는 범위인가.</summary>
    public static bool IsPlausible(double value) =>
        double.IsFinite(value) && value is >= 0 and <= MaxPlausiblePercent;

    public void Begin() => _pass++;

    /// <summary>값이 온 인스턴스.</summary>
    public EngineReading Read(string instance, double value)
    {
        bool plausible = IsPlausible(value);
        if (!(value <= 0)) _moving[instance] = _pass;     // 상한을 넘은 값도 움직인 것이다. NaN 도 여기로 온다.

        if (_broken.Count > 0 && _broken.TryGetValue(instance, out var known))
        {
            int streak = plausible ? known.Streak + 1 : 0;
            if (streak < ForgiveAfter)
            {
                _broken[instance] = (_pass, streak);
                return EngineReading.Broken;
            }

            // 이번 값도 그 연속의 일부라 그대로 쓴다.
            _broken.Remove(instance);
            SensorLog.Write($"GPU 엔진 카운터가 {ForgiveAfter}번 연달아 정상 범위였다 — 다시 믿는다: {instance}");
            return EngineReading.Trusted;
        }

        return plausible ? EngineReading.Trusted : EngineReading.Skipped;
    }

    /// <summary>
    /// 상태가 무효였던 인스턴스. 깨진 것이면(이미 알았든, 직전에 값을 내다가 지금 거꾸로 갔든) true.
    /// 처음 보는 인스턴스의 무효는 기준선이 없어서일 뿐이라 false 다.
    /// </summary>
    public bool Invalid(string instance)
    {
        if (_broken.ContainsKey(instance))
        {
            _broken[instance] = (_pass, 0);     // 정상으로 돌아오는 연속은 끊긴다
            return true;
        }

        if (!_moving.Remove(instance)) return false;

        _broken[instance] = (_pass, 0);
        SensorLog.Write($"GPU 엔진 카운터가 거꾸로 갔다 — 이 인스턴스를 측정 불가로 둔다: {instance}");
        return true;
    }

    /// <summary>이번 읽기에서 움직이지 않았거나 나타나지 않은 인스턴스는 잊는다.</summary>
    public void End()
    {
        foreach (var (instance, seen) in _moving)
            if (seen != _pass) _moving.Remove(instance);

        foreach (var (instance, known) in _broken)
            if (known.Seen != _pass) _broken.Remove(instance);
    }
}
