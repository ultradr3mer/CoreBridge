using CoreBridge.Core.Common;
using CoreBridge.Core.Multipass;
using CoreBridge.Interaction;

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

    /// <summary>
    /// Drückt der Benutzer während des Wartens auf den Stop Enter, endet das Warten
    /// sofort (lange vor dem Timeout) und der Daemon wird ohne Rückfrage beendet.
    /// </summary>
    [Fact]
    public async Task KillImAngebotBeendetWartenUndDaemonOhneRueckfrage()
    {
        var service = new FakeService { State = MultipassServiceState.Running, StopHangs = true };
        ChoiceEvent<HungStopOptions>? offer = null;
        var lines = new List<string>();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        await foreach (var line in service.Restart(TimeSpan.FromMinutes(5), service.Confirm, message => offer = new(TimeSpan.Zero, message)))
        {
            lines.Add(line);
            if (line == Progress.ProgressTick && offer is { IsOpen: true })
            {
                Assert.True(offer.TryChooseKey(Keys.Enter));
            }
        }

        Assert.NotNull(offer);
        Assert.False(offer.IsOpen);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromMinutes(1));
        Assert.Equal(["stop", "kill 4711", "start"], service.Calls);
        Assert.Contains(lines, line => line.StartsWith("Warten abgebrochen"));
        Assert.Equal(MultipassServiceState.Running, service.State);
    }

    /// <summary><c>Esc</c> im Angebot bricht den Neustart ab, ohne den Daemon anzufassen.</summary>
    [Fact]
    public async Task AbbrechenImAngebotWirftOhneKill()
    {
        var service = new FakeService { State = MultipassServiceState.Running, StopHangs = true };
        ChoiceEvent<HungStopOptions>? offer = null;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var line in service.Restart(TimeSpan.FromMinutes(5), service.Confirm, message => offer = new(TimeSpan.Zero, message)))
            {
                if (line == Progress.ProgressTick && offer is { IsOpen: true })
                {
                    offer.TryChooseKey(Keys.Esc);
                }
            }
        });

        Assert.Equal(["stop"], service.Calls);
        Assert.True(offer!.TryGetAnswer(out var answer));
        Assert.Equal(HungStopOptions.Cancel, answer);
    }

    /// <summary>Mit <c>killNow</c> (erhöhtes Fenster nach Enter im Angebot): gar nicht erst warten, kein Angebot.</summary>
    [Fact]
    public async Task KillNowWartetNichtUndBietetNichtsAn()
    {
        var service = new FakeService { State = MultipassServiceState.StopPending, StopHangs = true };
        var offered = false;

        var lines = new List<string>();
        await foreach (var line in service.Restart(TimeSpan.FromMinutes(5), service.Confirm, message => { offered = true; return new(TimeSpan.Zero, message); }, killNow: true))
        {
            lines.Add(line);
        }

        Assert.False(offered);
        Assert.Equal(["kill 4711", "start"], service.Calls);
        Assert.DoesNotContain(Progress.ProgressTick, lines.TakeWhile(line => !line.StartsWith("Beendet")));
    }

    /// <summary>Ein regulärer Stop lässt das Angebot unbeantwortet; der Dienst schließt es mit „weiter warten“.</summary>
    [Fact]
    public async Task UnbeantwortetesAngebotSchliesstMitWeiterWarten()
    {
        var service = new FakeService { State = MultipassServiceState.Running };
        ChoiceEvent<HungStopOptions>? offer = null;

        await foreach (var _ in service.Restart(Short, service.Confirm, message => offer = new(TimeSpan.Zero, message)))
        {
        }

        Assert.Equal(["stop", "start"], service.Calls);
        Assert.True(offer!.TryGetAnswer(out var answer));
        Assert.Equal(HungStopOptions.Wait, answer);
    }

    /// <summary>
    /// Blockiert eine einzelne Daemon-Abfrage (multipass version beim Hochfahren),
    /// laufen die Ticks trotzdem weiter — sonst stünde der Spinner still und
    /// keine Taste würde gelesen.
    /// </summary>
    [Fact]
    public async Task LangsameDaemonAbfrageLiefertWeiterTicks()
    {
        var service = new FakeService
        {
            State = MultipassServiceState.Stopped,
            DaemonCheckDelay = TimeSpan.FromMilliseconds(300)
        };

        var lines = await Drain(service, TimeSpan.FromSeconds(10));

        var ticksBeforeOk = lines.SkipWhile(line => line != "Warte auf Daemon ...").TakeWhile(line => line != "OK").Count(line => line == Progress.ProgressTick);
        Assert.True(ticksBeforeOk >= 3, $"nur {ticksBeforeOk} Ticks während der blockierenden Abfrage");
    }

    /// <summary>Antwortet der Daemon nach dem Timeout noch nicht, wird gefragt statt abgebrochen.</summary>
    [Fact]
    public async Task DaemonNachTimeoutFragtWeiterWarten()
    {
        var service = new FakeService { State = MultipassServiceState.Stopped, DaemonRespondsAfter = TimeSpan.FromMilliseconds(200) };

        await Drain(service);

        Assert.Equal("start", service.Calls[0]);
        Assert.Contains("confirm", service.Calls);
        Assert.Contains(service.Questions, question => question.Contains("weiter warten?"));
    }

    [Fact]
    public async Task DaemonNichtWeiterWartenWirftTimeout()
    {
        var service = new FakeService
        {
            State = MultipassServiceState.Stopped,
            DaemonRespondsAfter = TimeSpan.FromMinutes(5),
            Answer = false
        };

        var exception = await Assert.ThrowsAsync<MultipassServiceTimeoutException>(() => Drain(service));

        Assert.Contains("antwortet", exception.Message);
        Assert.Equal(["start", "confirm"], service.Calls);
    }

    [Fact]
    public async Task BereitsGestopptStartetNur()
    {
        var service = new FakeService { State = MultipassServiceState.Stopped };

        await Drain(service);

        Assert.Equal(["start"], service.Calls);
    }

    private static async Task<List<string>> Drain(FakeService service, TimeSpan? timeout = null)
    {
        var lines = new List<string>();
        await foreach (var line in service.Restart(timeout ?? Short, service.Confirm))
        {
            lines.Add(line);
        }

        return lines;
    }

    /// <summary>
    /// Der Dienst meldet Stopped, der alte multipassd lebt aber weiter (hält den
    /// Socket): erst abwarten, dann nach Rückfrage hart beenden, dann starten. Am
    /// Ende läuft der Dienst unter neuer PID.
    /// </summary>
    [Fact]
    public async Task AlterDaemonNachStopWirdBeendetBevorGestartetWird()
    {
        var service = new FakeService { State = MultipassServiceState.Running, OldProcessLingers = true };

        var lines = await Drain(service);

        Assert.Equal(["stop", "confirm", "kill 4711", "start"], service.Calls);
        Assert.Contains(lines, line => line.Contains("(PID 4711) läuft nach"));
        Assert.Equal("multipassd: PID 4711 → 4712", lines[^1]);
    }

    /// <summary>Lehnt der Benutzer das Beenden des alten Prozesses ab, wird nicht gestartet.</summary>
    [Fact]
    public async Task AlterDaemonOhneZustimmungBrichtAbOhneStart()
    {
        var service = new FakeService { State = MultipassServiceState.Running, OldProcessLingers = true, Answer = false };

        var failure = await Assert.ThrowsAsync<MultipassException>(() => Drain(service));

        Assert.Contains("hält den Daemon-Socket", failure.Message);
        Assert.DoesNotContain("start", service.Calls);
    }

    /// <summary>
    /// Nach Enter im Angebot: der Dienst läuft per Wiederherstellung schon unter neuer
    /// PID, der alte Prozess lebt noch — er wird ohne weitere Rückfrage beendet.
    /// </summary>
    [Fact]
    public async Task AlterDaemonNachKillWirdOhneRueckfrageBeendet()
    {
        var service = new FakeService
        {
            State = MultipassServiceState.Running,
            StopHangs = true,
            KillRestarts = true,
            OldProcessLingers = true
        };

        var lines = new List<string>();
        ChoiceEvent<HungStopOptions>? offer = null;
        await foreach (var line in service.Restart(TimeSpan.FromMinutes(5), service.Confirm, message => offer = new(TimeSpan.Zero, message)))
        {
            lines.Add(line);
            if (line == Progress.ProgressTick && offer is { IsOpen: true })
            {
                offer.TryChooseKey(Keys.Enter);
            }
        }

        Assert.Equal(["stop", "kill 4711", "kill 4711"], service.Calls);
        Assert.Equal("multipassd: PID 4711 → 4712", lines[^1]);
    }

    /// <summary>Lässt sich der alte Prozess nicht beenden, scheitert der Neustart mit Hinweis.</summary>
    [Fact]
    public async Task UnbeendbarerAlterDaemonScheitert()
    {
        var service = new FakeService { State = MultipassServiceState.Running, OldProcessLingers = true, StrayUnkillable = true };

        var failure = await Assert.ThrowsAsync<MultipassException>(() => Drain(service));

        Assert.Contains("auch nach dem harten Beenden", failure.Message);
        Assert.DoesNotContain("start", service.Calls);
    }

    /// <summary>Läuft der Dienst danach unter derselben PID, war der Neustart unwirksam.</summary>
    [Fact]
    public async Task GleichePidNachNeustartScheitert()
    {
        var service = new FakeService { State = MultipassServiceState.Running, StartKeepsProcessId = true };

        var failure = await Assert.ThrowsAsync<MultipassException>(() => Drain(service));

        Assert.Contains("Neustart unwirksam", failure.Message);
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

        /// <summary>Der alte Prozess lebt nach Stop bzw. Kill weiter (wie ein multipassd, der den Socket hält).</summary>
        public bool OldProcessLingers { get; init; }

        /// <summary>Ein weiterlebender alter Prozess lässt sich auch hart nicht beenden.</summary>
        public bool StrayUnkillable { get; init; }

        /// <summary>Der Start bringt keinen neuen Prozess (gleiche PID wie vorher).</summary>
        public bool StartKeepsProcessId { get; init; }

        /// <summary>Weiterlebende alte multipassd-Prozesse.</summary>
        public List<int> Strays { get; } = [];

        public bool Answer { get; init; } = true;

        public List<string> Calls { get; } = [];

        public List<string> Questions { get; } = [];

        /// <summary>So lange blockiert jede Daemon-Abfrage (wie multipass version beim Hochfahren).</summary>
        public TimeSpan DaemonCheckDelay { get; init; }

        /// <summary>Ab dem ersten Start so lange antwortet der Daemon noch nicht.</summary>
        public TimeSpan DaemonRespondsAfter { get; init; }

        private readonly System.Diagnostics.Stopwatch sinceStart = new();

        public override bool IsElevated() => Elevated;

        public override bool CanControlService() => CanControl;

        public override MultipassServiceState QueryState() => State;

        public override void RequestStop()
        {
            Calls.Add("stop");
            State = StopHangs ? MultipassServiceState.StopPending : MultipassServiceState.Stopped;
            if (State == MultipassServiceState.Stopped && OldProcessLingers)
            {
                Strays.Add(ProcessId);
            }
        }

        public override void RequestStart()
        {
            Calls.Add("start");
            if (State == MultipassServiceState.Stopped && !StartKeepsProcessId)
            {
                ProcessId++;
            }

            State = MultipassServiceState.Running;
            sinceStart.Restart();
        }

        public bool Confirm(string question)
        {
            Calls.Add("confirm");
            Questions.Add(question);
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

            if (Strays.Contains(processId))
            {
                if (!StrayUnkillable)
                {
                    Strays.Remove(processId);
                }

                return;
            }

            if (OldProcessLingers && (KillRestarts || KillHelps))
            {
                Strays.Add(processId);
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

        public override IReadOnlyList<int> FindDaemonProcesses() =>
            State == MultipassServiceState.Stopped ? [.. Strays] : [.. Strays, ProcessId];

        public override bool IsDaemonResponding(bool acceptRejectedClient = false)
        {
            Thread.Sleep(DaemonCheckDelay);
            return State == MultipassServiceState.Running && sinceStart.Elapsed >= DaemonRespondsAfter;
        }
    }
}
