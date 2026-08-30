# Plans

Forward-looking design work: the architecture migration, its stage scopes, the
feasibility studies for game families WPR can't host today, and open TODO lists.

These describe intent, not the current state of the tree. For how WPR works
*today*, see [`Docs/`](../Docs/README.md).

## Architecture migration

The layered-architecture redesign that's currently in flight — the reason project
and assembly names have been moving.

| Doc | What it covers |
| --- | --- |
| [ARCHITECTURE-MIGRATION.md](ARCHITECTURE-MIGRATION.md) | The ADR: current-state audit, target dependency graph, the 8 stages, and a running record of what has landed |
| [STAGE-GATE.md](STAGE-GATE.md) | The three checks every stage must pass before the next begins (build both TFMs, smoke titles reach gameplay, fitness test green) |
| [STAGE5-SIZING.md](STAGE5-SIZING.md) | Per-project audit sizing the FNA/Vortice severance — the migration's milestone stage |
| [STAGE5C-SCOPE.md](STAGE5C-SCOPE.md) | The RHI seam design: why the graphics seam mirrors the FNA3D C API rather than inventing its own vocabulary |

## Feasibility studies

Scoping work on game families that can't simply be hosted in-process.

| Doc | Verdict |
| --- | --- |
| [Unity_WP8_Feasibility.md](Unity_WP8_Feasibility.md) | Unity WP titles can't be hosted (native ARM engine) but *are* recoverable — a one-time per-game rebuild that WPR launches instead. The launcher rail for this is implemented. |
| [Spark2_Managed_Reimplementation_Feasibility.md](Spark2_Managed_Reimplementation_Feasibility.md) | Ubisoft's Spark2 engine (AC Pirates): **not practically feasible**. Gated on decrypting AES content from non-runnable ARM binaries, behind which sits a full proprietary D3D11 engine. |

## Open TODO lists

Working notes rather than finished documents.

| Doc | What it covers |
| --- | --- |
| [WPR-TODO.txt](WPR-TODO.txt) | Per-game crash notes awaiting diagnosis |
| [PenguinGame fixing todo.txt](<PenguinGame fixing todo.txt>) | Debugging notes for one title's null-reference crash (Russian) |
