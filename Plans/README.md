# Plans

Forward-looking design work: the architecture migration, its stage scopes, the
feasibility studies for game families WPR can't host today, and open TODO lists.

These describe intent, not the current state of the tree. For how WPR works
*today*, see [`Docs/`](../Docs/README.md).

## Architecture migration

The layered-architecture redesign that's currently in flight — the reason project
and assembly names have been moving.

**These four docs were trimmed on 2026-08-30 to hold outstanding work only.** Stages
0–3 and sub-stages 5a–5e are complete and their narratives were removed; recover them
with `git show 2ce1cd2c:Plans/<file>`. Design rationale that still governs future
work — the assembly-identity rules, the RHI seam shape, the live risks — was kept,
because several of those sections are cited by name from source comments.

| Doc | What it covers |
| --- | --- |
| [ARCHITECTURE-MIGRATION.md](ARCHITECTURE-MIGRATION.md) | The ADR: target dependency graph, assembly-identity rules, and the stages that are left (Stage 4 remnant, Stage 5 remnant, the spine, 6–8) |
| [STAGE-GATE.md](STAGE-GATE.md) | The three checks every stage must pass before the next begins (build both TFMs, smoke titles reach gameplay, fitness test green) + the live `KnownBackendLeaks` baseline |
| [STAGE5-SIZING.md](STAGE5-SIZING.md) | The two clusters of the FNA/Vortice severance still open, and the frozen risk register (Risk #1 is cited from `IGameHost.cs`) |
| [STAGE5C-SCOPE.md](STAGE5C-SCOPE.md) | Design of record for the seven `WPR.Xna.Rhi` seams (cited from 13 source files), plus the design constraints on the spine stage |

**Start here:** `ARCHITECTURE-MIGRATION.md` §5, "What is left".

## Feasibility studies

Scoping work on game families that can't simply be hosted in-process.

| Doc | Verdict |
| --- | --- |
| [Unity_WP8_Feasibility.md](Unity_WP8_Feasibility.md) | Unity WP titles can't be hosted (native ARM engine) but *are* recoverable — a one-time per-game rebuild that WPR launches instead. The launcher rail for this is implemented; the per-game port is not. |
| [Spark2_Managed_Reimplementation_Feasibility.md](Spark2_Managed_Reimplementation_Feasibility.md) | Ubisoft's Spark2 engine (AC Pirates): **not practically feasible**. Gated on decrypting AES content from non-runnable ARM binaries, behind which sits a full proprietary D3D11 engine. |

## Open TODO lists

Working notes rather than finished documents.

| Doc | What it covers |
| --- | --- |
| [WPR-TODO.txt](WPR-TODO.txt) | Per-game crash notes awaiting diagnosis |
| [PenguinGame fixing todo.txt](<PenguinGame fixing todo.txt>) | Debugging notes for one title's null-reference crash (Russian) |
