using System;
using System.Collections.Generic;
using System.Linq;

using Mono.Cecil;
using Mono.Cecil.Cil;

namespace WPR.Achievements
{
    /// <summary>
    /// Finds the achievement keys a package's IL hands to its award call, for a package that has
    /// no XLAST configuration to say so. The keys are string constants that reach the key argument
    /// of an award call: directly (<c>ldstr</c>), through a local, a field, an element of a string
    /// array initialised with constants, a parameterless method returning one, or a method's own
    /// parameter, in which case that method becomes an award call too (a game's wrapper).
    /// </summary>
    /// <remarks>
    /// <para><b>Award calls</b> are <c>SignedInGamer.AwardAchievement</c> /
    /// <c>BeginAwardAchievement</c>, and any method of the package whose name says it awards or
    /// unlocks an achievement and whose first parameter is a string. The second is a shape, not a
    /// name list: it is how a game that wraps the call, or that shipped with the call stripped
    /// (the wrapper left as a stub), still names its keys.</para>
    ///
    /// <para><b>Order</b> is the order the constants are found in, which for an array is its index
    /// order, so a catalogue lists them as the game declared them.</para>
    /// </remarks>
    public sealed class AwardCallKeyScanner
    {
        /// <summary>Where a key came from when it was an element of a constant string array.</summary>
        public sealed record ArrayOrigin(FieldDefinition Field, int Index);

        private const int MaxDepth = 8;
        private readonly List<ModuleDefinition> _modules;
        private readonly Dictionary<string, List<(MethodDefinition Method, Instruction Store)>> _fieldStores = new(StringComparer.Ordinal);
        private readonly Dictionary<string, MethodDefinition> _methods = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Dictionary<int, string>?> _arrays = new(StringComparer.Ordinal);

        /// <summary>Methods whose given parameter is an achievement key, by full name.</summary>
        private readonly Dictionary<string, int> _wrappers = new(StringComparer.Ordinal);

        private readonly List<string> _keys = new();
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

        public AwardCallKeyScanner(IEnumerable<ModuleDefinition> modules)
        {
            _modules = modules.ToList();
            foreach (MethodDefinition method in AllMethods())
            {
                _methods[method.FullName] = method;
                foreach (Instruction i in method.Body.Instructions)
                {
                    if ((i.OpCode.Code is Code.Stfld or Code.Stsfld) && i.Operand is FieldReference f)
                    {
                        if (!_fieldStores.TryGetValue(f.FullName, out var list)) _fieldStores[f.FullName] = list = new();
                        list.Add((method, i));
                    }
                }
            }
        }

        /// <summary>The keys found, in discovery order.</summary>
        public IReadOnlyList<string> Keys => _keys;

        /// <summary>Keys that came from a constant string array, and where.</summary>
        public Dictionary<string, ArrayOrigin> Origins { get; } = new(StringComparer.Ordinal);

        /// <summary>True when the package calls <c>SignedInGamer</c>'s award methods itself.</summary>
        public bool CallsSignedInGamer { get; private set; }

        /// <summary>The number of award call sites seen (SignedInGamer and wrappers).</summary>
        public int CallSites { get; private set; }

        public AwardCallKeyScanner Scan()
        {
            // Wrappers discovered while tracing are award calls too: repeat until none is new.
            int wrappers;
            do
            {
                wrappers = _wrappers.Count;
                CallSites = 0;
                foreach (MethodDefinition method in AllMethods())
                {
                    var instructions = method.Body.Instructions;
                    for (int i = 0; i < instructions.Count; i++)
                    {
                        if (instructions[i].OpCode.Code is not (Code.Call or Code.Callvirt or Code.Newobj)) continue;
                        if (instructions[i].Operand is not MethodReference callee) continue;
                        int? keyParam = KeyParameter(callee);
                        if (keyParam is not { } p) continue;
                        CallSites++;
                        int fromTop = callee.Parameters.Count - 1 - p;
                        if (Producer(method, i, fromTop) is { } producer)
                        {
                            Collect(method, producer, 0, null);
                        }
                    }
                }
            }
            while (_wrappers.Count != wrappers);
            return this;
        }

