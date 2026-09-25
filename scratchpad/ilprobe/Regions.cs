using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace IlProbe
{
    // Reports, for one method, where its underflow conflicts sit relative to its exception
    // handler regions — the question that decides whether the relocation pass could handle a
    // method it currently skips outright for having handlers.
    //   usage: ilprobe --regions <dll> <Type::Method>
    internal static class Regions
    {
        public static void Run(string[] args)
        {
            int k = Array.IndexOf(args, "--regions");
            string dll = args[k + 1], want = args[k + 2];
            var asm = AssemblyDefinition.ReadAssembly(dll);
            foreach (var t in asm.MainModule.GetTypes())
                foreach (var m in t.Methods)
                {
                    if (!m.HasBody) continue;
                    if (!(t.FullName + "::" + m.Name).Equals(want, StringComparison.OrdinalIgnoreCase)) continue;
                    Report(m);
                }
        }

        static void Report(MethodDefinition m)
        {
            var ins = m.Body.Instructions.ToList();
            var idx = new Dictionary<Instruction, int>();
            for (int i = 0; i < ins.Count; i++) idx[ins[i]] = i;
            bool[] seen; int[] depth;
            Scan.Flow(m, ins, idx, out seen, out depth);

            Console.WriteLine($"=== {m.FullName}  instrs={ins.Count} handlers={m.Body.ExceptionHandlers.Count} last={ins.Last().OpCode.Code}");
            var ranges = new List<(int lo, int hi, string what)>();
            foreach (var h in m.Body.ExceptionHandlers)
            {
                int ts = idx[h.TryStart], te = h.TryEnd != null ? idx[h.TryEnd] : ins.Count;
                int hs = idx[h.HandlerStart], he = h.HandlerEnd != null ? idx[h.HandlerEnd] : ins.Count;
                Console.WriteLine($"  {h.HandlerType}: try [{ts}..{te}) handler [{hs}..{he})");
                ranges.Add((ts, te, "try")); ranges.Add((hs, he, "handler"));
            }
            Console.WriteLine($"  tail index = {ins.Count - 1}; tail inside a region: {ranges.Any(r => ins.Count - 1 >= r.lo && ins.Count - 1 < r.hi)}");

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
                if (depth[p] >= depth[i]) continue;
                var inside = ranges.Where(r => i >= r.lo && i < r.hi).Select(r => r.what).ToList();
                Console.WriteLine($"  UNDERFLOW at index {i} (IL_{ins[i].Offset:x4} {ins[i].OpCode.Name}) carry={depth[p]} entry={depth[i]} regions=[{string.Join(",", inside)}]");
            }
        }
    }
}
