# CoreBridge

„Multipass Bridge“ — .NET-10-Konsolen-App, die ein echtes Host-Git-Repo mit
isolierten Agent-VMs über Multipass/Hyper-V verbindet.

- Verbindlicher Plan: `IMPLEMENTATION_PLAN.md` (Leitsätze, Phasen, Experimente)
- Agent-Regeln: `AGENTS.md`
- Experimente: `dotnet test test/Multipass.Experiments` — jeder Lauf landet in
  `EXPERIMENT_LOG.md`; Multipass wird nie direkt in der Shell aufgerufen.

Status: Phase A (Skeleton + CLI `multipass-status`), siehe Plan §10.
