using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ChronoLoad.Core.Metrics;

namespace ChronoLoad.Core.Tests;

/// <summary>
/// 문서와 바이너리가 한 버전 번호를 쓴다 (CLAUDE.md 「개정 이력 쓰기」).
/// </summary>
/// <remarks>
/// 문서 버전은 개정 이력마다 올랐는데 바이너리는 한동안 1.0.0 에 머물러, 배포 파일 이름과 정보 창만으로는
/// 어느 판인지 가를 수 없었다. 셋 중 하나만 올리고 잊는 일을 여기서 막는다.
/// </remarks>
public class VersionConsistencyTests
{
    private static readonly Regex Semantic = new(@"^\d+\.\d+\.\d+$");

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "ChronoLoad.slnx"))) return dir.FullName;
        throw new InvalidOperationException("저장소 루트(ChronoLoad.slnx)를 찾지 못했다.");
    }

    private static string PropsVersion() =>
        XDocument.Load(Path.Combine(RepoRoot(), "Directory.Build.props"))
            .Descendants("Version").Single().Value.Trim();

    private static string DocumentVersion()
    {
        string text = File.ReadAllText(Path.Combine(RepoRoot(), "PROJECT.md"));
        var match = Regex.Match(text, @"\*\*문서 버전\*\*:\s*([0-9.]+)");
        Assert.True(match.Success, "PROJECT.md 머리말에서 문서 버전을 찾지 못했다.");
        return match.Groups[1].Value;
    }

    private static string LastChangeLogVersion()
    {
        var versions = File.ReadLines(Path.Combine(RepoRoot(), "CHANGE_LOG.md"))
            .Select(line => Regex.Match(line, @"^\|\s*(?:\*\*)?([0-9]+(?:\.[0-9]+)+)(?:\*\*)?\s*\|"))
            .Where(m => m.Success)
            .Select(m => m.Groups[1].Value)
            .ToArray();
        Assert.NotEmpty(versions);
        return versions[^1];
    }

    [Fact]
    public void Document_changelog_and_binary_share_one_version()
    {
        string props = PropsVersion();

        Assert.Matches(Semantic, props);
        Assert.Equal(props, DocumentVersion());
        Assert.Equal(props, LastChangeLogVersion());
    }

    [Fact]
    public void The_built_assembly_carries_the_same_version()
    {
        // 정보 창은 InformationalVersion 의 '+' 앞을, MCP 서버 정보는 AssemblyVersion 의 세 자리를 보여준다.
        var assembly = typeof(MetricRegistry).Assembly;
        string informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
            .InformationalVersion.Split('+')[0];

        Assert.Equal(PropsVersion(), informational);
        Assert.Equal(PropsVersion(), assembly.GetName().Version!.ToString(3));
    }
}
