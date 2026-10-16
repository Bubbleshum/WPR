using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace WPR.Wp8Native
{
    /// <summary>
    /// Writes a <see cref="XamlShell"/> for a Direct3D/XAML title WPR has no hand-written description
    /// of, by reading what its managed page does with the component - so an unknown title of this
    /// kind attempts to run instead of stopping at frame 0 with "no page description".
    /// </summary>
    /// <remarks>
    /// <para>The managed half of these titles follows the SDK's Direct3D-with-XAML template: the app
    /// (or the page) constructs the component class or asks a static for it, subscribes some of its
    /// events, and the <c>DrawingSurfaceBackgroundGrid</c>'s Loaded handler sets the sizes, calls
    /// <c>CreateContentProvider</c> and hands the class to <c>SetBackgroundManipulationHandler</c>.
    /// The shell's IL is read with System.Reflection.Metadata (nothing is loaded or run) and every
    /// call it makes on the component class is collected, in order, from the methods the shell's
    /// lifecycle reaches: the App's constructor and <c>Application_Launching</c> and the page
    /// constructors (<see cref="XamlShell.Launch"/>); the page's <c>*Loaded</c> handlers
    /// (<see cref="XamlShell.Loaded"/>); <c>Application_Deactivated</c>/<c>OnNavigatedFrom</c> and
    /// <c>Application_Activated</c>; the back-key handler; <c>Application_Closing</c>. Calls into the
    /// shell's own methods are followed a few levels deep (Alex's App calls <c>MainPage.Launching</c>,
    /// which calls <c>FusionMain.Launching</c>).</para>
    /// <para>Argument values: sizes and the scale factor are what the template computes on this
    /// display (<see cref="Wp8Display"/>); a constant the IL loads right before the call (a
    /// <c>true</c>, a <c>1</c>, a string literal) is passed as written; an event subscription gets a
    /// no-op handler; an argument of one of the component's own interfaces gets a host object that
    /// answers defaults; anything else gets the type's default. That is less than a hand-written
    /// description knows - which branch a handler takes, what a C# callback answers - so the
    /// hand-written ones in <see cref="XamlShells"/> still win; this is what runs when there is none.</para>
    /// <para>Nothing in this file may name an XNA or engine type: the probe compiles it.</para>
    /// </remarks>
    public static class XamlShellInference
    {
        private const string CreateContentProvider = "CreateContentProvider";
        private const string SetManipulationHost = "SetManipulationHost";
        private const int MaxDepth = 4;

        /// <summary>A call the shell makes on a component class, as read from IL.</summary>
        private sealed record ShellCall(string Class, string Method, bool IsNew, bool IsStatic, bool IsFunctionPointer, int Parameters, object?[] TrailingConstants, string? NextCall);

        /// <summary>
        /// Infers a shell for <paramref name="componentDll"/> from the managed assemblies beside it.
        /// Null when the component's metadata declares no class with <c>CreateContentProvider</c>.
        /// </summary>
        public static XamlShell? Infer(string componentDll, WinmdReader metadata, Action<string>? log = null)
        {
            string directory = Path.GetDirectoryName(Path.GetFullPath(componentDll)) ?? ".";
            List<string> candidates = metadata.ClassNames.Where(c => metadata.FindMethod(c, CreateContentProvider) != null).ToList();
            if (candidates.Count == 0)
            {
                log?.Invoke($"xaml inference: no class in the package metadata declares {CreateContentProvider}");
                return null;
            }

            // What a call's owner means: a runtime class by its own name, or - for a page that talks
            // to the component through one of its interfaces (EA's shared page holds an
            // IEAD3DBackground made with Activator.CreateInstance) - the drawing class implementing it.
            Dictionary<string, string> owners = new(StringComparer.Ordinal);
            foreach (string name in metadata.ClassNames)
            {
                owners[name] = name;
            }

            foreach (string candidate in candidates)
            {
                foreach (WinmdReader.InterfaceLayout layout in metadata.InterfacesOf(candidate))
                {
                    if (!layout.FullName.StartsWith("Windows.", StringComparison.Ordinal))
                    {
                        owners.TryAdd(layout.FullName, candidate);
                    }
                }
            }

            ShellReading? reading = null;
            foreach (string assembly in ManagedAssemblies(directory))
            {
                try
                {
                    ShellReading candidate = ShellReading.Read(assembly, owners, candidates.ToHashSet(StringComparer.Ordinal));
                    if (candidate.Mentions.Count > 0 && (reading is null || candidate.Mentions.Count > reading.Mentions.Count))
                    {
                        reading = candidate;
                    }
                }
                catch (Exception ex)
                {
                    log?.Invoke($"xaml inference: {Path.GetFileName(assembly)} unreadable ({ex.GetType().Name}: {ex.Message})");
                }
            }

            string classId = reading?.Mentions.GroupBy(c => c).OrderByDescending(g => g.Count()).First().Key
                ?? PreferredCandidate(candidates, componentDll);
            log?.Invoke(reading is null
                ? $"xaml inference: no managed shell names {classId}; the template's steps are assumed"
                : $"xaml inference: {Path.GetFileName(reading.Path)} drives {classId}");

            Builder builder = new(metadata, classId);
            List<XamlShellStep> launch = builder.Steps(reading?.Launch, Phase.Launch);
            List<XamlShellStep> loaded = builder.Steps(reading?.Loaded, Phase.Loaded);
            if (!loaded.Any(s => s.Method == CreateContentProvider) && reading is not null)
            {
                // No Loaded handler reaches the component (some shells do it from OnNavigatedTo).
                loaded.AddRange(builder.Steps(reading.Navigated, Phase.Loaded));
            }

            if (!launch.Concat(loaded).Any(s => s.Method == CreateContentProvider))
            {
                // The template's sequence. Members the class does not have are skipped at run time.
                loaded.AddRange(
                [
                    new("set_WindowBounds", new SizeF(Wp8Display.LogicalWidth, Wp8Display.LogicalHeight)),
                    new("set_NativeResolution", new SizeF(Wp8Display.PhysicalWidth, Wp8Display.PhysicalHeight)),
                    new("set_RenderResolution", new SizeF(Wp8Display.PhysicalWidth, Wp8Display.PhysicalHeight)),
                    new(CreateContentProvider),
                ]);
            }

            // The grid calls SetManipulationHost when the page hands it the class
            // (SetBackgroundManipulationHandler), so it is never in the shell's own IL.
            if (!loaded.Concat(launch).Any(s => s.Method == SetManipulationHost))
            {
                List<XamlShellStep> list = loaded.Any(s => s.Method == CreateContentProvider) ? loaded : launch;
                int at = list.FindIndex(s => s.Method == CreateContentProvider);
                list.Insert(at + 1, new XamlShellStep(SetManipulationHost, XamlShell.ManipulationHost));
            }

            // A page with two surfaces (GTA: San Andreas' Loaded handlers) asks twice; one provider.
            foreach (string once in new[] { CreateContentProvider, SetManipulationHost })
            {
                bool seen = false;
                foreach (List<XamlShellStep> list in new[] { launch, loaded })
                {
                    list.RemoveAll(s => s.Method == once && (seen || !(seen = true)));
                }
            }

            (string? instanceFrom, object?[] instanceArguments) = builder.Instance(reading?.All ?? []);
            XamlShell shell = new(Path.GetFileName(componentDll), classId, launch, loaded)
            {
                InstanceFrom = instanceFrom,
                InstanceArguments = instanceArguments,
                BackKey = builder.BackKey(reading?.BackKey, reading is null ? null : builder.Steps(reading.Closing, Phase.Lifecycle)),
                Deactivated = builder.Steps(reading?.Deactivated, Phase.Lifecycle),
                Activated = builder.Steps(reading?.Activated, Phase.Lifecycle),
                Inferred = true,
                DrawingSurface = reading is { UsesDrawingSurface: true, UsesBackgroundGrid: false },
            };

            log?.Invoke($"xaml inference: {Describe(shell)}");
            return shell;
        }

        /// <summary>One line naming every step, for the log.</summary>
        public static string Describe(XamlShell shell)
        {
            static string Steps(IEnumerable<XamlShellStep> steps) => string.Join(", ", steps.Select(s => s.Method));
            string instance = shell.InstanceFrom is { } from ? $"{from}()" : "ActivateInstance";
            return $"{shell.ClassId} via {instance}{(shell.DrawingSurface ? " on a DrawingSurface" : "")}; launch [{Steps(shell.Launch)}]; loaded [{Steps(shell.Loaded)}]; " +
                   $"deactivated [{Steps(shell.Deactivated)}]; activated [{Steps(shell.Activated)}]; " +
                   $"back {(shell.BackKey is { } back ? $"{back.QuitIf} (quit when {back.QuitWhen})" : "closes")}";
        }

        private static string PreferredCandidate(List<string> candidates, string componentDll)
        {
            string stem = Path.GetFileNameWithoutExtension(componentDll);
            return candidates.FirstOrDefault(c => c.StartsWith(stem + ".", StringComparison.OrdinalIgnoreCase)) ?? candidates[0];
        }

        private static IEnumerable<string> ManagedAssemblies(string directory)
        {
            foreach (string path in Directory.EnumerateFiles(directory, "*.dll"))
            {
                string name = Path.GetFileName(path);
                if (name.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase) || name.StartsWith("System.", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                bool managed;
                try
                {
                    using FileStream stream = File.OpenRead(path);
                    using PEReader pe = new(stream);
                    managed = pe.HasMetadata && pe.GetMetadataReader().IsAssembly;
                }
                catch (Exception)
                {
                    managed = false;
                }

                if (managed)
                {
                    yield return path;
                }
            }
        }

        private enum Phase
        {
            Launch,
            Loaded,
            Lifecycle,
        }

        /// <summary>Turns calls read from IL into steps, with arguments from the component's metadata.</summary>
        private sealed class Builder(WinmdReader metadata, string classId)
        {
            public List<XamlShellStep> Steps(IReadOnlyList<ShellCall>? calls, Phase phase)
            {
                List<XamlShellStep> steps = [];
                foreach (ShellCall call in calls ?? [])
                {
                    if (call.Class != classId)
                    {
                        // Another class the package registers, built through its factory in the page
                        // (Spark2's LuaMobileSDKBridge(new FlurryInterfaceImpl())): the same call here.
                        if (call.IsNew && call.Parameters > 0 && phase != Phase.Lifecycle &&
                            metadata.FindStaticMethod(call.Class, "CreateInstance") is { } other &&
                            other.Method.ParameterTypes.Count == call.Parameters)
                        {
                            steps.Add(new XamlShellStep("CreateInstance", other.Method.ParameterTypes.Select(t => DefaultFor(t, "", null)).ToArray()) { Class = call.Class });
                        }
                        // A static of another class, usually handing the engine a C# callback it
                        // later calls unconditionally (Doodle God Blitz's DeviceWrapper.SetLanguageHandler
                        // and a dozen more: left out, the engine called through null 16 frames in).
                        else if (call.IsStatic && !call.IsFunctionPointer && !call.IsNew &&
                                 metadata.FindStaticMethod(call.Class, call.Method) is { } statics &&
                                 (phase != Phase.Lifecycle || statics.Method.ReturnType == "Void"))
                        {
                            steps.Add(new XamlShellStep(call.Method, Arguments(call, statics.Method)) { Class = call.Class });
                        }

                        continue;
                    }

                    if (call.IsNew || call.Method.StartsWith("remove_", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    bool subscription = call.Method.StartsWith("add_", StringComparison.Ordinal);
                    if (call.IsFunctionPointer && !subscription)
                    {
                        continue;
                    }

                    // Queries change nothing the page relies on; lifecycle phases re-subscribe
                    // nothing either (Alex's Activated re-creates FusionMain only when the instance
                    // was NOT preserved, which never happens here).
                    if (call.Method.StartsWith("get_", StringComparison.Ordinal) || (subscription && phase == Phase.Lifecycle))
                    {
                        continue;
                    }

                    var method = metadata.FindMethod(classId, call.Method) ?? metadata.FindStaticMethod(classId, call.Method);
                    if (method is null && call.Method != SetManipulationHost)
                    {
                        continue;
                    }

                    if (method is { } found && found.Method.ReturnType == classId && call.IsStatic)
                    {
                        continue; // the instance factory (GetInstance), see Instance
                    }

                    if (phase == Phase.Lifecycle && method is { } query && query.Method.ReturnType != "Void")
                    {
                        continue; // a deactivation or back handler's questions, not its actions
                    }

                    object?[] arguments = method is { } m ? Arguments(call, m.Method) : [XamlShell.ManipulationHost];
                    if (subscription && arguments.Length > 0)
                    {
                        arguments[0] = CallbackFor(method is { } e && e.Method.ParameterTypes.Count > 0 ? e.Method.ParameterTypes[0] : ""); // a C# handler
                    }

                    if (steps.Count > 0 && steps[^1].Method == call.Method)
                    {
                        continue;
                    }

                    steps.Add(new XamlShellStep(call.Method, arguments));
                }

                return steps;
            }

            /// <summary>How the page gets its instance: a static that returns the class, a factory constructor, or activation.</summary>
            public (string?, object?[]) Instance(IReadOnlyList<ShellCall> calls)
            {
                foreach (ShellCall call in calls.Where(c => c.Class == classId))
                {
                    if (call.IsNew)
                    {
                        var create = metadata.FindStaticMethod(classId, "CreateInstance");
                        if (call.Parameters > 0 && create is { } factory && factory.Method.ParameterTypes.Count == call.Parameters)
                        {
                            return ("CreateInstance", factory.Method.ParameterTypes.Select(t => DefaultFor(t, "", null)).ToArray());
                        }

                        return (null, []);
                    }

                    if (call.IsStatic && metadata.FindStaticMethod(classId, call.Method) is { } statics && statics.Method.ReturnType == classId)
                    {
                        return (call.Method, statics.Method.ParameterTypes.Select(t => DefaultFor(t, "", null)).ToArray());
                    }
                }

                // Never constructed by the shell: a class with no default constructor is reached
                // through its statics (a singleton's GetInstance), otherwise it is activated.
                foreach (string name in new[] { "GetInstance", "get_Instance", "get_Current" })
                {
                    if (metadata.FindStaticMethod(classId, name) is { } single && single.Method.ReturnType == classId)
                    {
                        return (name, []);
                    }
                }

                return (null, []);
            }

            /// <summary>
            /// The back-key handler: the first boolean the page asks the component decides whether
            /// the press closes the app. Assigned straight to <c>e.Cancel</c> (Modern Combat 4's
            /// <c>OnBackButtonPressed</c>) it closes on false; tested (Alex's <c>IsSafeToQuit</c>)
            /// it closes on true. A void call keeps every press (Spark2's <c>OnBackButtonPressed</c>).
            /// </summary>
            public XamlBackKey? BackKey(IReadOnlyList<ShellCall>? calls, List<XamlShellStep>? closing)
            {
                List<ShellCall> own = (calls ?? []).Where(c => c.Class == classId && !c.IsNew && !c.IsFunctionPointer).ToList();
                // A call whose answer goes straight to e.Cancel is the decision; earlier queries
                // (Modern Combat 4 asks IsInGLLive first) are for overlays this host never opens.
                // Then the first boolean; a void call only when it is named for the key (Spark2's
                // OnBackButtonPressed), since a handler also sets unrelated state on the way.
                string? Returns(ShellCall c) => metadata.FindMethod(classId, c.Method)?.Method.ReturnType;
                IEnumerable<ShellCall> ranked = own.Where(c => c.NextCall == "set_Cancel" && Returns(c) == "Boolean")
                    .Concat(own.Where(c => Returns(c) == "Boolean"))
                    .Concat(own.Where(c => Returns(c) == "Void" && c.Method.Contains("Back", StringComparison.OrdinalIgnoreCase)));
                foreach (ShellCall call in ranked)
                {
                    string returns = Returns(call)!;

                    bool quitWhen = returns == "Void" || call.NextCall != "set_Cancel";
                    List<XamlShellStep> otherwise = Steps(own.SkipWhile(c => c != call).Skip(1).ToList(), Phase.Lifecycle);
                    return new XamlBackKey(call.Method, otherwise, closing ?? [], quitWhen);
                }

                return null;
            }

            /// <summary>
            /// A C# handler as data: a no-op that answers its delegate's default, except that a
            /// question about the device gets the answer a phone would give - an engine that asks
            /// for its language (Doodle God Blitz's GetLanguageHandler) uses the answer at once.
            /// </summary>
            private object CallbackFor(string delegateType)
            {
                if (metadata.DelegateInvoke(delegateType)?.ReturnType != "String")
                {
                    return XamlShell.Callback;
                }

                string? answer = DeviceAnswer(delegateType.Split('.')[^1]);
                return answer is null ? XamlShell.Callback : new XamlCallback(answer);
            }

            /// <summary>What a WP8 phone answers to a device question, by the name it is asked under; null for anything else.</summary>
            private static string? DeviceAnswer(string name)
            {
                bool Has(string word) => name.Contains(word, StringComparison.OrdinalIgnoreCase);
                return Has("Language") || Has("Locale") || Has("Culture") ? "en-US"
                    : Has("Region") || Has("Country") ? "US"
                    : Has("Firmware") || Has("OSVersion") ? "8.0.10521.0"
                    : Has("Manufacturer") ? "NOKIA"
                    : Has("DeviceName") || Has("Model") ? "RM-821_eu_euro1_276"
                    : null;
            }

            private object?[] Arguments(ShellCall call, WinmdReader.MethodLayout method)
            {
                int count = method.ParameterTypes.Count;
                object?[] arguments = new object?[count];
                for (int i = 0; i < count; i++)
                {
                    // Constants are known for the trailing run of arguments the IL loaded literally.
                    int fromEnd = count - 1 - i;
                    object? constant = fromEnd < call.TrailingConstants.Length ? call.TrailingConstants[^(fromEnd + 1)] : null;
                    arguments[i] = DefaultFor(method.ParameterTypes[i], call.Method, constant);
                }

                return arguments;
            }

            private object? DefaultFor(string type, string method, object? constant)
            {
                switch (type)
                {
                    case "Windows.Foundation.Size":
                        return method.Contains("Window", StringComparison.OrdinalIgnoreCase) || method.Contains("Bounds", StringComparison.OrdinalIgnoreCase)
                            ? new SizeF(Wp8Display.LogicalWidth, Wp8Display.LogicalHeight)
                            : new SizeF(Wp8Display.PhysicalWidth, Wp8Display.PhysicalHeight);
                    case "Single":
                    case "Double":
                        return constant is int or float or double
                            ? Convert.ToSingle(constant)
                            : method.Contains("Scale", StringComparison.OrdinalIgnoreCase) ? (float)Wp8Display.Scale : 0f;
                    case "Boolean":
                        // WPR answers every licence question as "bought" (Guide.IsTrialMode is false),
                        // so a full-version flag the page computed from it is true.
                        return constant is int b ? b != 0
                            : method.Contains("FullVersion", StringComparison.OrdinalIgnoreCase) || method.Contains("Licensed", StringComparison.OrdinalIgnoreCase)
                              || method.Contains("Purchased", StringComparison.OrdinalIgnoreCase);
                    case "String":
                        return constant as string ?? DeviceAnswer(method) ?? "";
                    case "Int32":
                    case "UInt32":
                    case "Int64":
                    case "Int16":
                    case "Byte":
                        return constant is int n ? n : method.Contains("Scale", StringComparison.OrdinalIgnoreCase) ? Wp8Display.Scale : 0;
                }

                if (metadata.DelegateInvoke(type) is not null)
                {
                    return CallbackFor(type);
                }

                if (metadata.Interface(type) is not null)
                {
                    return new XamlHostObject(type);
                }

                return constant is int e ? e : null;
            }
        }

        /// <summary>The component calls a managed shell makes, grouped by the lifecycle point that reaches them.</summary>
        private sealed class ShellReading
        {
            public string Path { get; private init; } = "";

            public List<string> Mentions { get; } = [];

            public List<ShellCall> Launch { get; } = [];

            public List<ShellCall> Loaded { get; } = [];

            public List<ShellCall> Navigated { get; } = [];

            public List<ShellCall> Deactivated { get; } = [];

            public List<ShellCall> Activated { get; } = [];

            public List<ShellCall> BackKey { get; } = [];

            public List<ShellCall> Closing { get; } = [];

            public List<ShellCall> All { get; } = [];

            /// <summary>The page calls <c>DrawingSurface.SetContentProvider</c>.</summary>
            public bool UsesDrawingSurface { get; private set; }

            /// <summary>The page calls <c>DrawingSurfaceBackgroundGrid.SetBackgroundContentProvider</c>.</summary>
            public bool UsesBackgroundGrid { get; private set; }

            private readonly MetadataReader _md;
            private readonly PEReader _pe;
            private readonly IReadOnlyDictionary<string, string> _classes;

            private readonly HashSet<string> _drawing;

            private ShellReading(PEReader pe, IReadOnlyDictionary<string, string> classes, HashSet<string> drawing)
            {
                _pe = pe;
                _md = pe.GetMetadataReader();
                _classes = classes;
                _drawing = drawing;
            }

            /// <param name="classes">Owner type name to the runtime class it stands for.</param>
            /// <param name="drawing">The classes with CreateContentProvider.</param>
            public static ShellReading Read(string path, IReadOnlyDictionary<string, string> classes, HashSet<string> drawing)
            {
                using FileStream stream = File.OpenRead(path);
                using PEReader pe = new(stream, PEStreamOptions.PrefetchEntireImage);
                ShellReading reading = new(pe, classes, drawing) { Path = path };
                reading.ReadAll();
                return reading;
            }

            private void ReadAll()
            {
                List<TypeDefinition> apps = [], pages = [];
                foreach (TypeDefinitionHandle handle in _md.TypeDefinitions)
                {
                    TypeDefinition type = _md.GetTypeDefinition(handle);
                    string? baseName = BaseName(type);
                    if (baseName == "System.Windows.Application")
                    {
                        apps.Add(type);
                    }
                    else if (baseName is "Microsoft.Phone.Controls.PhoneApplicationPage" || DerivesFromPage(type, 0))
                    {
                        pages.Add(type);
                    }
                }

                foreach (TypeDefinition app in apps)
                {
                    Walk(app, m => m == ".ctor", Launch);
                    Walk(app, m => m.Contains("Launching", StringComparison.Ordinal), Launch);
                    Walk(app, m => m.Contains("Deactivated", StringComparison.Ordinal), Deactivated);
                    Walk(app, m => m.Contains("Activated", StringComparison.Ordinal) && !m.Contains("Deactivated", StringComparison.Ordinal), Activated);
                    Walk(app, m => m.Contains("Closing", StringComparison.Ordinal), Closing);
                }

                foreach (TypeDefinition page in pages)
                {
                    Walk(page, m => m == ".ctor", Launch);
                    Walk(page, m => m.EndsWith("Loaded", StringComparison.Ordinal) && m != "get_Loaded", Loaded);
                    Walk(page, m => m == "OnNavigatedTo", Navigated);
                    Walk(page, m => m == "OnNavigatedFrom", Deactivated);
                    Walk(page, m => m.Contains("BackKeyPress", StringComparison.Ordinal), BackKey);
                }

                foreach (TypeDefinitionHandle handle in _md.TypeDefinitions)
                {
                    foreach (MethodDefinitionHandle mh in _md.GetTypeDefinition(handle).GetMethods())
                    {
                        Collect(mh, All, 0, [], followCalls: false);
                    }
                }

                Mentions.AddRange(All.Select(c => c.Class).Where(_drawing.Contains));
            }

            private bool DerivesFromPage(TypeDefinition type, int depth)
            {
                if (depth > 4 || type.BaseType.IsNil || type.BaseType.Kind != HandleKind.TypeDefinition)
                {
                    return false;
                }

                TypeDefinition parent = _md.GetTypeDefinition((TypeDefinitionHandle)type.BaseType);
                return BaseName(parent) == "Microsoft.Phone.Controls.PhoneApplicationPage" || DerivesFromPage(parent, depth + 1);
            }

            private string? BaseName(TypeDefinition type) => type.BaseType.IsNil ? null : TypeName(type.BaseType);

            private void Walk(TypeDefinition type, Func<string, bool> root, List<ShellCall> into)
            {
                foreach (MethodDefinitionHandle mh in type.GetMethods())
                {
                    if (root(_md.GetString(_md.GetMethodDefinition(mh).Name)))
                    {
                        Collect(mh, into, 0, [], followCalls: true);
                    }
                }
            }

            private void Collect(MethodDefinitionHandle handle, List<ShellCall> into, int depth, HashSet<MethodDefinitionHandle> visited, bool followCalls)
            {
                if (depth > MaxDepth || !visited.Add(handle))
                {
                    return;
                }

                MethodDefinition method = _md.GetMethodDefinition(handle);
                if (method.RelativeVirtualAddress == 0)
                {
                    return;
                }

                byte[] il = _pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes() ?? [];
                List<(ILOpCode Op, int Token, object? Constant)> code = Decode(il);
                for (int i = 0; i < code.Count; i++)
                {
                    (ILOpCode op, int token, _) = code[i];
                    if (op is not (ILOpCode.Call or ILOpCode.Callvirt or ILOpCode.Newobj or ILOpCode.Ldftn or ILOpCode.Ldvirtftn))
                    {
                        continue;
                    }

                    EntityHandle target = MetadataTokens.EntityHandle(token);
                    if (target.Kind == HandleKind.MethodSpecification)
                    {
                        target = _md.GetMethodSpecification((MethodSpecificationHandle)target).Method;
                    }

                    if (target.Kind == HandleKind.MethodDefinition)
                    {
                        if (followCalls && op is not (ILOpCode.Ldftn or ILOpCode.Ldvirtftn))
                        {
                            Collect((MethodDefinitionHandle)target, into, depth + 1, visited, followCalls);
                        }

                        continue;
                    }

                    if (target.Kind != HandleKind.MemberReference)
                    {
                        continue;
                    }

                    MemberReference member = _md.GetMemberReference((MemberReferenceHandle)target);
                    string? owner = member.Parent.Kind is HandleKind.TypeReference or HandleKind.TypeDefinition ? TypeName(member.Parent) : null;
                    string memberName = _md.GetString(member.Name);
                    if (owner == "System.Windows.Controls.DrawingSurface" && memberName == "SetContentProvider")
                    {
                        UsesDrawingSurface = true;
                    }
                    else if (owner == "System.Windows.Controls.DrawingSurfaceBackgroundGrid" && memberName == "SetBackgroundContentProvider")
                    {
                        UsesBackgroundGrid = true;
                    }

                    if (owner is null || !_classes.TryGetValue(owner, out string? resolved))
                    {
                        continue;
                    }

                    owner = resolved;

                    BlobReader signature = _md.GetBlobReader(member.Signature);
                    SignatureHeader header = signature.ReadSignatureHeader();
                    int parameters = header.IsGeneric ? 0 : signature.ReadCompressedInteger();
                    List<object?> trailing = [];
                    for (int back = i - 1; back >= 0 && trailing.Count < parameters && code[back].Op.IsConstantLoad(); back--)
                    {
                        trailing.Insert(0, code[back].Constant);
                    }

                    string? next = null;
                    if (i + 1 < code.Count && code[i + 1].Op is ILOpCode.Call or ILOpCode.Callvirt)
                    {
                        EntityHandle nextTarget = MetadataTokens.EntityHandle(code[i + 1].Token);
                        next = nextTarget.Kind == HandleKind.MemberReference
                            ? _md.GetString(_md.GetMemberReference((MemberReferenceHandle)nextTarget).Name)
                            : nextTarget.Kind == HandleKind.MethodDefinition
                                ? _md.GetString(_md.GetMethodDefinition((MethodDefinitionHandle)nextTarget).Name)
                                : null;
                    }

                    into.Add(new ShellCall(owner, memberName, op == ILOpCode.Newobj, !header.IsInstance,
                        op is ILOpCode.Ldftn or ILOpCode.Ldvirtftn, parameters, [.. trailing], next));
                }
            }

            private string? TypeName(EntityHandle handle)
            {
                switch (handle.Kind)
                {
                    case HandleKind.TypeReference:
                        TypeReference reference = _md.GetTypeReference((TypeReferenceHandle)handle);
                        string ns = _md.GetString(reference.Namespace);
                        return ns.Length == 0 ? _md.GetString(reference.Name) : ns + "." + _md.GetString(reference.Name);
                    case HandleKind.TypeDefinition:
                        TypeDefinition definition = _md.GetTypeDefinition((TypeDefinitionHandle)handle);
                        string dns = _md.GetString(definition.Namespace);
                        return dns.Length == 0 ? _md.GetString(definition.Name) : dns + "." + _md.GetString(definition.Name);
                    default:
                        return null;
                }
            }

            /// <summary>Opcodes with their metadata token or literal operand; nothing else is kept.</summary>
            private List<(ILOpCode, int, object?)> Decode(byte[] il)
            {
                List<(ILOpCode, int, object?)> code = [];
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
                    int token = size == 4 && at + 4 <= il.Length ? BitConverter.ToInt32(il, at) : 0;
                    object? constant = op switch
                    {
                        >= ILOpCode.Ldc_i4_m1 and <= ILOpCode.Ldc_i4_8 => (int)op - (int)ILOpCode.Ldc_i4_0,
                        ILOpCode.Ldc_i4_s => (int)(sbyte)il[at],
                        ILOpCode.Ldc_i4 => token,
                        ILOpCode.Ldc_r4 => BitConverter.ToSingle(il, at),
                        ILOpCode.Ldc_r8 => (float)BitConverter.ToDouble(il, at),
                        ILOpCode.Ldstr => _md.GetUserString(MetadataTokens.UserStringHandle(token & 0xFFFFFF)),
                        _ => null,
                    };
                    code.Add((op, token, constant));
                    at += size;
                }

                return code;
            }

            private static int OperandSize(ILOpCode op, byte[] il, int at)
            {
                int value = (int)op;
                if (value >= 0xFE00)
                {
                    return op switch
                    {
                        ILOpCode.Ldftn or ILOpCode.Ldvirtftn or ILOpCode.Initobj or ILOpCode.Constrained or ILOpCode.Sizeof => 4,
                        ILOpCode.Ldarg or ILOpCode.Ldarga or ILOpCode.Starg or ILOpCode.Ldloc or ILOpCode.Ldloca or ILOpCode.Stloc => 2,
                        ILOpCode.Unaligned or (ILOpCode)0xFE19 => 1, // unaligned., no.
                        _ => 0,
                    };
                }

                switch (value)
                {
                    case >= 0x0E and <= 0x13: // ldarg.s .. stloc.s
                    case 0x1F: // ldc.i4.s
                    case >= 0x2B and <= 0x37: // short branches
                    case 0xDE: // leave.s
                        return 1;
                    case 0x21: // ldc.i8
                    case 0x23: // ldc.r8
                        return 8;
                    case 0x45: // switch
                        return at + 4 <= il.Length ? 4 + 4 * BitConverter.ToInt32(il, at) : il.Length - at;
                    case 0x20: case 0x22: case 0x27: case 0x28: case 0x29:
                    case >= 0x38 and <= 0x44:
                    case >= 0x6F and <= 0x75:
                    case 0x79:
                    case >= 0x7B and <= 0x81:
                    case 0x8C: case 0x8D: case 0x8F:
                    case 0xA3: case 0xA4: case 0xA5:
                    case 0xC2: case 0xC6: case 0xD0: case 0xDD:
                        return 4;
                    default:
                        return 0;
                }
            }
        }

        private static bool IsConstantLoad(this ILOpCode op) => op is >= ILOpCode.Ldc_i4_m1 and <= ILOpCode.Ldc_r8 or ILOpCode.Ldstr;
    }
}
