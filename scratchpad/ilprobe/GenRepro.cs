using System;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace IlProbe
{
    // Emits a tiny self-contained assembly carrying the EXACT IL shape MonoVM refuses in
    // iStuntWP7.iStuntGame::.cctor — an unreachable instruction sitting between a `br` and a
    // branch target whose real entry depth is higher than the depth that br carries.
    //
    //   IL_0015  br.s IL_001b     <- carries depth 0
    //   IL_0017  ldc.i4.0         <- UNREACHABLE; Mono skips it
    //   IL_0018  pop              <- branch target, real entry depth 1  => Mono says underflow
    //
    // Everything is primitives, so the assembly references nothing but mscorlib and can be
    // loaded on any runtime. CoreCLR and ILVerify both accept it; MonoVM refuses it.
    //   usage: ilprobe --genrepro <out.dll>
    internal static class GenRepro
    {
        public static void Run(string[] args)
        {
            string outPath = args[Array.IndexOf(args, "--genrepro") + 1];

            var asm = AssemblyDefinition.CreateAssembly(
                new AssemblyNameDefinition("MonoRepro", new Version(1, 0, 0, 0)), "MonoRepro", ModuleKind.Dll);
            var module = asm.MainModule;

            var type = new TypeDefinition("MonoRepro", "Repro",
                TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.Class,
                module.TypeSystem.Object);
            module.Types.Add(type);

            var method = new MethodDefinition("Run",
                MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
                module.TypeSystem.Void);
            type.Methods.Add(method);

            var il = method.Body.GetILProcessor();

            // Labels mirroring the real .cctor's control flow.
            var swA = Instruction.Create(OpCodes.Br, Instruction.Create(OpCodes.Nop));   // placeholder, fixed below
            var lA = Instruction.Create(OpCodes.Nop);   // switch target A  -> br to lC
            var dead = Instruction.Create(OpCodes.Ldc_I4_0);                              // UNREACHABLE
            var lD = Instruction.Create(OpCodes.Pop);   // branch target entered at depth 1
            var lC = Instruction.Create(OpCodes.Ldc_I4_0);
            var lE = Instruction.Create(OpCodes.Nop);
            var lB = Instruction.Create(OpCodes.Nop);   // switch target B
            var ret = Instruction.Create(OpCodes.Ret);

            var brToC = Instruction.Create(OpCodes.Br, lC);
            var brDtoE = Instruction.Create(OpCodes.Br, lE);
            var brCtoD = Instruction.Create(OpCodes.Br, lD);

            // ldc.i4.1; ldc.i4.1; ceq   -> one int on the stack
            il.Append(Instruction.Create(OpCodes.Ldc_I4_1));
            il.Append(Instruction.Create(OpCodes.Ldc_I4_1));
            il.Append(Instruction.Create(OpCodes.Ceq));
            // switch consumes it; targets A and B, falls through to A
            il.Append(Instruction.Create(OpCodes.Switch, new[] { lA, lB, lA }));

            il.Append(lA);          // depth 0
            il.Append(brToC);       // br lC   — carries depth 0
            il.Append(dead);        // ldc.i4.0, UNREACHABLE (nothing branches here, br above)
            il.Append(lD);          // pop     — entered from brCtoD at depth 1
            il.Append(brDtoE);      // br lE
            il.Append(lC);          // ldc.i4.0 -> depth 1
            il.Append(brCtoD);      // br lD   — arrives at depth 1
            il.Append(lE);          // nop
            il.Append(lB);          // nop
            il.Append(ret);

            method.Body.MaxStackSize = 4;

            // A second entry point so the result is observable: returns 42 if Run() completed.
            var probe = new MethodDefinition("Check",
                MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
                module.TypeSystem.Int32);
            type.Methods.Add(probe);
            var pil = probe.Body.GetILProcessor();
            pil.Append(Instruction.Create(OpCodes.Call, method));
            pil.Append(Instruction.Create(OpCodes.Ldc_I4, 42));
            pil.Append(Instruction.Create(OpCodes.Ret));
            probe.Body.MaxStackSize = 1;

            asm.Write(outPath);
            Console.Error.WriteLine($"[genrepro] wrote {outPath}");

            var counts = Scan.Analyse(method);
            Console.Error.WriteLine($"[genrepro] model says: underflow={counts.under} leftover={counts.over}");
        }
    }
}
