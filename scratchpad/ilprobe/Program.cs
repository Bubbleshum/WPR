using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace IlProbe
{
    // Dumps one method's IL and reports the Mono stack-conflict candidates the patcher's
    // RelocateMonoStackConflictBlocks pass looks for.
    //   usage: ilprobe <dll> <Type::Method>  [--il]
    internal static class Program
    {
        public static void Main(string[] args)
        {
            if (args.Length < 2 && !args.Contains("--scan")) { Console.Error.WriteLine("usage: ilprobe <dll> <Type::Method> [--il]"); return; }
            if (args.Contains("--mixed")) { Mixed.Run(args); return; }
            if (args.Contains("--genrepro")) { GenRepro.Run(args); return; }
            if (args.Contains("--regions")) { Regions.Run(args); return; }
            if (args.Contains("--scan")) { Scan.Run(args); return; }
            if (args.Contains("--fix")) { Fix.Run(args); return; }
            if (args.Contains("--reloc")) { Reloc.Run(args); return; }
            bool dumpIl = args.Contains("--il");

            var rp = new ReaderParameters { ReadSymbols = false };
            var asm = AssemblyDefinition.ReadAssembly(args[0], rp);
            string want = args[1];

            foreach (var type in asm.MainModule.GetTypes())
            {
                foreach (var m in type.Methods)
                {
                    string full = type.FullName + "::" + m.Name;
                    if (!full.Equals(want, StringComparison.OrdinalIgnoreCase) &&
                        full.IndexOf(want, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    Report(m, dumpIl);
                }
            }
        }

        static void Report(MethodDefinition m, bool dumpIl)
        {
            Console.WriteLine($"=== {m.FullName}");
            if (!m.HasBody) { Console.WriteLine("  (no body)"); return; }
            var body = m.Body;
            var ins = body.Instructions.ToList();
            Console.WriteLine($"  instructions={ins.Count} handlers={body.ExceptionHandlers.Count} " +
                              $"maxStack={body.MaxStackSize} last={ins[ins.Count - 1].OpCode.Code}");

            bool skipHandlers = body.ExceptionHandlers.Count > 0;
            bool skipLast = ins[ins.Count - 1].OpCode.Code != Code.Ret && ins[ins.Count - 1].OpCode.Code != Code.Throw;
            if (skipHandlers) Console.WriteLine("  PASS SKIPS: has exception handlers");
            if (skipLast) Console.WriteLine("  PASS SKIPS: does not end in ret/throw");

            int[] depth = Depths(ins);

            var targets = new HashSet<Instruction>();
            foreach (var i in ins)
            {
                if (i.Operand is Instruction s) targets.Add(s);
                else if (i.Operand is Instruction[] many) foreach (var t in many) targets.Add(t);
            }

            int cand = 0;
            for (int i = 1; i < ins.Count; i++)
            {
                var prev = ins[i - 1];
                if (prev.OpCode.Code != Code.Br && prev.OpCode.Code != Code.Br_S) continue;
                if (!targets.Contains(ins[i])) continue;
                if (depth[i - 1] <= 0 || depth[i] < 0) continue;
                cand++;
                Console.WriteLine($"  CANDIDATE @IL_{ins[i].Offset:x4} carried={depth[i - 1]} entry={depth[i]} op={ins[i].OpCode.Code}");
            }
            Console.WriteLine($"  candidates(strict)={cand}");

            // the WIDE predicate, for information only
            int wide = 0;
            for (int i = 1; i < ins.Count; i++)
            {
                var prev = ins[i - 1];
                if (prev.OpCode.Code != Code.Br && prev.OpCode.Code != Code.Br_S) continue;
                if (!targets.Contains(ins[i])) continue;
                if (depth[i - 1] == depth[i]) continue;
                wide++;
            }
            Console.WriteLine($"  candidates(wide)={wide}");

            // does it already look relocated? (patcher appends clones at the end)
            if (dumpIl)
            {
                Console.WriteLine("  --- IL ---");
                for (int i = 0; i < ins.Count; i++)
                {
                    string mark = targets.Contains(ins[i]) ? ">" : " ";
                    Console.WriteLine($"  {mark} IL_{ins[i].Offset:x4} d={depth[i],3} {ins[i].OpCode.Name} {Fmt(ins[i].Operand)}");
                }
            }
        }

        static string Fmt(object o)
        {
            if (o == null) return "";
            if (o is Instruction i) return "IL_" + i.Offset.ToString("x4");
            if (o is Instruction[] a) return string.Join(",", a.Select(x => "IL_" + x.Offset.ToString("x4")));
            return o.ToString();
        }

        // Mirrors ApplicationPatcher.ComputeEntryStackDepths: linear carry, branch targets get the
        // depth of the first path that reaches them.
        static int[] Depths(List<Instruction> ins)
        {
            int[] d = new int[ins.Count];
            for (int i = 0; i < d.Length; i++) d[i] = -1;
            var index = new Dictionary<Instruction, int>();
            for (int i = 0; i < ins.Count; i++) index[ins[i]] = i;

            var work = new Stack<(int, int)>();
            work.Push((0, 0));
            while (work.Count > 0)
            {
                var (i, depth) = work.Pop();
                while (i < ins.Count)
                {
                    if (d[i] >= 0) break;
                    d[i] = depth;
                    var op = ins[i];
                    int after = depth + Delta(op);
                    if (after < 0) after = 0;

                    if (op.Operand is Instruction t && index.TryGetValue(t, out int ti)) work.Push((ti, after));
                    else if (op.Operand is Instruction[] many)
                        foreach (var x in many) if (index.TryGetValue(x, out int xi)) work.Push((xi, after));

                    var fc = op.OpCode.FlowControl;
                    if (fc == FlowControl.Branch || fc == FlowControl.Return || fc == FlowControl.Throw) break;
                    depth = after;
                    i++;
                }
            }
            for (int i = 0; i < d.Length; i++) if (d[i] < 0) d[i] = 0;
            return d;
        }

        internal static int Delta(Instruction ins)
        {
            int pop = 0, push = 0;
            switch (ins.OpCode.StackBehaviourPop)
            {
                case StackBehaviour.Pop0: pop = 0; break;
                case StackBehaviour.Pop1: case StackBehaviour.Popi: case StackBehaviour.Popref: pop = 1; break;
                case StackBehaviour.Pop1_pop1:
                case StackBehaviour.Popi_pop1: case StackBehaviour.Popi_popi: case StackBehaviour.Popi_popi8:
                case StackBehaviour.Popi_popr4: case StackBehaviour.Popi_popr8: case StackBehaviour.Popref_pop1:
                case StackBehaviour.Popref_popi: pop = 2; break;
                case StackBehaviour.Popi_popi_popi: case StackBehaviour.Popref_popi_popi:
                case StackBehaviour.Popref_popi_popi8: case StackBehaviour.Popref_popi_popr4:
                case StackBehaviour.Popref_popi_popr8: case StackBehaviour.Popref_popi_popref: pop = 3; break;
                case StackBehaviour.PopAll: pop = 0; break;
                case StackBehaviour.Varpop:
                    var mr = ins.Operand as MethodReference;
                    if (mr != null)
                    {
                        pop = mr.Parameters.Count + (mr.HasThis && ins.OpCode.Code != Code.Newobj ? 1 : 0);
                    }
                    else if (ins.OpCode.Code == Code.Ret) pop = 1;
                    break;
            }
            switch (ins.OpCode.StackBehaviourPush)
            {
                case StackBehaviour.Push0: push = 0; break;
                case StackBehaviour.Push1: case StackBehaviour.Pushi: case StackBehaviour.Pushi8:
                case StackBehaviour.Pushr4: case StackBehaviour.Pushr8: case StackBehaviour.Pushref: push = 1; break;
                case StackBehaviour.Push1_push1: push = 2; break;
                case StackBehaviour.Varpush:
                    var mr2 = ins.Operand as MethodReference;
                    if (mr2 != null && mr2.ReturnType.FullName != "System.Void") push = 1;
                    break;
            }
            return push - pop;
        }
    }
}