        /// <summary>Which parameter of <paramref name="callee"/> is an achievement key, if it is an award call.</summary>
        private int? KeyParameter(MethodReference callee)
        {
            if (callee.Name is "AwardAchievement" or "BeginAwardAchievement" &&
                callee.DeclaringType?.Name == "SignedInGamer" &&
                callee.Parameters.Count > 0 && callee.Parameters[0].ParameterType.FullName == "System.String")
            {
                // XNA's own (or WPR's, in a package already patched), not a same-named type a
                // package defines for itself, as a SharpDX port's own XNA layer does.
                if (callee.DeclaringType.Scope is AssemblyNameReference { Name: "Microsoft.Xna.Framework.GamerServices" or "WPR.Framework.Xna" })
                {
                    CallsSignedInGamer = true;
                }
                return 0;
            }
            if (_wrappers.TryGetValue(callee.FullName, out int wrapped)) return wrapped;

            string name = callee.Name;
            bool named = name.Contains("AwardAchievement", StringComparison.OrdinalIgnoreCase) ||
                         name.Contains("UnlockAchievement", StringComparison.OrdinalIgnoreCase);
            if (named && callee.Parameters.Count > 0 && callee.Parameters[0].ParameterType.FullName == "System.String" &&
                _methods.ContainsKey(callee.FullName))
            {
                return 0;
            }
            return null;
        }

        /// <summary>
        /// The instruction that pushed the value <paramref name="fromTop"/> slots below the top of
        /// the stack just before <paramref name="index"/>, walking back through straight-line code.
        /// Null when control flow merges in between or the shape is not understood.
        /// </summary>
        private static Instruction? Producer(MethodDefinition method, int index, int fromTop)
        {
            var instructions = method.Body.Instructions;
            int need = fromTop;
            for (int j = index - 1; j >= 0; j--)
            {
                Instruction i = instructions[j];
                FlowControl flow = i.OpCode.FlowControl;
                if (flow is FlowControl.Branch or FlowControl.Return or FlowControl.Throw) return null;
                int pushes = Pushes(i);
                int pops = Pops(i, method);
                if (pushes < 0 || pops < 0) return null;
                if (need < pushes) return pushes == 1 ? i : null;
                need = need - pushes + pops;
            }
            return null;
        }

        private void Collect(MethodDefinition method, Instruction producer, int depth, ArrayOrigin? origin)
        {
            if (depth > MaxDepth) return;
            switch (producer.OpCode.Code)
            {
                case Code.Ldstr:
                    Add((string)producer.Operand, origin);
                    break;

                case Code.Ldarg: case Code.Ldarg_S: case Code.Ldarg_0: case Code.Ldarg_1: case Code.Ldarg_2: case Code.Ldarg_3:
                {
                    int index = ArgumentIndex(method, producer);
                    if (index >= 0 && index < method.Parameters.Count &&
                        method.Parameters[index].ParameterType.FullName == "System.String")
                    {
                        _wrappers.TryAdd(method.FullName, index);
                    }
                    break;
                }

                case Code.Ldloc: case Code.Ldloc_S: case Code.Ldloc_0: case Code.Ldloc_1: case Code.Ldloc_2: case Code.Ldloc_3:
                {
                    int local = LocalIndex(producer);
                    var instructions = method.Body.Instructions;
                    for (int j = 0; j < instructions.Count; j++)
                    {
                        if (IsStloc(instructions[j], out int stored) && stored == local &&
                            Producer(method, j, 0) is { } value)
                        {
                            Collect(method, value, depth + 1, origin);
                        }
                    }
                    break;
                }

                case Code.Ldfld: case Code.Ldsfld:
                {
                    FieldReference field = (FieldReference)producer.Operand;
                    if (!_fieldStores.TryGetValue(field.FullName, out var stores)) break;
                    foreach ((MethodDefinition m, Instruction store) in stores)
                    {
                        int at = m.Body.Instructions.IndexOf(store);
                        if (Producer(m, at, 0) is { } value) Collect(m, value, depth + 1, origin);
                    }
                    break;
                }

                case Code.Ldelem_Ref: case Code.Ldelem_Any:
                {
                    // The array: one slot below the index.
                    int at = method.Body.Instructions.IndexOf(producer);
                    if (Producer(method, at, 1) is { } array && ArrayField(method, array, depth) is { } field &&
                        StringArray(field) is { } elements)
                    {
                        foreach ((int index, string value) in elements.OrderBy(e => e.Key))
                        {
                            Add(value, new ArrayOrigin(field, index));
                        }
                    }
                    break;
                }

                case Code.Call: case Code.Callvirt:
                {
                    // A parameterless method of the package returning a string: what it returns.
                    if (producer.Operand is MethodReference callee && callee.Parameters.Count == 0 &&
                        callee.ReturnType.FullName == "System.String" &&
                        _methods.TryGetValue(callee.FullName, out MethodDefinition? target))
                    {
                        var instructions = target.Body.Instructions;
                        for (int j = 0; j < instructions.Count; j++)
                        {
                            if (instructions[j].OpCode.Code == Code.Ret && Producer(target, j, 0) is { } value)
                            {
                                Collect(target, value, depth + 1, origin);
                            }
                        }
                    }
                    break;
                }
            }
        }

