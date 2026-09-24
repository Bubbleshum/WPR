using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace IlProbe
{
    // Scans assemblies for the reachability-aware Mono carry conflict:
    //   a reachable branch target T whose nearest preceding REACHABLE instruction (skipping
    //   unreachable ones) is an unconditional br, and whose true entry depth differs from the
    //   depth that br carries.
    //   usage: ilprobe --scan <dir-or-dll> [--verbose]
    internal static class Scan
    {
        public static void Run(string[] args)
        {
            string path = args[Array.IndexOf(args, "--scan") + 1];
            bool verbose = args.Contains("--verbose");
            var files = Directory.Exists(path)
                ? Directory.GetFiles(path, "*.dll", SearchOption.AllDirectories)
                : new[] { path };

            int totalUnder = 0, totalOver = 0, totalMethods = 0, totalAsm = 0;
            var hist = new SortedDictionary<int, int>();
            foreach (var f in files)
            {
                AssemblyDefinition asm;
                try { asm = AssemblyDefinition.ReadAssembly(f); } catch { continue; }
                int under = 0, over = 0, methods = 0;
                foreach (var t in asm.MainModule.GetTypes())
                    foreach (var m in t.Methods)
                    {
                        if (!m.HasBody || m.Body.Instructions.Count == 0) continue;
                        var c = Analyse(m);
                        if (c.under == 0 && c.over == 0) continue;
                        under += c.under; over += c.over; methods++;
                        if (c.under > 0) { hist.TryGetValue(c.under, out int hv); hist[c.under] = hv + 1; }
                        if (verbose && c.under > 0)
                            Console.WriteLine($"  {Path.GetFileName(f)} {t.FullName}::{m.Name} under={c.under} over={c.over} handlers={m.Body.ExceptionHandlers.Count} last={m.Body.Instructions.Last().OpCode.Code}");
                    }
                if (under + over > 0)
                {
                    totalAsm++;
                    Console.WriteLine($"{Path.GetFileName(f)}: methods={methods} underflow={under} leftover={over}");
                }
                totalUnder += under; totalOver += over; totalMethods += methods;
            }
            Console.WriteLine($"TOTAL assemblies={totalAsm} methods={totalMethods} underflow={totalUnder} leftover={totalOver}");
            Console.WriteLine("underflow-per-method histogram: " + string.Join(" ", hist.Select(k => k.Key + "x" + k.Value)));
        }

        public struct Counts { public int under, over; }

        public static Counts Analyse(MethodDefinition m)
        {
            var ins = m.Body.Instructions.ToList();
            var idx = new Dictionary<Instruction, int>();
            for (int i = 0; i < ins.Count; i++) idx[ins[i]] = i;
            bool[] seen; int[] depth;
            Flow(m, ins, idx, out seen, out depth);
            return Conflicts(ins, seen, depth);
        }

        public static void Flow(MethodDefinition m, List<Instruction> ins, Dictionary<Instruction,int> idx, out bool[] seenOut, out int[] depthOut)
        {
            int[] depth = new int[ins.Count];
            bool[] seen = new bool[ins.Count];
            var work = new Stack<(int, int)>();
            work.Push((0, 0));
            foreach (var h in m.Body.ExceptionHandlers)
            {
                if (h.TryStart != null && idx.TryGetValue(h.TryStart, out int a)) work.Push((a, 0));
                if (h.HandlerStart != null && idx.TryGetValue(h.HandlerStart, out int b))
                    work.Push((b, h.HandlerType == ExceptionHandlerType.Finally || h.HandlerType == ExceptionHandlerType.Fault ? 0 : 1));
                if (h.FilterStart != null && idx.TryGetValue(h.FilterStart, out int c2)) work.Push((c2, 1));
            }
            while (work.Count > 0)
            {
                var (i, d) = work.Pop();
                while (i >= 0 && i < ins.Count)
                {
                    if (seen[i]) break;
                    seen[i] = true; depth[i] = d;
                    var op = ins[i];
                    int after = d + Program.Delta(op);
                    if (after < 0) after = 0;
                    if (op.Operand is Instruction t && idx.TryGetValue(t, out int ti)) work.Push((ti, after));
                    else if (op.Operand is Instruction[] many)
                        foreach (var x in many) if (idx.TryGetValue(x, out int xi)) work.Push((xi, after));
                    var fc = op.OpCode.FlowControl;
                    if (fc == FlowControl.Branch || fc == FlowControl.Return || fc == FlowControl.Throw) break;
                    d = after; i++;
                }
            }

            seenOut = seen; depthOut = depth;
        }

        public static Counts Conflicts(List<Instruction> ins, bool[] seen, int[] depth)
        {
            var targets = new HashSet<Instruction>();
            foreach (var i in ins)
            {
                if (i.Operand is Instruction s) targets.Add(s);
                else if (i.Operand is Instruction[] many) foreach (var x in many) targets.Add(x);
            }

            var counts = new Counts();
            for (int i = 1; i < ins.Count; i++)
            {
                if (!seen[i] || !targets.Contains(ins[i])) continue;
                // nearest preceding REACHABLE instruction
                int p = i - 1;
                while (p >= 0 && !seen[p]) p--;
                if (p < 0) continue;
                var pc = ins[p].OpCode.Code;
                if (pc != Code.Br && pc != Code.Br_S) continue;
                int carry = depth[p];
                if (carry == depth[i]) continue;
                if (carry < depth[i]) counts.under++; else counts.over++;
            }
            return counts;
        }
    }
}
