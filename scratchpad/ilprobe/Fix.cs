using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

namespace IlProbe
{
    // Prototype of the reachability-aware relocation: relocate underflow blocks to the end of the
    // method until the method is clean, and revert the method entirely if it cannot be made clean.
    //   usage: ilprobe --fix <dll> [--out <path>]
    internal static class Fix
    {
        const int MaxRounds = 40;

        // Bisect switches for the two relaxations over the strict (known desktop-safe) version.
        static readonly bool AllowHandlers = Environment.GetEnvironmentVariable("ILPROBE_HANDLERS") == "1";
        static readonly bool AcceptProgress = Environment.GetEnvironmentVariable("ILPROBE_PROGRESS") == "1";

        public static void Run(string[] args)
        {
            string dll = args[Array.IndexOf(args, "--fix") + 1];
            int oi = Array.IndexOf(args, "--out");
            string outPath = oi >= 0 ? args[oi + 1] : dll + ".fixed.dll";

            var asm = AssemblyDefinition.ReadAssembly(dll);
            int fixedCount = 0, reverted = 0, blocks = 0;

            foreach (var t in asm.MainModule.GetTypes())
                foreach (var m in t.Methods)
                {
                    if (!m.HasBody || m.Body.Instructions.Count == 0) continue;
                    if (AllowHandlers) { if (!HandlersAreRelocatable(m)) continue; }
                    else if (m.Body.ExceptionHandlers.Count > 0) continue;
                    var lastOp = m.Body.Instructions[m.Body.Instructions.Count - 1].OpCode.Code;
                    if (lastOp != Code.Ret && lastOp != Code.Throw) continue;
                    if (Scan.Analyse(m).under == 0) continue;

                    int n = TryFix(m);
                    if (n > 0) { fixedCount++; blocks += n; } else if (n == 0) reverted++;
                }

            asm.Write(outPath);
            Console.Error.WriteLine($"[fix] {Path.GetFileName(dll)}: methods={fixedCount} blocks={blocks} reverted={reverted} -> {outPath}");
        }

        // A method is a candidate even when it has handlers, PROVIDED every region is bounded by a
        // real instruction (a null End means "to the end of the body", which appending would
        // silently extend) and the append point — the current tail — lies outside every region.
        // Blocks that lie inside a region are still refused, per-block, below.
        static bool HandlersAreRelocatable(MethodDefinition m)
        {
            foreach (var h in m.Body.ExceptionHandlers)
            {
                if (h.TryStart == null || h.TryEnd == null) return false;
                if (h.HandlerStart == null || h.HandlerEnd == null) return false;
            }
            return !InsideAnyRegion(m, m.Body.Instructions.Count - 1);
        }

        static bool InsideAnyRegion(MethodDefinition m, int index)
        {
            var idx = new Dictionary<Instruction, int>();
            var ins = m.Body.Instructions;
            for (int i = 0; i < ins.Count; i++) idx[ins[i]] = i;
            foreach (var h in m.Body.ExceptionHandlers)
            {
                if (Within(idx, h.TryStart, h.TryEnd, index)) return true;
                if (Within(idx, h.HandlerStart, h.HandlerEnd, index)) return true;
                if (h.FilterStart != null && Within(idx, h.FilterStart, h.HandlerStart, index)) return true;
            }
            return false;
        }

        static bool Within(Dictionary<Instruction, int> idx, Instruction start, Instruction end, int index)
        {
            if (start == null) return false;
            int lo = idx[start];
            int hi = end != null && idx.ContainsKey(end) ? idx[end] : int.MaxValue;
            return index >= lo && index < hi;
        }

        sealed class Undo
        {
            public List<Instruction> Clones = new List<Instruction>();
            public List<(Instruction ins, Instruction old)> Single = new List<(Instruction, Instruction)>();
            public List<(Instruction ins, int slot, Instruction old)> Multi = new List<(Instruction, int, Instruction)>();
        }