        /// <summary>The field an array value was loaded from (directly, or through a local).</summary>
        private FieldDefinition? ArrayField(MethodDefinition method, Instruction array, int depth)
        {
            if (depth > MaxDepth) return null;
            if (array.OpCode.Code is Code.Ldsfld or Code.Ldfld)
            {
                try { return (array.Operand as FieldReference)?.Resolve(); }
                catch (Exception) { return null; }
            }
            if (IsLdloc(array, out int local))
            {
                var instructions = method.Body.Instructions;
                for (int j = 0; j < instructions.Count; j++)
                {
                    if (IsStloc(instructions[j], out int stored) && stored == local &&
                        Producer(method, j, 0) is { } value && ArrayField(method, value, depth + 1) is { } field)
                    {
                        return field;
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// The constant elements of a string array field, by index: the
        /// <c>newarr; (dup; ldc.i4 i; ldstr s; stelem.ref)*; stsfld</c> run that initialises it.
        /// Null when the field is never given such an array.
        /// </summary>
        public Dictionary<int, string>? StringArray(FieldDefinition field)
        {
            if (_arrays.TryGetValue(field.FullName, out var cached)) return cached;
            Dictionary<int, string>? result = null;
            if (field.FieldType is ArrayType { ElementType.FullName: "System.String" } &&
                _fieldStores.TryGetValue(field.FullName, out var stores))
            {
                foreach ((MethodDefinition m, Instruction store) in stores)
                {
                    var instructions = m.Body.Instructions;
                    int end = instructions.IndexOf(store);
                    int start = end - 1;
                    while (start >= 0 && instructions[start].OpCode.Code != Code.Newarr) start--;
                    if (start < 0) continue;
                    Dictionary<int, string> elements = new();
                    for (int j = start + 1; j + 2 < end; j++)
                    {
                        if (instructions[j + 1].OpCode.Code == Code.Ldstr && instructions[j + 2].OpCode.Code == Code.Stelem_Ref &&
                            ConstantInt(instructions[j]) is { } index)
                        {
                            elements[index] = (string)instructions[j + 1].Operand;
                        }
                    }
                    if (elements.Count > 0) { result = elements; break; }
                }
            }
            _arrays[field.FullName] = result;
            return result;
        }

        private void Add(string value, ArrayOrigin? origin)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 128) return;
            if (_seen.Add(value))
            {
                _keys.Add(value);
                if (origin is not null) Origins[value] = origin;
            }
        }

        private IEnumerable<MethodDefinition> AllMethods()
        {
            foreach (ModuleDefinition module in _modules)
            {
                foreach (TypeDefinition type in module.GetTypes())
                {
                    foreach (MethodDefinition method in type.Methods)
                    {
                        if (method.HasBody) yield return method;
                    }
                }
            }
        }

        private static int? ConstantInt(Instruction i) => i.OpCode.Code switch
        {
            Code.Ldc_I4_0 => 0, Code.Ldc_I4_1 => 1, Code.Ldc_I4_2 => 2, Code.Ldc_I4_3 => 3, Code.Ldc_I4_4 => 4,
            Code.Ldc_I4_5 => 5, Code.Ldc_I4_6 => 6, Code.Ldc_I4_7 => 7, Code.Ldc_I4_8 => 8,
            Code.Ldc_I4_S => (sbyte)i.Operand, Code.Ldc_I4 => (int)i.Operand,
            _ => null,
        };

        /// <summary>The parameter index (not counting <c>this</c>) an <c>ldarg</c> loads; -1 for <c>this</c>.</summary>
        private static int ArgumentIndex(MethodDefinition method, Instruction i)
        {
            if (i.Operand is ParameterDefinition p) return p.Index;
            int raw = i.OpCode.Code switch
            {
                Code.Ldarg_0 => 0, Code.Ldarg_1 => 1, Code.Ldarg_2 => 2, Code.Ldarg_3 => 3,
                _ => -1,
            };
            return raw < 0 ? -1 : method.HasThis ? raw - 1 : raw;
        }

        private static int LocalIndex(Instruction i) => IsLdloc(i, out int index) ? index : -1;

        private static bool IsLdloc(Instruction i, out int index)
        {
            index = i.OpCode.Code switch
            {
                Code.Ldloc_0 => 0, Code.Ldloc_1 => 1, Code.Ldloc_2 => 2, Code.Ldloc_3 => 3,
                Code.Ldloc or Code.Ldloc_S => ((VariableDefinition)i.Operand).Index,
                _ => -1,
            };
            return index >= 0;
        }

        private static bool IsStloc(Instruction i, out int index)
        {
            index = i.OpCode.Code switch
            {
                Code.Stloc_0 => 0, Code.Stloc_1 => 1, Code.Stloc_2 => 2, Code.Stloc_3 => 3,
                Code.Stloc or Code.Stloc_S => ((VariableDefinition)i.Operand).Index,
                _ => -1,
            };
            return index >= 0;
        }

        private static int Pushes(Instruction i)
        {
            switch (i.OpCode.StackBehaviourPush)
            {
                case StackBehaviour.Push0: return 0;
                case StackBehaviour.Push1: case StackBehaviour.Pushi: case StackBehaviour.Pushi8:
                case StackBehaviour.Pushr4: case StackBehaviour.Pushr8: case StackBehaviour.Pushref:
                    return 1;
                case StackBehaviour.Push1_push1: return 2;
                case StackBehaviour.Varpush:
                    return i.Operand is MethodReference m && m.ReturnType.FullName != "System.Void" || i.OpCode.Code == Code.Newobj ? 1 : 0;
                default: return -1;
            }
        }

        private static int Pops(Instruction i, MethodDefinition method)
        {
            switch (i.OpCode.StackBehaviourPop)
            {
                case StackBehaviour.Pop0: return 0;
                case StackBehaviour.Pop1: case StackBehaviour.Popi: case StackBehaviour.Popref: return 1;
                case StackBehaviour.Pop1_pop1: case StackBehaviour.Popi_pop1: case StackBehaviour.Popi_popi:
                case StackBehaviour.Popi_popi8: case StackBehaviour.Popi_popr4: case StackBehaviour.Popi_popr8:
                case StackBehaviour.Popref_pop1: case StackBehaviour.Popref_popi:
                    return 2;
                case StackBehaviour.Popi_popi_popi: case StackBehaviour.Popref_popi_popi: case StackBehaviour.Popref_popi_popi8:
                case StackBehaviour.Popref_popi_popr4: case StackBehaviour.Popref_popi_popr8: case StackBehaviour.Popref_popi_popref:
                    return 3;
                case StackBehaviour.Varpop:
                    if (i.OpCode.Code == Code.Ret) return method.ReturnType.FullName == "System.Void" ? 0 : 1;
                    if (i.Operand is MethodReference m)
                    {
                        int count = m.Parameters.Count;
                        if (m.HasThis && i.OpCode.Code != Code.Newobj) count++;
                        return count;
                    }
                    return -1;
                case StackBehaviour.PopAll: return -1;
                default: return -1;
            }
        }
    }
}
