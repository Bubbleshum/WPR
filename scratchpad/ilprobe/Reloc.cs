using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

namespace IlProbe
{
    // One-off experiment: relocate the block starting at a given IL offset to the end of the
    // method and repoint its entrants, exactly as ApplicationPatcher.RelocateMonoStackConflictBlocks
    // does for ret/throw blocks.  Used to confirm the mechanism cures a Mono underflow before
    // generalising the real pass.
    //   usage: ilprobe <dll> <Type::Method> --reloc <hexOffset> --out <path>
    internal static class Reloc
    {
        public static void Run(string[] args)
        {
            string dll = args[0], want = args[1];
            int at = Convert.ToInt32(args[Array.IndexOf(args, "--reloc") + 1], 16);
            string outPath = args[Array.IndexOf(args, "--out") + 1];

            var asm = AssemblyDefinition.ReadAssembly(dll, new ReaderParameters { ReadWrite = false });
            MethodDefinition target = null;
            foreach (var t in asm.MainModule.GetTypes())
                foreach (var m in t.Methods)
                    if ((t.FullName + "::" + m.Name).Equals(want, StringComparison.OrdinalIgnoreCase)) target = m;

            if (target == null) { Console.Error.WriteLine("method not found"); return; }

            var body = target.Body;
            body.SimplifyMacros();
            var ins = body.Instructions.ToList();

            int start = ins.FindIndex(i => i.Offset == at);
            if (start < 0)
            {
                // SimplifyMacros renumbers; find by original order instead
                Console.Error.WriteLine("offset not found after SimplifyMacros; listing:");
                foreach (var i in ins) Console.Error.WriteLine($"  IL_{i.Offset:x4} {i.OpCode.Name}");
                return;
            }

            // block = [start .. first ret/throw/br], stopping before any other entry point
            var targets = new HashSet<Instruction>();
            foreach (var i in ins)
            {
                if (i.Operand is Instruction s) targets.Add(s);
                else if (i.Operand is Instruction[] many) foreach (var x in many) targets.Add(x);
            }

            int end = start;
            while (end < ins.Count)
            {
                if (end > start && targets.Contains(ins[end])) { Console.Error.WriteLine("second entry point"); return; }
                var c = ins[end].OpCode.Code;
                if (c == Code.Ret || c == Code.Throw || c == Code.Br) break;
                end++;
            }
            Console.Error.WriteLine($"block IL_{ins[start].Offset:x4}..IL_{ins[end].Offset:x4} ({end - start + 1} instrs, ends {ins[end].OpCode.Code})");

            var il = body.GetILProcessor();
            var clones = new List<Instruction>();
            var map = new Dictionary<Instruction, Instruction>();
            for (int i = start; i <= end; i++)
            {
                var c = CloneInstruction(ins[i]);
                clones.Add(c);
                map[ins[i]] = c;
            }
            // fix intra-block branches
            foreach (var c in clones)
                if (c.Operand is Instruction op && map.TryGetValue(op, out var rep)) c.Operand = rep;

            // append after the last instruction
            var tail = body.Instructions[body.Instructions.Count - 1];
            foreach (var c in clones) { il.InsertAfter(tail, c); tail = c; }

            // repoint every entrant of the block head (outside the block) at the clone
            var head = ins[start];
            var cloneHead = clones[0];
            int repointed = 0;
            foreach (var i in body.Instructions)
            {
                if (clones.Contains(i)) continue;
                if (i.Operand is Instruction s && s == head) { i.Operand = cloneHead; repointed++; }
                else if (i.Operand is Instruction[] many)
                {
                    for (int k = 0; k < many.Length; k++) if (many[k] == head) { many[k] = cloneHead; repointed++; }
                }
            }
            Console.Error.WriteLine($"repointed {repointed} entrant(s)");

            body.OptimizeMacros();
            asm.Write(outPath);
            Console.Error.WriteLine($"wrote {outPath}");
        }

        internal static Instruction CloneInstruction(Instruction i)
        {
            if (i.Operand == null) return Instruction.Create(i.OpCode);
            switch (i.Operand)
            {
                case Instruction x: return Instruction.Create(i.OpCode, x);
                case TypeReference x: return Instruction.Create(i.OpCode, x);
                case MethodReference x: return Instruction.Create(i.OpCode, x);
                case FieldReference x: return Instruction.Create(i.OpCode, x);
                case string x: return Instruction.Create(i.OpCode, x);
                case sbyte x: return Instruction.Create(i.OpCode, x);
                case byte x: return Instruction.Create(i.OpCode, x);
                case int x: return Instruction.Create(i.OpCode, x);
                case long x: return Instruction.Create(i.OpCode, x);
                case float x: return Instruction.Create(i.OpCode, x);
                case double x: return Instruction.Create(i.OpCode, x);
                case VariableDefinition x: return Instruction.Create(i.OpCode, x);
                case ParameterDefinition x: return Instruction.Create(i.OpCode, x);
                case Instruction[] x: return Instruction.Create(i.OpCode, (Instruction[])x.Clone());
                default: throw new NotSupportedException(i.Operand.GetType().Name);
            }
        }
    }
}
