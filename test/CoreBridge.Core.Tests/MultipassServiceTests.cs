using CoreBridge.Core.Common;
using CoreBridge.Core.Multipass;

namespace CoreBridge.Core.Tests;

public class MultipassServiceTests
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(50);

    [Theory]
    [InlineData("        STATE              : 4  RUNNING\r\n", MultipassServiceState.Running)]
    [InlineData("        STATE              : 3  STOP_PENDING\r\n                                (STOPPABLE)", MultipassServiceState.StopPending)]
    [InlineData("        STATE              : 1  STOPPED\r\n", MultipassServiceState.Stopped)]
    [InlineData("kein Status", MultipassServiceState.Unknown)]
    public void ParseStateLiestStateZeile(string output, MultipassServiceState expected)
    {
        Assert.Equal(expected, MultipassService.ParseState(output));
    }

    [Theory]
    [InlineData("        STATE              : 3  STOP_PENDING\r\n        PID                : 4711\r\n        FLAGS              :", 4711)]
    [InlineData("        STATE              : 1  STOPPED\r\n        PID                : 0\r\n", 0)]
    [InlineData("kein Status", 0)]
    public void ParseProcessIdLiestPidZeile(string output, int expected)
    {
        Assert.Equal(expected, MultipassService.ParseProcessId(output));
    }

    [Fact]
    public async Task OhneRechteAmDienstWirftOhneEingriff()
    {
        var service = new FakeService { Elevated = false, CanControl = false };

        await Assert.ThrowsAsync<MultipassAccessDeniedException>(() => Drain(service));
        Assert.Empty(service.Calls);
    }

    /// <summary>Erlaubt die Dienst-ACL dem Benutzer Stop/Start, braucht es keine Administratorrechte.</summary>
    [Fact]
    public async Task OhneAdminAberMitDienstrechtenStartetNeu()
    {
        var service = new FakeService { Elevated = false, CanControl = true, State = MultipassServiceState.Running };

        await Drain(service);

        Assert.Equal(["stop", "start"], service.Calls);
        Assert.Equal(MultipassServiceState.Running, service.State);
    }

    /// <summary>
    /// Den als SYSTEM laufenden Daemon hart zu beenden geht nur mit
    /// Administratorrechten: Zugriff verweigert, damit die CLI erst jetzt erhöht.
    /// </summary>
    [Fact]
    public async Task KillOhneAdminWirftZugriffVerweigert()
    {
        var service = new FakeService
        {
            Elevated = false,
            CanControl = true,
            State = MultipassServiceState.Running,
            StopHangs = true,
            KillDenied = true
        };

        await Assert.ThrowsAsync<MultipassAccessDeniedException>(() => Drain(service));
        Assert.Equal(["stop", "confirm", "kill 4711"], service.Calls);
    }

    [Fact]
    public async Task RegulaererStopOhneKill()
    {
        var service = new FakeService { State = MultipassServiceState.Running };

        var lines = await Drain(service);

        Assert.Equal(["stop", "start"], service.Calls);
        Assert.Contains("Service gestoppt.", lines);
        Assert.Equal(MultipassServiceState.Running, service.State);
    }

    [Fact]
    public async Task HaengenderStopBeendetDaemon()
    {
        var service = new FakeService { State = MultipassServiceState.StopPending, StopHangs = true };

        var lines = await Drain(service);

        // StopPending: kein zweiter Stop-Request, sondern direkt warten → fragen → kill.
        Assert.Equal(["confirm", "kill 4711", "start"], service.Calls);
        Assert.Contains(lines, line => line.StartsWith("Beendet: PID 4711"));
        Assert.Contains(Progress.ProgressTick, lines);
        Assert.Equal(MultipassServiceState.Running, service.State);
    }

    [Fact]
    public async Task AbgelehntesBeendenWirftOhneKill()
    {
        var service = new FakeService
        {
            State = MultipassServiceState.Running,
            StopHangs = true,
            Answer = false
        };

        var exception = await Assert.ThrowsAsync<MultipassException>(() => Drain(service));

        Assert.StartsWith("Abgebrochen", exception.Message);
        Assert.Equal(["stop", "confirm"], service.Calls);
    }

    [Fact]
    public async Task KillOhneWirkungWirftMitHinweis()
    {
        var service = new FakeService
        {
            State = MultipassServiceState.Running,
            StopHangs = true,
            KillHelps = false
        };

        var exception = await Assert.ThrowsAsync<MultipassServiceTimeoutException>(() => Drain(service));

        Assert.Contains("Stop-VM -TurnOff", exception.Message);
        Assert.DoesNotContain("start", service.Calls);
    }

    /// <summary>
    /// Die Wiederherstellungsaktion des Dienstes startet multipassd nach dem Kill
    /// sofort neu: Running unter neuer PID ist Erfolg, kein Hänger — und kein
    /// zweiter Start.
    /// </summary>
    [Fact]
    public async Task NachKillAutomatischNeuGestartetIstErfolg()
    {
        var service = new FakeService
        {
            State = MultipassServiceState.StopPending,
            StopHangs = true,
            KillRestarts = true
        };

        var lines = await Drain(service);

        Assert.Equal(["confirm", "kill 4711"], service.Calls);
        Assert.Contains("Service Multipass wurde automatisch neu gestartet (PID 4712).", lines);
        Assert.DoesNotContain("Service gestoppt.", lines);
        Assert.Equal(MultipassServiceState.Running, service.State);
    }

    [Fact]
    public async Task BereitsGestopptStartetNur()
    {
        var service = new FakeService { State = MultipassServiceState.Stopped };

        await Drain(service);

        Assert.Equal(["start"], service.Calls);
    }

    private static async Task<List<string>> Drain(FakeService service)
    {
        var lines = new List<string>();
        await foreach (var line in service.Restart(Short, service.Confirm))
        {
            lines.Add(line);
        }

        return lines;
    }

    private sealed class FakeService() : MultipassService(TimeSpan.FromMilliseconds(1), Short)
    {
        public bool Elevated { get; init; } = true;

        public bool CanControl { get; init; }

        public bool KillDenied { get; init; }

        public MultipassServiceState State { get; set; }

        public bool StopHangs { get; init; }

        public bool KillHelps { get; init; } = true;

        /// <summary>Kill wirkt wie die Wiederherstellungsaktion: sofort wieder Running unter neuer PID.</summary>
        public bool KillRestarts { get; init; }

        public int ProcessId { get; private set; } = 4711;

        public bool Answer { get; init; } = true;

        public List<string> Calls { get; } = [];

        public override bool IsElevated() => Elevated;

        public override bool CanControlService() => CanControl;

        public override MultipassServiceState QueryState() => State;

        public override void RequestStop()
        {
            Calls.Add("stop");
            State = StopHangs ? MultipassServiceState.StopPending : MultipassServiceState.Stopped;
        }

        public override void RequestStart()
        {
            Calls.Add("start");
            State = MultipassServiceState.Running;
        }

        public bool Confirm(string question)
        {
            Calls.Add("confirm");
            return Answer;
        }

        public override int QueryProcessId() => State == MultipassServiceState.Stopped ? 0 : ProcessId;

        public override void KillProcess(int processId)
        {
            Calls.Add($"kill {processId}");
            if (KillDenied)
            {
                throw new MultipassAccessDeniedException("Zugriff verweigert (Test).");
            }

            if (KillRestarts)
            {
                State = MultipassServiceState.Running;
                ProcessId = 4712;
            }
            else if (KillHelps)
            {
                State = MultipassServiceState.Stopped;
            }
        }

        public override bool IsDaemonResponding() => State == MultipassServiceState.Running;
    }
}
