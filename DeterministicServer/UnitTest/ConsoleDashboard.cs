using System;
using System.Collections.Generic;

namespace LoadTestBot
{
    /// <summary>
    /// 콘솔 화면을 상단 고정 대시보드 영역과 그 아래로 쌓이는 로그 영역으로 나눠 관리한다.
    ///
    /// 동작 방식:
    /// - StartDashboard가 대시보드 줄 수만큼 빈 줄을 확보해 top 위치를 고정한다.
    /// - UpdateDashboard는 커서를 top 위치로 되돌려 각 줄을 덮어쓰고, 로그 영역의 커서 위치는
    ///   갱신 전후로 그대로 복원한다.
    /// - Log는 새 로그를 찍기 전 대시보드를 지우고, 로그를 찍은 뒤 대시보드를 그 아래에 다시
    ///   그린다 — 대시보드를 로그 바로 위로 재배치하는 방식으로, 로그는 위로 쌓이고 대시보드는
    ///   항상 로그 바로 아래 고정된다.
    ///
    /// 커서 제어를 지원하지 않는 환경(출력이 파일/파이프로 리다이렉트되거나 콘솔 API가 예외를
    /// 던지는 경우)에서는 폴백 모드로 전환해 대시보드도 한 줄씩 순차 출력한다 — 스크롤 방지
    /// 효과는 없지만 프로그램은 죽지 않는다.
    /// </summary>
    public class ConsoleDashboard
    {
        private readonly object _sync = new();
        private readonly List<string> _dashboardLines = new();
        private bool _dashboardVisible;
        private readonly bool _supportsCursorControl;

        public ConsoleDashboard()
        {
            _supportsCursorControl = DetectCursorSupport();
        }

        private static bool DetectCursorSupport()
        {
            if (Console.IsOutputRedirected) return false;

            try
            {
                // 실제로 한 번 읽어봐서 예외가 나지 않는지 확인한다. 일부 환경(예: 특정 CI 러너)은
                // IsOutputRedirected가 false인데도 커서 API 접근 시 예외를 던진다.
                _ = Console.CursorLeft;
                _ = Console.BufferWidth;
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>대시보드 영역을 초기화하고 처음 내용을 그린다. 로그 출력 시작 전, 한 번만 호출한다.</summary>
        public void StartDashboard(IReadOnlyList<string> initialLines)
        {
            lock (_sync)
            {
                _dashboardLines.Clear();
                _dashboardLines.AddRange(initialLines);

                if (!_supportsCursorControl)
                {
                    foreach (var line in _dashboardLines) Console.WriteLine(line);
                    return;
                }

                foreach (var line in _dashboardLines) Console.WriteLine(line);
                _dashboardVisible = true;
            }
        }

        /// <summary>대시보드 내용을 새 값으로 갱신한다 (줄 수는 StartDashboard와 동일해야 한다).</summary>
        public void UpdateDashboard(IReadOnlyList<string> newLines)
        {
            lock (_sync)
            {
                if (!_supportsCursorControl)
                {
                    // 폴백 모드에서는 갱신을 새 로그처럼 찍으면 출력이 너무 많아지므로
                    // 생략한다(최종 리포트는 별도로 출력되므로 정보 손실은 없다).
                    return;
                }

                if (!_dashboardVisible || newLines.Count != _dashboardLines.Count)
                {
                    // 줄 수가 달라지면 안전하게 다시 그린다 (드문 경우지만 방어적으로 처리).
                    RedrawDashboardLocked(newLines);
                    return;
                }

                try
                {
                    var (savedLeft, savedTop) = (Console.CursorLeft, Console.CursorTop);
                    int dashboardTop = savedTop - _dashboardLines.Count;

                    for (int i = 0; i < newLines.Count; i++)
                    {
                        Console.SetCursorPosition(0, dashboardTop + i);
                        WritePaddedLine(newLines[i]);
                    }

                    Console.SetCursorPosition(savedLeft, savedTop);
                    _dashboardLines.Clear();
                    _dashboardLines.AddRange(newLines);
                }
                catch (ArgumentOutOfRangeException)
                {
                    // 터미널 창이 리사이즈되는 등으로 커서 좌표가 유효 범위를 벗어난 경우.
                    // 대시보드 갱신 한 번을 건너뛸 뿐 프로그램은 계속 진행한다.
                }
            }
        }

        /// <summary>로그 영역에 새 줄을 추가한다. 대시보드가 항상 로그 바로 아래에 오도록 재배치한다.</summary>
        public void Log(string message)
        {
            lock (_sync)
            {
                if (!_supportsCursorControl || !_dashboardVisible)
                {
                    Console.WriteLine(message);
                    return;
                }

                try
                {
                    // 1) 대시보드 영역을 지운다 (로그가 그 자리에 끼어들 수 있도록).
                    var (_, currentTop) = (Console.CursorLeft, Console.CursorTop);
                    int dashboardTop = currentTop - _dashboardLines.Count;

                    Console.SetCursorPosition(0, dashboardTop);
                    for (int i = 0; i < _dashboardLines.Count; i++)
                    {
                        WritePaddedLine(string.Empty);
                    }
                    Console.SetCursorPosition(0, dashboardTop);

                    // 2) 로그를 그 자리에 찍는다 (커서가 자연스럽게 한 줄 아래로 내려간다).
                    Console.WriteLine(message);

                    // 3) 대시보드를 로그 바로 아래에 다시 그린다.
                    foreach (var line in _dashboardLines) Console.WriteLine(line);
                }
                catch (ArgumentOutOfRangeException)
                {
                    Console.WriteLine(message);
                }
            }
        }

        private void RedrawDashboardLocked(IReadOnlyList<string> newLines)
        {
            foreach (var line in newLines) Console.WriteLine(line);
            _dashboardLines.Clear();
            _dashboardLines.AddRange(newLines);
            _dashboardVisible = true;
        }

        /// <summary>대시보드를 최종적으로 접는다 (더 이상 갱신하지 않고 마지막 상태를 화면에 남긴다).</summary>
        public void StopDashboard()
        {
            lock (_sync)
            {
                _dashboardVisible = false;
            }
        }

        private static void WritePaddedLine(string content)
        {
            int width = SafeBufferWidth();
            // 이전 내용보다 짧은 새 내용을 쓸 때 오른쪽에 이전 글자가 남지 않도록 줄 전체를 공백으로 채운다.
            string padded = content.Length < width ? content.PadRight(width - 1) : content;
            Console.Write(padded);
        }

        private static int SafeBufferWidth()
        {
            try { return Math.Max(Console.BufferWidth, 20); }
            catch { return 120; }
        }
    }
}