        // >0 = number of blocks relocated, verified clean; 0 = attempted then reverted; -1 = nothing
        static int TryFix(MethodDefinition m)
        {
            var body = m.Body;
            body.SimplifyMacros();
            var il = body.GetILProcessor();
            var undo = new Undo();
            int done = 0;
            int before = Scan.Analyse(m).under;

            // Two phases, and the order is the whole point. A clone is appended after the body's
            // last instruction, which must end the flow — so every ret/throw-terminated clone is
            // appended first (each one leaves a ret/throw as the new tail, so the next append is
            // still safe), and a br-terminated clone can only ever be the LAST thing in the method,
            // because whatever followed it would sit in its shadow. One br clone per method.
            for (int round = 0; round < MaxRounds; round++)
            {
                if (Scan.Analyse(m).under == 0) break;
                if (!RelocateOne(m, il, undo, allowBrTerminator: false)) break;
                done++;
            }
            if (Scan.Analyse(m).under != 0 && RelocateOne(m, il, undo, allowBrTerminator: true))
            {
                done++;
            }

            // Keep the transformation only if it strictly reduced the conflict count. Relocation
            // is semantics-preserving, so partial progress is worth keeping (some blocks sit
            // inside a protected region and can never move); what must never be kept is a pass
            // that left the method no better, or worse, than it found it.
            int after = Scan.Analyse(m).under;
            bool improved = AcceptProgress ? after < before : after == 0;
            if (!improved || done == 0)
            {
                // revert exactly, newest first
                for (int i = undo.Clones.Count - 1; i >= 0; i--) il.Remove(undo.Clones[i]);
                foreach (var (ins, old) in undo.Single) ins.Operand = old;
                foreach (var (ins, slot, old) in undo.Multi) ((Instruction[])ins.Operand)[slot] = old;
                body.OptimizeMacros();
                return done == 0 ? -1 : 0;
            }

            body.OptimizeMacros();
            return done;
        }

