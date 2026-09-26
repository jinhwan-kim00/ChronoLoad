using System.Runtime.CompilerServices;

// 포트 폴백 판정(McpHost.IsPortTaken)을 테스트에서 직접 부르기 위해 공개한다.
// 실제 소켓을 물려 재현하는 경로도 있지만, 예외 중첩 구조는 그쪽으로 다 덮이지 않는다.
[assembly: InternalsVisibleTo("ChronoLoad.Core.Tests")]
