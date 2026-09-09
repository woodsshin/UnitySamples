using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace LoadTestBot
{
    internal class Program
    {
        private const int DefaultPort = 9050;
        private const int DefaultBotCount = 10; // 봇개수 인자를 생략했을 때 사용하는 기본값 (가벼운 스모크 테스트 규모)
        private const int PlayerIdWraparoundWarningThreshold = 254; // 서버 _nextPlayerId가 byte라 이 값을 넘으면 ID가 겹칠 수 있음

        private static async Task<int> Main(string[] args)
        {
            var dashboard = new ConsoleDashboard();

            if (!TryParseArgs(args, out var config, out string parseError))
            {
                Console.WriteLine(parseError);
                PrintUsage();
                return 1;
            }

            IPAddress serverAddress;
            try
            {
                serverAddress = IPAddress.Parse(config.ServerIp);
            }
            catch (FormatException)
            {
                Console.WriteLine($"'{config.ServerIp}'는 올바른 IP 주소 형식이 아닙니다 (호스트 이름은 지원하지 않습니다).");
                return 1;
            }

            var serverEndPoint = new IPEndPoint(serverAddress, config.ServerPort);
            var stats = new LoadTestStats();

            if (config.UsedDefaultBotCount)
            {
                Console.WriteLine(
                    $"봇 개수를 지정하지 않아 기본값({DefaultBotCount}개)으로 실행합니다. " +
                    "직접 지정하려면: LoadTestBot <봇개수> [서버IP] [서버포트] [옵션]");
                Console.WriteLine();
            }

            PrintBanner(config, serverEndPoint);

            if (config.BotCount > PlayerIdWraparoundWarningThreshold)
            {
                Console.WriteLine(
                    $"[경고] 서버의 플레이어 ID는 byte(0~255) 이며 래핑 처리가 없습니다. " +
                    $"{PlayerIdWraparoundWarningThreshold}개를 초과하는 누적 접속이 발생하면 " +
                    "ID가 겹쳐 일부 봇의 상태가 잘못 표시될 수 있습니다.");
                Console.WriteLine();
            }

            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true; // 프로세스 즉시 종료 대신 우리가 만든 정리 절차를 타게 한다.
                dashboard.Log("중지 요청 감지. 모든 봇을 정리하는 중...");
                cts.Cancel();
            };

            if (config.DurationSeconds > 0)
            {
                cts.CancelAfter(TimeSpan.FromSeconds(config.DurationSeconds));
            }

            var bots = new List<BotClient>(config.BotCount);
            var botTasks = new List<Task>(config.BotCount);

            dashboard.Log("봇 접속 시작 (순차적으로 연결을 분산합니다)...");
            for (int i = 0; i < config.BotCount; i++)
            {
                if (cts.IsCancellationRequested) break;

                var bot = new BotClient(serverEndPoint, stats);
                bots.Add(bot);
                botTasks.Add(bot.RunAsync(cts.Token));

                if (config.SpawnIntervalMs > 0 && i < config.BotCount - 1)
                {
                    try
                    {
                        await Task.Delay(config.SpawnIntervalMs, cts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }

            dashboard.Log("모든 봇 접속 시도 완료. 아래 통계 영역은 실시간으로 갱신됩니다 (Ctrl+C로 중지)...");
            dashboard.Log("");

            dashboard.StartDashboard(BuildDashboardLines(stats, bots, config.BotCount, stats.Snapshot(), DateTime.UtcNow));

            var reportingTask = ReportLoopAsync(stats, bots, config.BotCount, dashboard, cts.Token);

            try
            {
                await Task.WhenAll(botTasks).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 정상 종료 경로.
            }

            cts.Cancel(); // 보고 루프도 함께 정리 (기간 만료로 봇들만 먼저 끝난 경우 대비).
            try { await reportingTask.ConfigureAwait(false); } catch (OperationCanceledException) { }

            dashboard.StopDashboard();
            PrintFinalReport(stats, bots, config.BotCount);
            return 0;
        }

        private static async Task ReportLoopAsync(LoadTestStats stats, List<BotClient> bots, int targetCount, ConsoleDashboard dashboard, CancellationToken ct)
        {
            var lastSnapshot = stats.Snapshot();
            var lastTime = DateTime.UtcNow;

            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
            try
            {
                while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                {
                    var now = DateTime.UtcNow;
                    var snap = stats.Snapshot();

                    dashboard.UpdateDashboard(BuildDashboardLines(stats, bots, targetCount, snap, now, lastSnapshot, lastTime));

                    lastSnapshot = snap;
                    lastTime = now;
                }
            }
            catch (OperationCanceledException)
            {
                // 정상 종료 경로.
            }
        }

        /// <summary>
        /// 상단 고정 대시보드에 표시할 줄들을 만든다. 항상 같은 줄 수(4줄)를 반환해야
        /// ConsoleDashboard가 매번 같은 영역만 덮어쓸 수 있다.
        /// prevSnapshot/prevTime이 없으면(최초 표시) 초당 수치는 0으로 표시한다.
        /// </summary>
        private static List<string> BuildDashboardLines(
            LoadTestStats stats,
            List<BotClient> bots,
            int targetCount,
            StatsSnapshot snap,
            DateTime now,
            StatsSnapshot? prevSnapshot = null,
            DateTime? prevTime = null)
        {
            int joinedNow = 0;
            foreach (var b in bots)
            {
                if (b.JoinSucceeded) joinedNow++;
            }

            double sentPerSec = 0, recvPerSec = 0, kbSentPerSec = 0, kbRecvPerSec = 0;
            if (prevSnapshot.HasValue && prevTime.HasValue)
            {
                double elapsedSec = Math.Max(0.001, (now - prevTime.Value).TotalSeconds);
                var prev = prevSnapshot.Value;
                sentPerSec = (snap.PacketsSent - prev.PacketsSent) / elapsedSec;
                recvPerSec = (snap.PacketsReceived - prev.PacketsReceived) / elapsedSec;
                kbSentPerSec = (snap.BytesSent - prev.BytesSent) / 1024.0 / elapsedSec;
                kbRecvPerSec = (snap.BytesReceived - prev.BytesReceived) / 1024.0 / elapsedSec;
            }

            return new List<string>
            {
                "──────────── 실시간 통계 (2초 주기 갱신) ────────────",
                $"접속        : {joinedNow}/{targetCount}  (실패 {snap.BotsJoinFailed}, Pong 수신 {snap.PongsReceived})",
                $"송신 처리량 : {sentPerSec,6:F0} pkt/s ({kbSentPerSec,7:F1} KB/s)   누적 {snap.PacketsSent:N0}건",
                $"수신 처리량 : {recvPerSec,6:F0} pkt/s ({kbRecvPerSec,7:F1} KB/s)   누적 {snap.PacketsReceived:N0}건",
            };
        }

        private static void PrintBanner(LoadTestConfig config, IPEndPoint endPoint)
        {
            Console.WriteLine("========================================");
            Console.WriteLine(" CustomServer 부하 테스트 봇");
            Console.WriteLine("========================================");
            Console.WriteLine($"대상 서버      : {endPoint}");
            Console.WriteLine($"봇 개수        : {config.BotCount}");
            Console.WriteLine($"접속 분산 간격 : {config.SpawnIntervalMs} ms/봇");
            Console.WriteLine(config.DurationSeconds > 0
                ? $"테스트 지속 시간: {config.DurationSeconds}초"
                : "테스트 지속 시간: 무제한 (Ctrl+C로 종료)");
            Console.WriteLine();
        }

        private static void PrintFinalReport(LoadTestStats stats, List<BotClient> bots, int targetCount)
        {
            var snap = stats.Snapshot();
            int joined = 0;
            foreach (var b in bots) if (b.JoinSucceeded) joined++;

            Console.WriteLine();
            Console.WriteLine("========================================");
            Console.WriteLine(" 최종 결과");
            Console.WriteLine("========================================");
            Console.WriteLine($"목표 봇 수         : {targetCount}");
            Console.WriteLine($"접속 성공          : {joined}");
            Console.WriteLine($"접속 실패/타임아웃 : {snap.BotsJoinFailed}");
            Console.WriteLine($"총 송신 패킷/바이트: {snap.PacketsSent:N0} / {FormatBytes(snap.BytesSent)}");
            Console.WriteLine($"총 수신 패킷/바이트: {snap.PacketsReceived:N0} / {FormatBytes(snap.BytesReceived)}");
            Console.WriteLine($"수신한 Pong 수     : {snap.PongsReceived:N0}");
            Console.WriteLine();
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            return $"{bytes / (1024.0 * 1024.0):F2} MB";
        }

        private static void PrintUsage()
        {
            Console.WriteLine();
            Console.WriteLine("사용법:");
            Console.WriteLine("  LoadTestBot [봇개수] [서버IP] [서버포트] [옵션]");
            Console.WriteLine();
            Console.WriteLine("인자:");
            Console.WriteLine($"  봇개수      선택. 생성할 봇(가짜 플레이어) 수. 기본값 {DefaultBotCount}.");
            Console.WriteLine("  서버IP      선택. 기본값 127.0.0.1.");
            Console.WriteLine($"  서버포트    선택. 기본값 {DefaultPort}.");
            Console.WriteLine();
            Console.WriteLine("옵션:");
            Console.WriteLine("  --duration <초>            테스트 지속 시간. 생략 시 Ctrl+C로 직접 종료.");
            Console.WriteLine("  --spawn-interval-ms <ms>   봇 접속을 분산시키는 간격. 기본값 25ms.");
            Console.WriteLine();
            Console.WriteLine("예시:");
            Console.WriteLine("  LoadTestBot                 (기본값으로 실행: 봇 10개, localhost, 무제한)");
            Console.WriteLine("  LoadTestBot 100");
            Console.WriteLine("  LoadTestBot 200 192.168.0.10");
            Console.WriteLine("  LoadTestBot 500 192.168.0.10 9050 --duration 120 --spawn-interval-ms 10");
        }

        private static bool TryParseArgs(string[] args, out LoadTestConfig config, out string error)
        {
            config = new LoadTestConfig();
            error = "";

            var positional = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (a.Equals("--duration", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 >= args.Length || !double.TryParse(args[++i], NumberStyles.Float, CultureInfo.InvariantCulture, out double dur) || dur < 0)
                    {
                        error = "--duration 뒤에는 0 이상의 초 단위 숫자가 와야 합니다.";
                        return false;
                    }
                    config.DurationSeconds = dur;
                }
                else if (a.Equals("--spawn-interval-ms", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 >= args.Length || !int.TryParse(args[++i], out int interval) || interval < 0)
                    {
                        error = "--spawn-interval-ms 뒤에는 0 이상의 정수(밀리초)가 와야 합니다.";
                        return false;
                    }
                    config.SpawnIntervalMs = interval;
                }
                else if (a.Equals("-h", StringComparison.OrdinalIgnoreCase) || a.Equals("--help", StringComparison.OrdinalIgnoreCase))
                {
                    PrintUsage();
                    Environment.Exit(0);
                    return false;
                }
                else
                {
                    positional.Add(a);
                }
            }

            if (positional.Count < 1)
            {
                // 봇개수를 아예 생략한 경우: 에러로 막지 않고 가벼운 스모크 테스트 규모로 기본 실행한다.
                // (반면 값은 줬는데 숫자가 아니거나 0 이하인 경우는 아래에서 여전히 에러로 처리한다 —
                //  오타를 기본값으로 조용히 덮어써 사용자가 원치 않는 규모로 테스트가 도는 것을 막기 위함.)
                config.BotCount = DefaultBotCount;
                config.UsedDefaultBotCount = true;
                config.ServerIp = "127.0.0.1";
                config.ServerPort = DefaultPort;
                return true;
            }

            if (!int.TryParse(positional[0], out int botCount) || botCount <= 0)
            {
                error = $"'{positional[0]}'는 올바른 봇 개수가 아닙니다. 1 이상의 정수를 입력하세요.";
                return false;
            }
            config.BotCount = botCount;

            config.ServerIp = positional.Count >= 2 ? positional[1] : "127.0.0.1";

            if (positional.Count >= 3)
            {
                if (!int.TryParse(positional[2], out int port) || port <= 0 || port > 65535)
                {
                    error = $"'{positional[2]}'는 올바른 포트 번호가 아닙니다.";
                    return false;
                }
                config.ServerPort = port;
            }
            else
            {
                config.ServerPort = DefaultPort;
            }

            return true;
        }
    }

    internal class LoadTestConfig
    {
        public int BotCount;
        public string ServerIp = "127.0.0.1";
        public int ServerPort = 9050;
        public double DurationSeconds = 0; // 0 = 무제한
        public int SpawnIntervalMs = 25;
        public bool UsedDefaultBotCount; // 봇개수 인자를 생략해 기본값(DefaultBotCount)이 적용됐는지
    }
}