        static bool RelocateOne(MethodDefinition m, ILProcessor il, Undo undo, bool allowBrTerminator)
        {
            var body = m.Body;

            var ins = body.Instructions.ToList();
            var idx = new Dictionary<Instruction, int>();
            for (int i = 0; i < ins.Count; i++) idx[ins[i]] = i;

            bool[] seen; int[] depth;
            Scan.Flow(m, ins, idx, out seen, out depth);

            var targets = new HashSet<Instruction>();
            foreach (var i in ins)
            {
                if (i.Operand is Instruction s) targets.Add(s);
                else if (i.Operand is Instruction[] many) foreach (var x in many) targets.Add(x);
            }

            for (int i = 1; i < ins.Count; i++)
            {
                if (!seen[i] || !targets.Contains(ins[i])) continue;
                int p = i - 1;
                while (p >= 0 && !seen[p]) p--;
                if (p < 0) continue;
                var pc = ins[p].OpCode.Code;
                if (pc != Code.Br && pc != Code.Br_S) continue;
                if (depth[p] >= depth[i]) continue;               // underflow direction only

                int e = i;
                bool delimited = false;
                while (e < ins.Count)
                {
                    if (e > i && targets.Contains(ins[e])) break;  // second entry point
                    var c = ins[e].OpCode.Code;
                    if (c == Code.Br && !allowBrTerminator) break;  // phase 1: ret/throw blocks only
                    if (c == Code.Ret || c == Code.Throw || c == Code.Br) { delimited = true; break; }
                    // Anything that is not straight-line code or a call ENDS the block without
                    // delimiting it. Testing only for Cond_Branch here was the bug that broke the
                    // handler relaxation: leave / endfinally / endfilter / rethrow are none of
                    // Ret, Throw, Br or Cond_Branch, so the scan walked straight past them and the
                    // block could be extended across a region boundary — and cloning a `leave` to
                    // the end of the method takes it OUT of the try it belongs to, which silently
                    // changes which exceptions are caught.
                    var fc = ins[e].OpCode.FlowControl;
                    if (fc != FlowControl.Next && fc != FlowControl.Call) break;
                    e++;
                }
                if (!delimited) continue;

                // never relocate a block that is already at the very end (it IS the tail)
                if (e == ins.Count - 1) continue;

                // a block inside a try/handler/filter cannot move out of it
                bool inRegion = false;
                for (int k = i; k <= e && !inRegion; k++) inRegion = InsideAnyRegion(m, k);
                if (inRegion) continue;

                // The clone is appended after the body's last instruction, so that instruction
                // decides whether the clone's head — a branch target — lands in a br's shadow.
                // ret/throw end the flow, so the append is unconditionally safe. A br hands its own
                // depth straight on, so appending after one is safe ONLY when that depth already
                // equals what the clone expects. Checked before anything is cloned or repointed,
                // so bailing out mutates nothing. Getting this wrong is what ILVerify reported as
                // PathStackDepth and what broke the multi-relocation variant.
                int tailIdx = ins.Count - 1;
                var tc = ins[tailIdx].OpCode.Code;
                Instruction anchor = null;
                if (tc == Code.Ret || tc == Code.Throw) anchor = ins[tailIdx];
                else if ((tc == Code.Br || tc == Code.Br_S) && seen[tailIdx] && depth[tailIdx] == depth[i]) anchor = ins[tailIdx];

                // The body tail is not always usable — a previous br-terminated clone may already
                // hold it. An INTERIOR ret/throw works just as well, provided the instruction that
                // currently follows it is not a branch target: a conflict can only arise AT a
                // branch target, so if the successor is plain fallthrough-only code (unreachable,
                // since nothing falls through a ret) there is nothing for the clone's terminator to
                // disagree with. Both the anchor and its successor must also sit outside every
                // protected region, or the clone lands inside a try it does not belong to.
                if (anchor == null)
                {
                    for (int k = ins.Count - 2; k >= 0; k--)
                    {
                        var kc = ins[k].OpCode.Code;
                        if (kc != Code.Ret && kc != Code.Throw) continue;
                        if (targets.Contains(ins[k + 1])) continue;
                        if (InsideAnyRegion(m, k) || InsideAnyRegion(m, k + 1)) continue;
                        if (k >= i && k <= e) continue;              // never anchor inside the block being moved
                        anchor = ins[k];
                        break;
                    }
                }
                if (anchor == null) continue;

                var clones = new List<Instruction>();
                var map = new Dictionary<Instruction, Instruction>();
                bool ok = true;
                for (int k = i; k <= e && ok; k++)
                {
                    Instruction c;
                    try { c = Reloc.CloneInstruction(ins[k]); } catch { ok = false; break; }
                    if (c == null) { ok = false; break; }
                    clones.Add(c); map[ins[k]] = c;
                }
                if (!ok) continue;
                foreach (var c in clones)
                    if (c.Operand is Instruction op && map.TryGetValue(op, out var rep)) c.Operand = rep;

                var head = ins[i];
                var cloneHead = clones[0];
                var single = new List<(Instruction, Instruction)>();
                var multi = new List<(Instruction, int, Instruction)>();
                foreach (var x in ins)
                {
                    int xi = idx[x];
                    if (xi >= i && xi <= e) continue;
                    if (x.Operand is Instruction s && s == head) { x.Operand = cloneHead; single.Add((x, head)); }
                    else if (x.Operand is Instruction[] many)
                    {
                        for (int k = 0; k < many.Length; k++)
                            if (many[k] == head) { many[k] = cloneHead; multi.Add((x, k, head)); }
                    }
                }
                if (single.Count == 0 && multi.Count == 0) continue;

                var tail = anchor;
                foreach (var c in clones) { il.InsertAfter(tail, c); tail = c; }

                undo.Clones.AddRange(clones);
                undo.Single.AddRange(single);
                undo.Multi.AddRange(multi);
                return true;
            }
            return false;
        }
    }
}
