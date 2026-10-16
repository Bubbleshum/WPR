using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace WPR.Wp8Native
{
    public static partial class XamlShellInference
    {
        private sealed partial class ShellReading
        {
            /// <summary>An evaluation-stack value whose content is not known (a field, a call result, an argument).</summary>
            private sealed class Unknown
            {
                public static readonly Unknown Value = new();
            }

            /// <summary>
            /// The arguments of the call at instruction <paramref name="callIndex"/>, as the page computes
            /// them on this display, by running the method's IL from its start up to that call over a tiny
            /// abstract machine. Null in a slot whose value is not known.
            /// </summary>
            /// <remarks>
            /// <para>Most shells compute their sizes rather than load them: 2020: My Country's
            /// <c>UpdateViewDimensions</c> is <c>Floor(Host.Content.ActualWidth * ScaleFactor / 100 + 0.5)</c>
            /// minus a system-tray height, and its <c>ScaleFactor</c> setter gets
            /// <c>Host.Content.ScaleFactor / 100f</c> (1.6, where Amazing Alex passes the raw 160). Guessing
            /// from the parameter name gave it <c>SetViewDimensions(0, 0, 0, 0)</c> and a scale factor of
            /// 160, and every touch it scaled by them landed off the screen.</para>
            /// <para>What is known: numeric constants, the locals they are stored in, arithmetic and
            /// conversions, <c>Math.Floor</c>/<c>Ceiling</c>/<c>Round</c>/<c>Min</c>/<c>Max</c>/<c>Abs</c>,
            /// <c>new Size(w, h)</c>, the <c>System.Windows.Interop.Content</c> sizes and scale from
            /// <see cref="Wp8Display"/> (portrait, as on WP8 in every orientation), and
            /// <c>SystemTray.IsVisible</c> from the page XAML. A branch on a known value is taken as the
            /// CLR would take it; one on an unknown value falls through. Anything else is unknown, and
            /// the caller falls back to answering by name and type.</para>
            /// </remarks>
            public object?[] EvaluateArguments(byte[] il, int callIndex, int parameters)
            {
                object?[] none = new object?[parameters];
                if (parameters == 0)
                {
                    return none;
                }

                List<(int Offset, ILOpCode Op, long Operand, int Next)> code = [];
                Dictionary<int, int> byOffset = [];
                int at = 0;
                while (at < il.Length)
                {
                    int start = at;
                    int value = il[at++];
                    if (value == 0xFE && at < il.Length)
                    {
                        value = 0xFE00 | il[at++];
                    }

                    ILOpCode op = (ILOpCode)value;
                    int size = OperandSize(op, il, at);
                    if (at + size > il.Length)
                    {
                        break;
                    }

                    long operand = size switch
                    {
                        1 => op is >= ILOpCode.Br_s and <= ILOpCode.Blt_un_s or ILOpCode.Leave_s or ILOpCode.Ldc_i4_s ? (sbyte)il[at] : il[at],
                        2 => BitConverter.ToUInt16(il, at),
                        4 => BitConverter.ToInt32(il, at),
                        8 => BitConverter.ToInt64(il, at),
                        _ => 0,
                    };
                    byOffset[start] = code.Count;
                    code.Add((start, op, operand, at + size));
                    at += size;
                }

                if (callIndex >= code.Count)
                {
                    return none;
                }

                Stack<object?> stack = new();
                Dictionary<int, object?> locals = [];
                object? Pop() => stack.Count > 0 ? stack.Pop() : Unknown.Value;
                static double? Num(object? v) => v switch { double d => d, long l => l, _ => null };

                int pc = 0;
                for (int steps = 0; steps < 20000 && pc < code.Count; steps++)
                {
                    if (pc == callIndex)
                    {
                        object?[] result = new object?[parameters];
                        for (int i = parameters - 1; i >= 0; i--)
                        {
                            object? v = Pop();
                            result[i] = v is long l ? (double)l : v is double or SizeF ? v : null;
                        }

                        return result;
                    }

                    (int offset, ILOpCode op, long operand, int next) = code[pc];
                    int? jump = null;
                    switch (op)
                    {
                        case ILOpCode.Nop:
                        case ILOpCode.Volatile:
                        case ILOpCode.Tail:
                        case ILOpCode.Readonly:
                        case ILOpCode.Constrained:
                        case ILOpCode.Unaligned:
                            break;
                        case >= ILOpCode.Ldc_i4_m1 and <= ILOpCode.Ldc_i4_8:
                            stack.Push((long)((int)op - (int)ILOpCode.Ldc_i4_0));
                            break;
                        case ILOpCode.Ldc_i4_s:
                        case ILOpCode.Ldc_i4:
                        case ILOpCode.Ldc_i8:
                            stack.Push(operand);
                            break;
                        case ILOpCode.Ldc_r4:
                            stack.Push((double)BitConverter.Int32BitsToSingle((int)operand));
                            break;
                        case ILOpCode.Ldc_r8:
                            stack.Push(BitConverter.Int64BitsToDouble(operand));
                            break;
                        case ILOpCode.Ldnull:
                        case ILOpCode.Ldstr:
                        case ILOpCode.Ldarg_0:
                        case ILOpCode.Ldarg_1:
                        case ILOpCode.Ldarg_2:
                        case ILOpCode.Ldarg_3:
                        case ILOpCode.Ldarg_s:
                        case ILOpCode.Ldarg:
                        case ILOpCode.Ldarga_s:
                        case ILOpCode.Ldarga:
                        case ILOpCode.Ldsfld:
                        case ILOpCode.Ldsflda:
                        case ILOpCode.Ldftn:
                        case ILOpCode.Ldtoken:
                        case ILOpCode.Sizeof:
                            stack.Push(op == ILOpCode.Ldnull ? null : Unknown.Value);
                            break;
                        case >= ILOpCode.Ldloc_0 and <= ILOpCode.Ldloc_3:
                            stack.Push(locals.GetValueOrDefault((int)op - (int)ILOpCode.Ldloc_0, Unknown.Value));
                            break;
                        case ILOpCode.Ldloc_s:
                        case ILOpCode.Ldloc:
                            stack.Push(locals.GetValueOrDefault((int)operand, Unknown.Value));
                            break;
                        case >= ILOpCode.Stloc_0 and <= ILOpCode.Stloc_3:
                            locals[(int)op - (int)ILOpCode.Stloc_0] = Pop();
                            break;
                        case ILOpCode.Stloc_s:
                        case ILOpCode.Stloc:
                            locals[(int)operand] = Pop();
                            break;
                        case ILOpCode.Ldloca_s:
                        case ILOpCode.Ldloca:
                            // Its address escapes (a struct initialised or called through it).
                            locals[(int)operand] = Unknown.Value;
                            stack.Push(Unknown.Value);
                            break;
                        case ILOpCode.Starg_s:
                        case ILOpCode.Starg:
                        case ILOpCode.Pop:
                        case ILOpCode.Stsfld:
                        case ILOpCode.Initobj:
                            Pop();
                            break;
                        case ILOpCode.Stfld:
                        case ILOpCode.Stobj:
                        case >= ILOpCode.Stind_ref and <= ILOpCode.Stind_r8:
                        case ILOpCode.Stind_i:
                            Pop();
                            Pop();
                            break;
                        case >= ILOpCode.Stelem_i and <= ILOpCode.Stelem_ref:
                        case ILOpCode.Stelem:
                            Pop();
                            Pop();
                            Pop();
                            break;
                        case ILOpCode.Dup:
                            {
                                object? top = Pop();
                                stack.Push(top);
                                stack.Push(top);
                                break;
                            }

                        case ILOpCode.Ldfld:
                        case ILOpCode.Ldflda:
                        case ILOpCode.Ldobj:
                        case >= ILOpCode.Ldind_i1 and <= ILOpCode.Ldind_ref:
                        case ILOpCode.Castclass:
                        case ILOpCode.Isinst:
                        case ILOpCode.Box:
                        case ILOpCode.Unbox:
                        case ILOpCode.Unbox_any:
                        case ILOpCode.Newarr:
                        case ILOpCode.Ldlen:
                        case ILOpCode.Ldvirtftn:
                            {
                                object? v = Pop();
                                // A boxed or unboxed number keeps its value; anything else is opaque.
                                stack.Push(op is ILOpCode.Box or ILOpCode.Unbox_any && v is double or long ? v : Unknown.Value);
                                break;
                            }

                        case >= ILOpCode.Ldelem_i1 and <= ILOpCode.Ldelem_ref:
                        case ILOpCode.Ldelem:
                        case ILOpCode.Ldelema:
                            Pop();
                            Pop();
                            stack.Push(Unknown.Value);
                            break;
                        case ILOpCode.Neg:
                            {
                                object? v0 = Pop();
                                double? a = Num(v0);
                                stack.Push(a is { } x ? (v0 is long ? -(long)x : -x) : Unknown.Value);
                                break;
                            }

                        case ILOpCode.Add:
                        case ILOpCode.Sub:
                        case ILOpCode.Mul:
                        case ILOpCode.Div:
                        case ILOpCode.Div_un:
                        case ILOpCode.Rem:
                        case ILOpCode.Ceq:
                        case ILOpCode.Cgt:
                        case ILOpCode.Cgt_un:
                        case ILOpCode.Clt:
                        case ILOpCode.Clt_un:
                        case ILOpCode.And:
                        case ILOpCode.Or:
                        case ILOpCode.Xor:
                        case ILOpCode.Shl:
                        case ILOpCode.Shr:
                        case ILOpCode.Shr_un:
                            {
                                object? rb = Pop(), ra = Pop();
                                double? b = Num(rb), a = Num(ra);
                                object? r = Unknown.Value;
                                if (a is { } x && b is { } y)
                                {
                                    r = op switch
                                    {
                                        ILOpCode.Add => x + y,
                                        ILOpCode.Sub => x - y,
                                        ILOpCode.Mul => x * y,
                                        ILOpCode.Div or ILOpCode.Div_un when y != 0 => x / y,
                                        ILOpCode.Rem when y != 0 => x % y,
                                        ILOpCode.Ceq => x == y ? 1.0 : 0.0,
                                        ILOpCode.Cgt or ILOpCode.Cgt_un => x > y ? 1.0 : 0.0,
                                        ILOpCode.Clt or ILOpCode.Clt_un => x < y ? 1.0 : 0.0,
                                        ILOpCode.And => (double)((long)x & (long)y),
                                        ILOpCode.Or => (double)((long)x | (long)y),
                                        ILOpCode.Xor => (double)((long)x ^ (long)y),
                                        ILOpCode.Shl => (double)((long)x << (int)y),
                                        ILOpCode.Shr or ILOpCode.Shr_un => (double)((long)x >> (int)y),
                                        _ => Unknown.Value,
                                    };
                                }

                                // Two integers make an integer (a division truncates), as on the CLR's stack;
                                // a comparison is an int32 0 or 1.
                                bool integer = ra is long && rb is long || op is ILOpCode.Ceq or ILOpCode.Cgt or ILOpCode.Cgt_un or ILOpCode.Clt or ILOpCode.Clt_un
                                    or ILOpCode.And or ILOpCode.Or or ILOpCode.Xor or ILOpCode.Shl or ILOpCode.Shr or ILOpCode.Shr_un;
                                stack.Push(r is double whole && integer ? (long)Math.Truncate(whole) : r);
                                break;
                            }

                        case ILOpCode.Conv_i1:
                        case ILOpCode.Conv_i2:
                        case ILOpCode.Conv_i4:
                        case ILOpCode.Conv_i8:
                        case ILOpCode.Conv_i:
                        case ILOpCode.Conv_u1:
                        case ILOpCode.Conv_u2:
                        case ILOpCode.Conv_u4:
                        case ILOpCode.Conv_u8:
                        case ILOpCode.Conv_u:
                        case >= ILOpCode.Conv_ovf_i1_un and <= ILOpCode.Conv_ovf_u_un:
                        case >= ILOpCode.Conv_ovf_i1 and <= ILOpCode.Conv_ovf_u8:
                        case ILOpCode.Conv_ovf_i:
                        case ILOpCode.Conv_ovf_u:
                            {
                                double? a = Num(Pop());
                                stack.Push(a is { } x ? (long)Math.Truncate(x) : Unknown.Value);
                                break;
                            }

                        case ILOpCode.Conv_r4:
                            {
                                double? a = Num(Pop());
                                stack.Push(a is { } x ? (double)(float)x : Unknown.Value);
                                break;
                            }

                        case ILOpCode.Conv_r8:
                        case ILOpCode.Conv_r_un:
                            {
                                double? a = Num(Pop());
                                stack.Push(a is { } x ? x : Unknown.Value);
                                break;
                            }

                        case ILOpCode.Br:
                        case ILOpCode.Br_s:
                        case ILOpCode.Leave:
                        case ILOpCode.Leave_s:
                            if (op is ILOpCode.Leave or ILOpCode.Leave_s)
                            {
                                stack.Clear();
                            }

                            jump = next + (int)operand;
                            break;
                        case ILOpCode.Brfalse:
                        case ILOpCode.Brfalse_s:
                        case ILOpCode.Brtrue:
                        case ILOpCode.Brtrue_s:
                            {
                                object? v = Pop();
                                bool? truth = Num(v) is { } d ? d != 0 : v is null ? false : null;
                                bool wantsTrue = op is ILOpCode.Brtrue or ILOpCode.Brtrue_s;
                                if (truth == wantsTrue)
                                {
                                    jump = next + (int)operand;
                                }

                                break;
                            }

                        case >= ILOpCode.Beq_s and <= ILOpCode.Blt_un_s:
                        case >= ILOpCode.Beq and <= ILOpCode.Blt_un:
                            {
                                double? b = Num(Pop()), a = Num(Pop());
                                ILOpCode longForm = op <= ILOpCode.Blt_un_s ? op + (ILOpCode.Beq - ILOpCode.Beq_s) : op;
                                if (a is { } x && b is { } y)
                                {
                                    bool taken = longForm switch
                                    {
                                        ILOpCode.Beq => x == y,
                                        ILOpCode.Bne_un => x != y,
                                        ILOpCode.Bge or ILOpCode.Bge_un => x >= y,
                                        ILOpCode.Bgt or ILOpCode.Bgt_un => x > y,
                                        ILOpCode.Ble or ILOpCode.Ble_un => x <= y,
                                        _ => x < y,
                                    };
                                    if (taken)
                                    {
                                        jump = next + (int)operand;
                                    }
                                }

                                break;
                            }

                        case ILOpCode.Switch:
                            Pop(); // an unknown case: falls through past the table
                            break;
                        case ILOpCode.Call:
                        case ILOpCode.Callvirt:
                        case ILOpCode.Newobj:
                            if (!Call(op, (int)operand, stack))
                            {
                                return none;
                            }

                            break;
                        default:
                            // ret, throw, endfinally, calli, localloc...: the path does not reach the call.
                            return none;
                    }

                    if (jump is { } target)
                    {
                        if (!byOffset.TryGetValue(target, out int index))
                        {
                            return none;
                        }

                        pc = index;
                    }
                    else
                    {
                        pc++;
                    }
                }

                return none;
            }

            /// <summary>A call during <see cref="EvaluateArguments"/>: pops its arguments and pushes what is known of its result.</summary>
            private bool Call(ILOpCode op, int token, Stack<object?> stack)
            {
                EntityHandle target = MetadataTokens.EntityHandle(token);
                if (target.Kind == HandleKind.MethodSpecification)
                {
                    target = _md.GetMethodSpecification((MethodSpecificationHandle)target).Method;
                }

                string? owner;
                string name;
                BlobHandle signatureHandle;
                switch (target.Kind)
                {
                    case HandleKind.MemberReference:
                        MemberReference member = _md.GetMemberReference((MemberReferenceHandle)target);
                        owner = member.Parent.Kind is HandleKind.TypeReference or HandleKind.TypeDefinition ? TypeName(member.Parent) : null;
                        name = _md.GetString(member.Name);
                        signatureHandle = member.Signature;
                        break;
                    case HandleKind.MethodDefinition:
                        MethodDefinition definition = _md.GetMethodDefinition((MethodDefinitionHandle)target);
                        owner = TypeName(definition.GetDeclaringType());
                        name = _md.GetString(definition.Name);
                        signatureHandle = definition.Signature;
                        break;
                    default:
                        return false;
                }

                BlobReader signature = _md.GetBlobReader(signatureHandle);
                SignatureHeader header = signature.ReadSignatureHeader();
                if (header.IsGeneric)
                {
                    signature.ReadCompressedInteger();
                }

                int count = signature.ReadCompressedInteger();
                bool returnsVoid = signature.RemainingBytes > 0 && signature.ReadByte() == (byte)SignatureTypeCode.Void;

                object?[] args = new object?[count];
                for (int i = count - 1; i >= 0; i--)
                {
                    args[i] = stack.Count > 0 ? stack.Pop() : Unknown.Value;
                }

                if (header.IsInstance && op != ILOpCode.Newobj && stack.Count > 0)
                {
                    stack.Pop();
                }

                double? Arg(int i) => i < args.Length ? args[i] switch { double d => d, long l => l, _ => null } : null;
                object? result = Unknown.Value;
                switch (owner, name)
                {
                    case ("System.Windows.Interop.Content", "get_ActualWidth"):
                        result = (double)Wp8Display.LogicalWidth;
                        break;
                    case ("System.Windows.Interop.Content", "get_ActualHeight"):
                        result = (double)Wp8Display.LogicalHeight;
                        break;
                    case ("System.Windows.Interop.Content", "get_ScaleFactor"):
                        result = (long)Wp8Display.Scale;
                        break;
                    case ("Microsoft.Phone.Shell.SystemTray", "get_IsVisible"):
                        result = SystemTrayVisible ? 1L : 0L;
                        break;
                    case ("System.Math", "Floor") when Arg(0) is { } f:
                        result = Math.Floor(f);
                        break;
                    case ("System.Math", "Ceiling") when Arg(0) is { } c:
                        result = Math.Ceiling(c);
                        break;
                    case ("System.Math", "Round") when Arg(0) is { } r && count == 1:
                        result = Math.Round(r);
                        break;
                    case ("System.Math", "Abs") when Arg(0) is { } a:
                        result = Math.Abs(a);
                        break;
                    case ("System.Math", "Min") when Arg(0) is { } x && Arg(1) is { } y:
                        result = Math.Min(x, y);
                        break;
                    case ("System.Math", "Max") when Arg(0) is { } x && Arg(1) is { } y:
                        result = Math.Max(x, y);
                        break;
                    case ("System.Windows.Size" or "Windows.Foundation.Size", ".ctor") when Arg(0) is { } w && Arg(1) is { } h:
                        result = new SizeF((float)w, (float)h);
                        break;
                }

                if (op == ILOpCode.Newobj || !returnsVoid)
                {
                    stack.Push(result);
                }

                return true;
            }
        }
    }
}
