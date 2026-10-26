using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace WPR.Wp8Native
{
    /// <summary>
    /// A page that feeds touch to its component from Silverlight's <c>Touch.FrameReported</c>, not
    /// through the grid's manipulation handler: which component method each touch action calls.
    /// </summary>
    /// <remarks>
    /// Gameloft's hybrid titles (Asphalt 7: Heat) do not implement
    /// <c>IDrawingSurfaceManipulationHandler</c> at all. Their page subscribes
    /// <c>Touch.FrameReported</c> and, for every touch point, calls <c>AddPoint(id, x, y)</c> on
    /// <c>TouchAction.Down</c>, <c>UpdatePoint</c> on Move and <c>RemovePoint</c> on Up. Without this
    /// the component never heard a touch and the title screen's "touch the screen to continue" sat
    /// there for ever.
    /// </remarks>
    /// <param name="Down">The method called for <c>TouchAction.Down</c> (1); null when none.</param>
    /// <param name="Move">The method called for <c>TouchAction.Move</c> (2); null when none.</param>
    /// <param name="Up">The method called for <c>TouchAction.Up</c> (3); null when none.</param>
    /// <param name="RelativeToRoot">
    /// The handler asks <c>GetTouchPoints(null)</c>: positions relative to the root frame, which on
    /// the phone never turns, so they are portrait DIPs whatever the page's orientation. False: they
    /// are relative to the page (or an element on it), so landscape on a landscape page.
    /// </param>
    public sealed record XamlTouchFrame(string? Down, string? Move, string? Up, bool RelativeToRoot)
    {
        /// <summary>Which method a touch action calls.</summary>
        public string? MethodFor(int action) => action switch
        {
            1 => Down,
            2 => Move,
            3 => Up,
            _ => null,
        };
    }

    /// <summary>
    /// Finds a <see cref="XamlTouchFrame"/> in a Direct3D/XAML title's managed shell, by reading its IL
    /// with System.Reflection.Metadata (nothing is loaded or run).
    /// </summary>
    /// <remarks>Nothing in this file may name an XNA or engine type: the probe compiles it.</remarks>
    public static class XamlTouchFrames
    {
        /// <summary>
        /// Reads the managed assemblies in <paramref name="directory"/> for a <c>Touch.FrameReported</c>
        /// handler that calls methods of <paramref name="classId"/>. Null when there is none.
        /// </summary>
        public static XamlTouchFrame? Infer(string directory, string classId, Action<string>? log = null)
        {
            foreach (string path in Directory.EnumerateFiles(directory, "*.dll"))
            {
                try
                {
                    using FileStream stream = File.OpenRead(path);
                    using PEReader pe = new(stream);
                    if (!pe.HasMetadata)
                    {
                        continue;
                    }

                    MetadataReader md = pe.GetMetadataReader();
                    XamlTouchFrame? found = Read(pe, md, classId);
                    if (found is not null)
                    {
                        log?.Invoke($"xaml inference: {Path.GetFileName(path)} feeds touch through Touch.FrameReported: " +
                            $"down={found.Down ?? "-"} move={found.Move ?? "-"} up={found.Up ?? "-"} " +
                            $"({(found.RelativeToRoot ? "relative to the root, portrait" : "relative to the page")})");
                        return found;
                    }
                }
                catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException or IOException)
                {
                    // Not a managed assembly, or one this reader cannot walk: not the shell.
                }
            }

            return null;
        }

        private static XamlTouchFrame? Read(PEReader pe, MetadataReader md, string classId)
        {
            foreach (MethodDefinitionHandle handle in md.MethodDefinitions)
            {
                MethodDefinition method = md.GetMethodDefinition(handle);
                if (method.RelativeVirtualAddress == 0)
                {
                    continue;
                }

                List<Instruction> code = Decode(pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes() ?? []);
                for (int i = 0; i < code.Count; i++)
                {
                    if (!IsCall(code[i]) || MemberName(md, code[i].Token, out string owner) != "add_FrameReported" ||
                        owner != "System.Windows.Input.Touch")
                    {
                        continue;
                    }

                    // newobj TouchFrameEventHandler(object, ldftn handler) just before the add_.
                    for (int back = i - 1; back >= Math.Max(0, i - 4); back--)
                    {
                        if (code[back].Op == ILOpCode.Ldftn &&
                            MetadataTokens.EntityHandle(code[back].Token) is { Kind: HandleKind.MethodDefinition } target &&
                            ReadHandler(pe, md, (MethodDefinitionHandle)target, classId) is { } frame)
                        {
                            return frame;
                        }
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// The handler's calls on the component, each under the <c>TouchPoint.Action</c> value the
        /// handler compared against last (<c>get_Action; ldc.i4 N; bne</c>).
        /// </summary>
        private static XamlTouchFrame? ReadHandler(PEReader pe, MetadataReader md, MethodDefinitionHandle handle, string classId)
        {
            MethodDefinition method = md.GetMethodDefinition(handle);
            if (method.RelativeVirtualAddress == 0)
            {
                return null;
            }

            List<Instruction> code = Decode(pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes() ?? []);
            string?[] byAction = new string?[4];
            List<string> unplaced = [];
            int action = 0;
            bool awaitingAction = false;
            bool relativeToRoot = false;
            for (int i = 0; i < code.Count; i++)
            {
                Instruction instruction = code[i];
                if (awaitingAction && TryConstant(instruction, out int constant))
                {
                    action = constant is >= 1 and <= 3 ? constant : 0;
                    awaitingAction = false;
                    continue;
                }

                if (!IsCall(instruction))
                {
                    continue;
                }

                string name = MemberName(md, instruction.Token, out string owner);
                if (name == "get_Action")
                {
                    awaitingAction = true;
                }
                else if (name is "GetTouchPoints" or "GetPrimaryTouchPoint")
                {
                    relativeToRoot = i > 0 && code[i - 1].Op == ILOpCode.Ldnull;
                }
                else if (owner == classId)
                {
                    if (action != 0)
                    {
                        byAction[action] ??= name;
                    }
                    else
                    {
                        unplaced.Add(name);
                    }
                }
            }

            // No Action comparison read: place the calls by name, then in order (down, move, up).
            foreach (string name in unplaced)
            {
                int guess = name.Contains("Add", StringComparison.OrdinalIgnoreCase) || name.Contains("Down", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("Press", StringComparison.OrdinalIgnoreCase) ? 1
                    : name.Contains("Update", StringComparison.OrdinalIgnoreCase) || name.Contains("Move", StringComparison.OrdinalIgnoreCase) ? 2
                    : name.Contains("Remove", StringComparison.OrdinalIgnoreCase) || name.Contains("Up", StringComparison.Ordinal) ||
                      name.Contains("Release", StringComparison.OrdinalIgnoreCase) ? 3
                    : Array.FindIndex(byAction, 1, a => a is null);
                if (guess is >= 1 and <= 3)
                {
                    byAction[guess] ??= name;
                }
            }

            return byAction[1] is null && byAction[2] is null && byAction[3] is null
                ? null
                : new XamlTouchFrame(byAction[1], byAction[2], byAction[3], relativeToRoot);
        }

        private readonly record struct Instruction(ILOpCode Op, int Token, long Constant);

        private static bool IsCall(Instruction instruction) => instruction.Op is ILOpCode.Call or ILOpCode.Callvirt;

        private static bool TryConstant(Instruction instruction, out int value)
        {
            switch (instruction.Op)
            {
                case >= ILOpCode.Ldc_i4_m1 and <= ILOpCode.Ldc_i4_8:
                    value = (int)instruction.Op - (int)ILOpCode.Ldc_i4_0;
                    return true;
                case ILOpCode.Ldc_i4_s:
                case ILOpCode.Ldc_i4:
                    value = (int)instruction.Constant;
                    return true;
                default:
                    value = 0;
                    return false;
            }
        }

        /// <summary>The name of a called member, and its declaring type's full name in <paramref name="owner"/>.</summary>
        private static string MemberName(MetadataReader md, int token, out string owner)
        {
            owner = string.Empty;
            EntityHandle handle = MetadataTokens.EntityHandle(token);
            switch (handle.Kind)
            {
                case HandleKind.MemberReference:
                    MemberReference reference = md.GetMemberReference((MemberReferenceHandle)handle);
                    owner = TypeName(md, reference.Parent);
                    return md.GetString(reference.Name);
                case HandleKind.MethodDefinition:
                    MethodDefinition definition = md.GetMethodDefinition((MethodDefinitionHandle)handle);
                    owner = TypeName(md, definition.GetDeclaringType());
                    return md.GetString(definition.Name);
                default:
                    return string.Empty;
            }
        }

        private static string TypeName(MetadataReader md, EntityHandle handle)
        {
            switch (handle.Kind)
            {
                case HandleKind.TypeReference:
                    TypeReference reference = md.GetTypeReference((TypeReferenceHandle)handle);
                    return Join(md.GetString(reference.Namespace), md.GetString(reference.Name));
                case HandleKind.TypeDefinition:
                    TypeDefinition definition = md.GetTypeDefinition((TypeDefinitionHandle)handle);
                    return Join(md.GetString(definition.Namespace), md.GetString(definition.Name));
                default:
                    return string.Empty;
            }
        }

        private static string Join(string ns, string name) => ns.Length == 0 ? name : ns + "." + name;

        /// <summary>Decodes a method body's instructions (opcode, token or constant).</summary>
        private static List<Instruction> Decode(byte[] il)
        {
            List<Instruction> code = [];
            int at = 0;
            while (at < il.Length)
            {
                int value = il[at++];
                if (value == 0xFE && at < il.Length)
                {
                    value = 0xFE00 | il[at++];
                }

                ILOpCode op = (ILOpCode)value;
                int size = OperandSize(op, il, at);
                long operand = size switch
                {
                    1 when at < il.Length => op == ILOpCode.Ldc_i4_s ? (sbyte)il[at] : il[at],
                    2 when at + 2 <= il.Length => BitConverter.ToUInt16(il, at),
                    4 when at + 4 <= il.Length => BitConverter.ToInt32(il, at),
                    8 when at + 8 <= il.Length => BitConverter.ToInt64(il, at),
                    _ => 0,
                };
                code.Add(new Instruction(op, size == 4 ? (int)operand : 0, operand));
                at += size;
            }

            return code;
        }

        private static int OperandSize(ILOpCode op, byte[] il, int at)
        {
            switch (op)
            {
                case ILOpCode.Switch:
                    return at + 4 <= il.Length ? 4 + (4 * BitConverter.ToInt32(il, at)) : 4;
                case >= ILOpCode.Br_s and <= ILOpCode.Blt_un_s:
                case ILOpCode.Leave_s:
                case ILOpCode.Ldc_i4_s:
                case ILOpCode.Ldarg_s:
                case ILOpCode.Ldarga_s:
                case ILOpCode.Starg_s:
                case ILOpCode.Ldloc_s:
                case ILOpCode.Ldloca_s:
                case ILOpCode.Stloc_s:
                case ILOpCode.Unaligned:
                    return 1;
                case ILOpCode.Ldarg:
                case ILOpCode.Ldarga:
                case ILOpCode.Starg:
                case ILOpCode.Ldloc:
                case ILOpCode.Ldloca:
                case ILOpCode.Stloc:
                    return 2;
                case ILOpCode.Ldc_i8:
                case ILOpCode.Ldc_r8:
                    return 8;
                case ILOpCode.Ldc_r4:
                case ILOpCode.Ldc_i4:
                case ILOpCode.Jmp:
                case ILOpCode.Call:
                case ILOpCode.Calli:
                case >= ILOpCode.Br and <= ILOpCode.Blt_un:
                case ILOpCode.Callvirt:
                case ILOpCode.Cpobj:
                case ILOpCode.Ldobj:
                case ILOpCode.Ldstr:
                case ILOpCode.Newobj:
                case ILOpCode.Castclass:
                case ILOpCode.Isinst:
                case ILOpCode.Unbox:
                case ILOpCode.Ldfld:
                case ILOpCode.Ldflda:
                case ILOpCode.Stfld:
                case ILOpCode.Ldsfld:
                case ILOpCode.Ldsflda:
                case ILOpCode.Stsfld:
                case ILOpCode.Stobj:
                case ILOpCode.Box:
                case ILOpCode.Newarr:
                case ILOpCode.Ldelema:
                case ILOpCode.Ldelem:
                case ILOpCode.Stelem:
                case ILOpCode.Unbox_any:
                case ILOpCode.Refanyval:
                case ILOpCode.Mkrefany:
                case ILOpCode.Ldtoken:
                case ILOpCode.Leave:
                case ILOpCode.Ldftn:
                case ILOpCode.Ldvirtftn:
                case ILOpCode.Initobj:
                case ILOpCode.Constrained:
                case ILOpCode.Sizeof:
                    return 4;
                default:
                    return 0;
            }
        }
    }

    public sealed partial class WinRtRuntime
    {
        /// <summary>
        /// Hands this turn's pointer events to the methods the page's <c>Touch.FrameReported</c>
        /// handler calls (<see cref="XamlShell.TouchFrame"/>), one after another, then
        /// <paramref name="continueWith"/>. False when there was nothing to deliver.
        /// </summary>
        private bool DeliverTouchFrame(Action continueWith)
        {
            List<PointerStep> steps = [];
            while (_external.TryDequeue(out PointerStep step))
            {
                steps.Add(step);
            }

            if (steps.Count == 0)
            {
                return false;
            }

            DeliverTouchSteps(steps, 0, continueWith);
            return true;
        }

        private void DeliverTouchSteps(List<PointerStep> steps, int index, Action done)
        {
            XamlTouchFrame frame = _shell!.TouchFrame!;
            for (; index < steps.Count; index++)
            {
                PointerStep step = steps[index];
                int action = step.Slot switch
                {
                    SlotAddPointerPressed => 1,
                    SlotAddPointerReleased => 3,
                    _ => 2,
                };

                _pointerX = step.X;
                _pointerY = step.Y;
                _pointerInContact = action != 3;
                _pointerId = step.Id;
                _pointerSlot = step.Slot;
                TrackPrimary(step.Slot, step.Id);

                string? method = frame.MethodFor(action);
                if (method is null)
                {
                    continue;
                }

                (float x, float y) = TouchFramePosition(frame, step.X, step.Y);
                object?[] arguments = TouchFrameArguments(method, step.Id, x, y);
                if (action == 2)
                {
                    _movesDelivered++;
                }
                else
                {
                    InputDelivered.Add($"touch frame: {method}({string.Join(", ", arguments)}) from ({step.X:0}, {step.Y:0})");
                }

                int next = index + 1;
                InvokeMember(method, arguments, _ => DeliverTouchSteps(steps, next, done));
                return;
            }

            done();
        }

        /// <summary>
        /// Where a host point (landscape composition pixels) is as the handler reads it: portrait DIPs
        /// relative to the root frame, or DIPs relative to a page that may itself be landscape.
        /// </summary>
        private (float X, float Y) TouchFramePosition(XamlTouchFrame frame, float x, float y)
        {
            if (frame.RelativeToRoot || !_shell!.Landscape)
            {
                return ToWindow(x, y);
            }

            return (x * WindowHeight / Math.Max(1, FrameCapture.Width), y * WindowWidth / Math.Max(1, FrameCapture.Height));
        }

        /// <summary>The method's arguments, typed by its metadata: (id, x, y), or (x, y) with two parameters.</summary>
        private object?[] TouchFrameArguments(string method, uint id, float x, float y)
        {
            IReadOnlyList<string> types = _metadata?.FindMethod(_shell!.ClassId, method)?.Method.ParameterTypes ?? ["Int32", "Int32", "Int32"];
            float[] values = types.Count >= 3 ? [id, x, y] : [x, y];
            object?[] arguments = new object?[types.Count];
            for (int i = 0; i < types.Count; i++)
            {
                float value = i < values.Length ? values[i] : 0f;
                arguments[i] = types[i] == "Single" ? (object)value : (int)value;
            }

            return arguments;
        }
    }
}
