using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;

namespace WPR.SilverlightCompability
{
    /// <summary>
    /// Parses XAML text into a Silverlight-shaped object tree backed by WPR shim types.
    /// Supports: type resolution from xmlns / clr-namespace, attribute → DP/CLR property
    /// setting with type conversion, property element syntax, x:Name registration.
    ///
    /// NOT supported in 1.4: markup extensions ({StaticResource}, {Binding}), x:Class wiring,
    /// content/children (no Panel types yet), styles, templates, animation triples.
    /// </summary>
    public static class XamlReader
    {
        private const string PresentationNs = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        private const string XamlDirectiveNs = "http://schemas.microsoft.com/winfx/2006/xaml";
        private const string MarkupCompatNs = "http://schemas.openxmlformats.org/markup-compatibility/2006";

        /// <summary>
        /// XML namespaces whose attributes should be silently skipped during parsing. These are
        /// designer-tooling namespaces — Blend, the WPF designer, the legacy WP designer — that
        /// only matter inside Visual Studio. Real Silverlight relies on <c>mc:Ignorable</c> to
        /// list these on the root element; we hard-code the common ones so unpatched XAML still
        /// loads cleanly even if its <c>mc:Ignorable</c> declaration is missing.
        /// </summary>
        private static readonly HashSet<string> AlwaysIgnorableNamespaces = new(StringComparer.Ordinal)
        {
            "http://schemas.microsoft.com/expression/blend/2008",
            "http://schemas.microsoft.com/expression/blend/2006",
            "http://schemas.microsoft.com/expression/interactivity/2010",
            "http://schemas.microsoft.com/winfx/2006/xaml/presentation/options",
        };

        /// <summary>
        /// Logical (xmlns → CLR namespace lookups) for the default presentation namespace.
        /// Real Silverlight maps it across many CLR namespaces; we collapse to one project.
        /// </summary>
        private static readonly (string Namespace, Assembly Assembly)[] PresentationLookups =
        {
            ("WPR.SilverlightCompability", typeof(FrameworkElement).Assembly),
            // Same assembly, second namespace: the types absorbed from WPR.WindowsCompability
            // (BitmapImage, WriteableBitmap, ResourceDictionary, MessageBox, …) kept their
            // original namespace, so terse XAML naming them under the presentation xmlns would
            // otherwise fail to resolve even though they are right here.
            ("WPR.WindowsCompability", typeof(FrameworkElement).Assembly),
        };

        // The CLR namespace our Silverlight shims live in. NOT the assembly name — the assembly
        // was renamed to WPR.Framework.Silverlight in Stage 3 and the two have differed since.
        private const string SlcNamespace = "WPR.SilverlightCompability";

        // Resolved from the running assembly rather than written as a literal, so a future
        // rename cannot silently turn the redirects below into dead code again (which is exactly
        // what happened when this was a literal "WPR.SilverlightCompability").
        private static readonly string SlcAssemblyName =
            typeof(FrameworkElement).Assembly.GetName().Name!;

        private const string PhoneAssemblyName = "Microsoft.Phone";

        /// <summary>
        /// Compatibility redirects for un-patched XAML resources (e.g. straight from a XAP).
        /// Maps a source CLR namespace to (target namespace, target assembly name). Resolved by
        /// assembly NAME because the WP control/shell/gesture types now live in the
        /// Microsoft.Phone assembly, which this reader (in WPR.SilverlightCompability) cannot
        /// reference — doing so would recreate the dependency cycle. Redirecting the phone
        /// namespaces here also keeps OUR shim winning over any user-bundled
        /// Microsoft.Phone.Controls.Toolkit.dll that ships its own GestureListener etc.
        /// </summary>
        private static readonly Dictionary<string, (string Namespace, string AssemblyName)> ClrNsRedirects =
            new(StringComparer.Ordinal)
            {
                ["Microsoft.Phone.Controls"] = ("Microsoft.Phone.Controls", PhoneAssemblyName),
                ["Microsoft.Phone.Shell"] = ("Microsoft.Phone.Shell", PhoneAssemblyName),
                ["System.Windows.Controls"] = (SlcNamespace, SlcAssemblyName),
                ["System.Windows"] = (SlcNamespace, SlcAssemblyName),
                ["System.Windows.Media"] = (SlcNamespace, SlcAssemblyName),
                ["System.Windows.Navigation"] = (SlcNamespace, SlcAssemblyName),
            };

        public static object Load(string xaml)
        {
            if (xaml == null) throw new ArgumentNullException(nameof(xaml));

            XDocument doc;
            try { doc = XDocument.Parse(xaml); }
            catch (Exception ex) { throw new XamlParseException("Failed to parse XAML: " + ex.Message, ex); }

            if (doc.Root == null)
                throw new XamlParseException("XAML has no root element");

            var ctx = new ParseContext();
            return ProcessElement(doc.Root, ctx);
        }

        /// <summary>
        /// Re-process a previously captured XElement (used by DataTemplate to materialize
        /// its deferred visual tree).
        /// </summary>
        public static object LoadElement(XElement element)
        {
            if (element == null) throw new ArgumentNullException(nameof(element));
            return ProcessElement(element, new ParseContext());
        }

        /// <summary>
        /// Loads XAML into an existing component instance (the code-behind partial class).
        /// The XAML root's <c>x:Class</c> is expected to match the component's type.
        /// Attributes/children apply to the component; <c>x:Name</c>'d elements get
        /// reflected onto matching fields; <c>EventName="MethodName"</c> attributes
        /// hook events to methods on the component.
        /// </summary>
        public static void LoadComponent(object component, string xaml)
        {
            if (component == null) throw new ArgumentNullException(nameof(component));
            if (xaml == null) throw new ArgumentNullException(nameof(xaml));

            XDocument doc;
            try { doc = XDocument.Parse(xaml); }
            catch (Exception ex) { throw new XamlParseException("Failed to parse XAML: " + ex.Message, ex); }
            if (doc.Root == null) throw new XamlParseException("XAML has no root element");

            var ctx = new ParseContext { ComponentRoot = component };
            ProcessElement(doc.Root, ctx);
            WireFields(component, ctx);

            // Hand the name table to the component so its FrameworkElement.FindName
            // lookups (called by the auto-generated InitializeComponent right after
            // us) resolve from this authoritative table instead of walking the
            // logical tree — which can't always reach into ItemsControl-derived
            // containers like Panorama that don't expose their items via Children.
            if (component is FrameworkElement fe)
                fe._nameScope = ctx.NameScope;

            // DIAGNOSTIC: dump the registered names + their types so it's visible at runtime
            // which x:Name'd elements actually made it through parsing — silent skips on
            // failed sub-tree elements are a common cause of post-InitializeComponent
            // NREs in user-control ctors.
            Console.WriteLine($"[XamlReader] LoadComponent({component.GetType().FullName}) registered {ctx.NameScope.Count} names:");
            foreach (var kv in ctx.NameScope)
                Console.WriteLine($"    {kv.Key} -> {kv.Value?.GetType().FullName ?? "<null>"}");
        }

        /// <summary>
        /// Map of x:Name → object instance, populated during the parse.
        /// Exposed for tests; production callers go through FrameworkElement.FindName.
        /// </summary>
        internal class ParseContext
        {
            public Dictionary<string, object> NameScope { get; } = new(StringComparer.Ordinal);

            /// <summary>
            /// When set, the root element of the XAML is loaded into this instance instead
            /// of being created. Set by <see cref="LoadComponent"/>.
            /// </summary>
            public object? ComponentRoot { get; set; }

            /// <summary>True for the very first call into ProcessElement only.</summary>
            public bool IsRoot { get; set; } = true;
        }

        private static void WireFields(object component, ParseContext ctx)
        {
            Type t = component.GetType();
            foreach (var kv in ctx.NameScope)
            {
                FieldInfo? f = null;
                for (Type? cur = t; cur != null && f == null; cur = cur.BaseType)
                {
                    f = cur.GetField(kv.Key,
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                }
                if (f != null && f.FieldType.IsAssignableFrom(kv.Value.GetType()))
                    f.SetValue(component, kv.Value);
            }
        }

        private static object ProcessElement(XElement element, ParseContext ctx)
        {
            if (element.Name.LocalName.Contains('.'))
                throw new XamlParseException(
                    $"Property element '{element.Name.LocalName}' encountered as standalone (must be nested under its owner element).");

            bool isRootCall = ctx.IsRoot;
            ctx.IsRoot = false; // every nested ProcessElement sees this as false

            object instance;
            Type type;

            // If LoadComponent supplied a target instance and we're at the XAML root, the
            // root element's x:Class is expected to match component.GetType() — use that
            // instance instead of creating a new one. Sub-elements always create fresh.
            if (isRootCall && ctx.ComponentRoot != null)
            {
                instance = ctx.ComponentRoot;
                type = instance.GetType();
            }
            else
            {
                type = ResolveType(element.Name);
                try { instance = Activator.CreateInstance(type)!; }
                catch (Exception ex)
                {
                    // Surface the inner chain to console — but DON'T call ex.ToString() here:
                    // its stack-trace formatter reads custom attributes off the frames, and a
                    // missing attribute type (e.g. StyleTypedPropertyAttribute) would throw a
                    // *new* TypeLoadException that masks the original. .GetType() + .Message
                    // is safe.
                    Exception inner = ex.InnerException ?? ex;
                    Console.WriteLine($"[XamlReader] CreateInstance FAILED for '{type.FullName}': " +
                                      $"{inner.GetType().FullName}: {inner.Message}");
                    throw new XamlParseException($"Failed to instantiate '{type.FullName}': {ex.Message}", ex);
                }
            }

            // Pass 1: attributes (skip xmlns declarations, x: directives handled inline).
            // A single problematic attribute — a property-changed handler that JIT-fails
            // because of an un-shimmed type, a missing converter, or anything else — must
            // not take down the entire page load. Otherwise *all* children of the parent
            // element get silently skipped because we never reach Pass 2 below. Swallow
            // everything (including XamlParseException) and continue with the next attr.
            foreach (XAttribute attr in element.Attributes())
            {
                if (attr.IsNamespaceDeclaration) continue;
                try { ApplyAttribute(instance, type, attr, ctx); }
                catch (Exception aex)
                {
                    Exception inner = aex.InnerException ?? aex;
                    Console.WriteLine(
                        $"[XamlReader] SKIP attr '{attr.Name.LocalName}=\"{attr.Value}\"' on '{type.Name}': " +
                        $"{inner.GetType().FullName}: {inner.Message}");
                }
            }

            // DataTemplate is not parsed recursively — its inner XML is captured deferred and
            // re-evaluated at LoadContent time (with the item as DataContext).
            if (instance is DataTemplate dt)
            {
                XElement? firstChild = null;
                foreach (XNode node in element.Nodes())
                {
                    if (node is XElement el) { firstChild = el; break; }
                }
                dt.VisualTreeRoot = firstChild;
                return instance;
            }

            // Pass 2: child elements — property element syntax + content children.
            foreach (XNode node in element.Nodes())
            {
                if (node is XElement child)
                {
                    // One unresolvable child shouldn't kill the whole load.
                    // See the per-collection-item skip in ApplyChildElement for the
                    // same rationale; this catch handles direct content children
                    // (e.g. <UserControl><Grid/></UserControl> where Grid resolves
                    // but its sub-tree references a missing type).
                    try { ApplyChildElement(instance, type, child, ctx); }
                    catch (XamlParseException xex)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[XamlReader] Skipping child '{child.Name}' on '{type.Name}': {xex.Message}");
                        Console.WriteLine(
                            $"[XamlReader] SKIP child '{child.Name}' on '{type.Name}': {xex.Message}");
                    }
                }
            }

            return instance;
        }

        private static void ApplyAttribute(object instance, Type type, XAttribute attr, ParseContext ctx)
        {
            string ns = attr.Name.NamespaceName;

            // mc:Ignorable, mc:ProcessContent, etc. are markup-compatibility directives — they
            // tell the XAML parser which prefixes to skip, but we hard-code the common designer
            // namespaces below, so we just drop these on the floor.
            if (ns == MarkupCompatNs) return;

            // Designer-only namespaces (Blend, WPF designer, presentation options like
            // Freeze=True). They never affect runtime behaviour; ignore.
            if (AlwaysIgnorableNamespaces.Contains(ns)) return;

            // x: directives
            if (ns == XamlDirectiveNs)
            {
                switch (attr.Name.LocalName)
                {
                    case "Name":
                        RegisterName(instance, type, attr.Value, ctx);
                        return;
                    case "Class":
                    case "Key":
                        // Recognized but not wired in 1.4.
                        return;
                    default:
                        // Unknown x: directive — ignore for forward compatibility.
                        return;
                }
            }

            string local = attr.Name.LocalName;
            if (local.Contains('.'))
            {
                ApplyAttachedProperty(instance, attr);
                return;
            }

            // {Binding ...} and other markup extensions
            if (MarkupExtensionParser.IsMarkupExtension(attr.Value))
            {
                if (TryApplyMarkupExtension(instance, type, local, attr.Value))
                    return;
            }

            // Event handler: e.g. Click="OnButtonClicked". Looked up on the component root
            // (the code-behind class) when LoadComponent supplied one; otherwise a no-op.
            EventInfo? ev = type.GetEvent(local, BindingFlags.Public | BindingFlags.Instance);
            if (ev != null)
            {
                if (ctx.ComponentRoot == null)
                    throw new XamlParseException(
                        $"Event '{local}' on '{type.Name}' set to '{attr.Value}', but no component root is available to look up the handler. " +
                        "This usually means LoadComponent wasn't used.");
                BindEventHandler(instance, ev, attr.Value, ctx.ComponentRoot);
                return;
            }

            SetMember(instance, type, local, attr.Value);
        }

        private static void BindEventHandler(object eventOwner, EventInfo ev, string methodName, object handlerHost)
        {
            Type handlerType = ev.EventHandlerType
                ?? throw new XamlParseException($"Event '{ev.Name}' has no handler type.");

            MethodInfo? method = null;
            for (Type? t = handlerHost.GetType(); t != null && method == null; t = t.BaseType)
            {
                method = t.GetMethod(methodName,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            }
            if (method == null)
                throw new XamlParseException(
                    $"Event handler method '{methodName}' not found on '{handlerHost.GetType().FullName}'.");

            Delegate? del;
            try { del = Delegate.CreateDelegate(handlerType, handlerHost, method); }
            catch (Exception ex)
            {
                // Fallback: Delegate.CreateDelegate is strict about type identity.
                // When the strict path fails — typically because the patcher's
                // typeref rewrite and our reflection see the "same" type via
                // different load paths — build a reflection-invoke trampoline
                // wrapped in the target delegate type. Slower but always works.
                ParameterInfo[] ps = method.GetParameters();
                Console.WriteLine(
                    $"[XamlReader] CreateDelegate strict failed for '{methodName}' on '{handlerHost.GetType().Name}' " +
                    $"(event {ev.Name}={handlerType.FullName}): {ex.Message}");
                Console.WriteLine($"    method params: {string.Join(", ", ps.Select(p => $"{p.ParameterType.FullName}@{p.ParameterType.Assembly.GetName().Name}"))}");
                var invoke = handlerType.GetMethod("Invoke");
                if (invoke != null)
                {
                    var dParams = invoke.GetParameters();
                    Console.WriteLine($"    delegate params: {string.Join(", ", dParams.Select(p => $"{p.ParameterType.FullName}@{p.ParameterType.Assembly.GetName().Name}"))}");
                }

                try
                {
                    del = BuildReflectionTrampoline(handlerType, handlerHost, method);
                    if (del == null)
                        throw new XamlParseException($"Could not build trampoline for '{methodName}' on '{handlerHost.GetType().Name}'.");
                    Console.WriteLine($"[XamlReader] Recovered via reflection trampoline.");
                }
                catch (Exception tex)
                {
                    throw new XamlParseException(
                        $"Method '{methodName}' on '{handlerHost.GetType().Name}' could not be bound to event '{ev.Name}': " +
                        $"{tex.Message}", tex);
                }
            }
            ev.AddEventHandler(eventOwner, del);
        }

        /// <summary>
        /// Build a delegate of <paramref name="handlerType"/> that invokes
        /// <paramref name="method"/> reflectively on <paramref name="target"/>.
        /// Uses System.Linq.Expressions so the resulting delegate is a tightly-
        /// typed wrapper (no per-call boxing of value-type args).
        /// </summary>
        private static Delegate? BuildReflectionTrampoline(Type handlerType, object target, MethodInfo method)
        {
            var invoke = handlerType.GetMethod("Invoke");
            if (invoke == null) return null;
            var invokeParams = invoke.GetParameters();
            var methodParams = method.GetParameters();
            if (invokeParams.Length != methodParams.Length) return null;

            // Build parameters that match the delegate's Invoke signature.
            var lambdaParams = invokeParams
                .Select((p, i) => System.Linq.Expressions.Expression.Parameter(p.ParameterType, "p" + i))
                .ToArray();

            // Build (T0)p0, (T1)p1 ... where Ti is the method's expected param type —
            // works for reference types (assignment-compatible upcast) and value types
            // via unbox.any. If the parameters are not compatible at runtime
            // a cast exception will surface there rather than at delegate creation.
            var callArgs = lambdaParams
                .Select((p, i) => (System.Linq.Expressions.Expression)
                    (methodParams[i].ParameterType == p.Type
                        ? p
                        : System.Linq.Expressions.Expression.Convert(p, methodParams[i].ParameterType)))
                .ToArray();

            var instanceExpr = method.IsStatic
                ? null
                : System.Linq.Expressions.Expression.Constant(target, method.DeclaringType ?? target.GetType());
            var call = System.Linq.Expressions.Expression.Call(instanceExpr, method, callArgs);

            var lambda = System.Linq.Expressions.Expression.Lambda(handlerType, call, lambdaParams);
            return lambda.Compile();
        }

        private static bool TryApplyMarkupExtension(object instance, Type type, string memberName, string raw)
        {
            var parsed = MarkupExtensionParser.Parse(raw);

            // {StaticResource X} — look up X in (a) the element's own Resources walking up,
            // (b) the Application.Resources global bag. PhoneTheme seeds the standard WP7
            // brushes / font sizes into Application.Resources so unqualified user-XAML
            // references resolve.
            if (parsed.TypeName == "StaticResource" || parsed.TypeName == "ThemeResource")
            {
                string key = parsed.PositionalArgs.Count > 0
                    ? parsed.PositionalArgs[0]
                    : parsed.NamedArgs.TryGetValue("ResourceKey", out var k) ? k : "";
                object? resolved = ResolveResource(instance, key);
                if (resolved == null) return false; // fall through to permissive null assignment

                // Find the DP or CLR property and assign — same lookup SetMember uses but with
                // an already-typed value so we skip XamlTypeConverter.
                FieldInfo? dpField = FindStaticField(type, memberName + "Property");
                if (dpField?.GetValue(null) is DependencyProperty dp && instance is DependencyObject d)
                {
                    d.SetValue(dp, resolved);
                    return true;
                }
                PropertyInfo? prop = type.GetProperty(memberName,
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                if (prop != null && prop.CanWrite &&
                    (resolved == null || prop.PropertyType.IsAssignableFrom(resolved.GetType())))
                {
                    prop.SetValue(instance, resolved);
                    return true;
                }
                return false;
            }

            if (parsed.TypeName == "Binding" || parsed.TypeName == "TemplateBinding")
            {
                if (instance is not FrameworkElement fe)
                    throw new XamlParseException(
                        $"{{Binding}} on member '{memberName}' requires a FrameworkElement target (got '{type.Name}').");

                FieldInfo? dpField = FindStaticField(type, memberName + "Property");
                if (dpField?.GetValue(null) is not DependencyProperty dp)
                    throw new XamlParseException(
                        $"{{Binding}} on member '{memberName}' requires a DependencyProperty (no '{memberName}Property' on '{type.Name}').");

                var binding = new Binding();
                if (parsed.PositionalArgs.Count > 0) binding.Path = parsed.PositionalArgs[0];
                if (parsed.NamedArgs.TryGetValue("Path", out var path)) binding.Path = path;
                if (parsed.NamedArgs.TryGetValue("Mode", out var mode) &&
                    Enum.TryParse(typeof(BindingMode), mode, true, out var modeObj))
                {
                    binding.Mode = (BindingMode)modeObj;
                }
                // Source can be a nested {StaticResource X} — common pattern for
                // localized strings: Source={StaticResource LocalizedStrings}.
                if (parsed.NamedArgs.TryGetValue("Source", out var src))
                {
                    if (MarkupExtensionParser.IsMarkupExtension(src))
                    {
                        var inner = MarkupExtensionParser.Parse(src);
                        if (inner.TypeName == "StaticResource" || inner.TypeName == "ThemeResource")
                        {
                            string key = inner.PositionalArgs.Count > 0 ? inner.PositionalArgs[0]
                                : inner.NamedArgs.TryGetValue("ResourceKey", out var k) ? k : "";
                            binding.Source = ResolveResource(fe, key);
                        }
                    }
                    else
                    {
                        binding.Source = src;
                    }
                }
                // ElementName="Foo" — Source resolves to the element registered under that name.
                if (parsed.NamedArgs.TryGetValue("ElementName", out var elemName))
                {
                    binding.Source = fe.FindName(elemName);
                }

                fe.SetBinding(dp, binding);
                return true;
            }

            // Unknown markup extension — fall through to plain string assignment.
            return false;
        }

        /// <summary>
        /// Looked up by <see cref="ResolveResource"/> for the Application-scope fallback.
        /// <c>WPR.WindowsCompability.Application</c> wires its own lookup in via this
        /// delegate at construction time (we can't reference it directly without a
        /// circular project ref). Null when no app singleton is available.
        /// </summary>
        public static Func<string, object?>? ApplicationResourceLookup;

        /// <summary>
        /// Looks up a StaticResource by key. Walks: this element's Resources →
        /// each ancestor's Resources → Application.Current.Resources (via
        /// <see cref="ApplicationResourceLookup"/>). First hit wins.
        /// </summary>
        internal static object? ResolveResource(object? scopeStart, string key)
        {
            if (string.IsNullOrEmpty(key)) return null;

            // Element-local + ancestor scope.
            for (var fe = scopeStart as FrameworkElement; fe != null; fe = fe.Parent as FrameworkElement)
            {
                if (fe.HasResources && fe.Resources.TryGetValue(key, out var localHit) && localHit != null)
                    return localHit;
            }

            // Application-wide scope, via the registered callback.
            try { return ApplicationResourceLookup?.Invoke(key); }
            catch { return null; }
        }

        private static void ApplyAttachedProperty(object instance, XAttribute attr)
        {
            string local = attr.Name.LocalName;
            int dot = local.IndexOf('.');
            string ownerLocal = local.Substring(0, dot);
            string propName = local.Substring(dot + 1);

            Type? ownerType;
            try { ownerType = ResolveType(XName.Get(ownerLocal, attr.Name.NamespaceName)); }
            catch (Exception ex)
            {
                // shell:SystemTray.IsVisible and other un-shimmed attached owners are common.
                // Skip rather than crash the page load.
                System.Diagnostics.Debug.WriteLine(
                    $"[XamlReader] Skipping attached property '{local}' (owner type unresolved: {ex.Message})");
                return;
            }

            FieldInfo? dpField = FindStaticField(ownerType, propName + "Property");
            if (dpField?.GetValue(null) is not DependencyProperty dp)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[XamlReader] Skipping attached property '{ownerLocal}.{propName}' (no '{propName}Property' on '{ownerType.FullName}').");
                return;
            }

            if (instance is not DependencyObject d)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[XamlReader] Skipping attached property '{ownerLocal}.{propName}' on non-DO '{instance.GetType().Name}'");
                return;
            }

            object? converted = XamlTypeConverter.Convert(attr.Value, dp.PropertyType);
            d.SetValue(dp, converted);
        }

        private static void ApplyChildElement(object instance, Type type, XElement child, ParseContext ctx)
        {
            string local = child.Name.LocalName;

            if (local.Contains('.'))
            {
                int dot = local.IndexOf('.');
                string ownerLocal = local.Substring(0, dot);
                string propName = local.Substring(dot + 1);

                // Don't enforce ownerName == type.Name strictly — derived types use base.PropertyName too.
                List<XElement> valueElements = child.Elements().ToList();

                // Detect attached-property element form:
                //   <toolkit:GestureService.GestureListener><toolkit:GestureListener .../></toolkit:GestureService.GestureListener>
                // The "owner" type is a DIFFERENT type that owns the attached DP — not
                // the parent element's own type. The parent (instance) just receives
                // the SetValue. This is distinct from the more common `<Type.Property>`
                // element form which sets a property on `instance` itself.
                Type? attachedOwner = null;
                if (!string.Equals(ownerLocal, type.Name, StringComparison.Ordinal))
                {
                    try { attachedOwner = ResolveType(XName.Get(ownerLocal, child.Name.NamespaceName)); }
                    catch { /* unresolved owner — fall through to the regular path */ }
                }
                if (attachedOwner != null && attachedOwner != type)
                {
                    FieldInfo? dpField = FindStaticField(attachedOwner, propName + "Property");
                    if (dpField?.GetValue(null) is DependencyProperty attachedDp
                        && instance is DependencyObject parentDo)
                    {
                        // Materialize the inner value: text content for primitive
                        // DPs, the (single) child element for ref-typed DPs.
                        object? attachedVal;
                        if (valueElements.Count == 0)
                        {
                            attachedVal = XamlTypeConverter.Convert(child.Value?.Trim() ?? "", attachedDp.PropertyType);
                        }
                        else
                        {
                            attachedVal = ProcessElement(valueElements[0], ctx);
                            if (attachedVal is string s)
                                attachedVal = XamlTypeConverter.Convert(s, attachedDp.PropertyType);
                        }
                        parentDo.SetValue(attachedDp, attachedVal);
                        return;
                    }
                    // If we couldn't find an attached DP, fall through and let the
                    // regular property-element handler try `instance.PropName`. Some
                    // XAML uses the namespaced form even for instance-typed members.
                }

                if (valueElements.Count == 0)
                {
                    string text = child.Value;
                    if (!string.IsNullOrWhiteSpace(text))
                        SetMember(instance, type, propName, text);
                    return;
                }

                // Read-only properties are collection-typed by convention (RowDefinitions, ColumnDefinitions, Children).
                // Append each child to the existing collection.
                PropertyInfo? prop = type.GetProperty(propName,
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                if (prop != null && !prop.CanWrite)
                {
                    object? coll = prop.GetValue(instance);
                    if (coll == null)
                        throw new XamlParseException(
                            $"Read-only property '{propName}' on '{type.Name}' returned null; cannot append children.");
                    foreach (XElement ce in valueElements)
                    {
                        // Skip individual collection items whose type can't be resolved
                        // or whose construction throws. App.xaml resource dictionaries
                        // routinely reference Silverlight types (IValueConverter,
                        // Behavior, etc.) that our shim doesn't fully cover — skipping
                        // a single resource is preferable to taking down the whole
                        // page load. Other resources, the visual tree, and runtime
                        // code that doesn't need this resource keep working.
                        object ci;
                        try { ci = ProcessElement(ce, ctx); }
                        catch (Exception xex)
                        {
                            // Broadened beyond XamlParseException — VisualStateManager-laden
                            // ControlTemplates inside Style.Setter.Value throw all kinds of
                            // things (MissingMethodException, NRE inside reflection-set, etc.).
                            // One resource going sideways shouldn't drop the whole dictionary.
                            System.Diagnostics.Debug.WriteLine(
                                $"[XamlReader] Skipping resource '{ce.Name}': {xex.Message}");
                            Console.WriteLine(
                                $"[XamlReader] SKIP resource '{ce.Name}': {xex.GetType().Name}: {xex.Message}");
                            continue;
                        }
                        try
                        {
                            if (!TryAddToCollection(coll, ci, ce))
                                throw new XamlParseException(
                                    $"No Add method on '{coll.GetType().Name}' accepts '{ci.GetType().Name}'.");
                        }
                        catch (Exception aex)
                        {
                            Console.WriteLine(
                                $"[XamlReader] SKIP add '{ce.Name}': {aex.GetType().Name}: {aex.Message}");
                        }
                    }
                    return;
                }

                // Writable property — single value.
                if (valueElements.Count > 1)
                    throw new XamlParseException(
                        $"Single-valued property '{propName}' on '{type.Name}' had {valueElements.Count} child elements.");
                object value = ProcessElement(valueElements[0], ctx);
                SetMember(instance, type, propName, value);
                return;
            }

            // Direct child element. Look up the type's [ContentProperty]; if it points
            // at a collection-typed property, append the child to that collection.
            string? contentPropName = FindContentPropertyName(type);
            if (contentPropName == null)
            {
                throw new XamlParseException(
                    $"Direct child element '{child.Name.LocalName}' on '{type.Name}', but '{type.Name}' " +
                    "has no [ContentProperty]. Wrap it in a property element, or annotate the type.");
            }

            object childInstance = ProcessElement(child, ctx);

            PropertyInfo? contentProp = type.GetProperty(contentPropName,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
            if (contentProp == null)
            {
                throw new XamlParseException(
                    $"[ContentProperty(\"{contentPropName}\")] on '{type.Name}' references a missing property.");
            }

            object? collection = contentProp.GetValue(instance);
            if (collection != null && TryAddToCollection(collection, childInstance, child))
                return;

            // Single-content (e.g. ContentControl.Content): assign directly.
            if (contentProp.CanWrite)
            {
                if (!contentProp.PropertyType.IsAssignableFrom(childInstance.GetType()))
                {
                    throw new XamlParseException(
                        $"Cannot assign '{childInstance.GetType().Name}' to content property " +
                        $"'{contentPropName}' (expected '{contentProp.PropertyType.Name}') on '{type.Name}'.");
                }
                contentProp.SetValue(instance, childInstance);
                return;
            }

            throw new XamlParseException(
                $"Content property '{contentPropName}' on '{type.Name}' has no Add(item) method and is not settable.");
        }

        private static bool TryAddToCollection(object collection, object item, XElement? sourceElement = null)
        {
            // Dictionary case: ResourceDictionary and friends use x:Key as the key.
            // We fall back to x:Name as the key when x:Key is missing — some game
            // App.xaml files (Minesweeper's, e.g.) declare resources with x:Name
            // instead, which technically requires SL to "x:Name as identifier" semantics.
            // Without this fallback one stray un-keyed item kills the rest of the
            // dictionary parse and every later resource lookup misses.
            if (collection is System.Collections.IDictionary dict)
            {
                string? key = sourceElement?.Attribute(XName.Get("Key", XamlDirectiveNs))?.Value
                              ?? sourceElement?.Attribute(XName.Get("Name", XamlDirectiveNs))?.Value;
                if (key == null)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[XamlReader] Skipping un-keyed/un-named resource '{item.GetType().Name}' in dictionary");
                    Console.WriteLine(
                        $"[XamlReader] SKIP dict-item '{item.GetType().Name}' (no x:Key/x:Name)");
                    return true; // pretend success — keep parsing siblings
                }
                dict[key] = item;
                return true;
            }
            if (collection is System.Collections.IList list)
            {
                list.Add(item);
                return true;
            }
            MethodInfo? addMethod = FindAddMethod(collection.GetType(), item.GetType());
            if (addMethod != null)
            {
                addMethod.Invoke(collection, new[] { item });
                return true;
            }
            return false;
        }

        private static MethodInfo? FindAddMethod(Type collectionType, Type itemType)
        {
            foreach (MethodInfo m in collectionType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (m.Name != "Add") continue;
                ParameterInfo[] ps = m.GetParameters();
                if (ps.Length == 1 && ps[0].ParameterType.IsAssignableFrom(itemType))
                    return m;
            }
            return null;
        }

        private static string? FindContentPropertyName(Type type)
        {
            // Walk inheritance chain; closest [ContentProperty] wins.
            for (Type? t = type; t != null; t = t.BaseType)
            {
                ContentPropertyAttribute? attr = (ContentPropertyAttribute?)
                    Attribute.GetCustomAttribute(t, typeof(ContentPropertyAttribute), inherit: false);
                if (attr != null) return attr.Name;
            }
            return null;
        }

        private static void RegisterName(object instance, Type type, string name, ParseContext ctx)
        {
            ctx.NameScope[name] = instance;
            // FrameworkElement also exposes a Name property; mirror it.
            PropertyInfo? nameProp = type.GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
            if (nameProp != null && nameProp.CanWrite && nameProp.PropertyType == typeof(string))
                nameProp.SetValue(instance, name);
        }

        /// <summary>
        /// Sets a member on the instance: tries DependencyProperty (PropertyName + "Property")
        /// first, then a CLR property. Performs string-to-target-type conversion.
        /// </summary>
        private static void SetMember(object instance, Type type, string memberName, object rawValue)
        {
            // Search up the type chain (DPs are inherited via static fields on base types).
            FieldInfo? dpField = FindStaticField(type, memberName + "Property");
            if (dpField != null)
            {
                if (dpField.GetValue(null) is DependencyProperty dp && instance is DependencyObject d)
                {
                    object? converted = ConvertIfString(rawValue, dp.PropertyType);
                    d.SetValue(dp, converted);
                    return;
                }
            }

            PropertyInfo? prop = type.GetProperty(memberName,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
            if (prop != null && prop.CanWrite)
            {
                object? converted = ConvertIfString(rawValue, prop.PropertyType);
                prop.SetValue(instance, converted);
                return;
            }

            // Permissive: real WP apps were compiled against full Silverlight, where every
            // FrameworkElement.* property exists. Our shim is partial. Rather than crash the
            // whole page load, log the miss and continue — the visual will just inherit the
            // default. Real errors (typos, broken bindings) still surface via the missing
            // property never taking effect; users can dig into the log if something looks off.
            System.Diagnostics.Debug.WriteLine(
                $"[XamlReader] Skipping unknown member '{memberName}' on '{type.FullName}' (= '{rawValue}')");
        }

        private static FieldInfo? FindStaticField(Type type, string name)
        {
            for (Type? t = type; t != null; t = t.BaseType)
            {
                FieldInfo? f = t.GetField(name,
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);
                if (f != null) return f;
            }
            return null;
        }

        private static object? ConvertIfString(object value, Type targetType)
        {
            if (value is string s) return XamlTypeConverter.Convert(s, targetType);
            return value;
        }

        private static Type ResolveType(XName name)
        {
            string xmlns = name.NamespaceName;
            string local = name.LocalName;

            if (xmlns == PresentationNs || xmlns == "")
            {
                foreach (var (ns, asm) in PresentationLookups)
                {
                    Type? t = SafeGetType(asm, ns + "." + local);
                    if (t != null) return t;
                }
                // PhoneApplicationPage and other WP types moved to the Microsoft.Phone assembly;
                // let the default/presentation namespace reach them too, so terse XAML (and our
                // tests) can name `<PhoneApplicationPage/>` without the phone: clr-namespace.
                Assembly? mp = FindRedirectAssembly(PhoneAssemblyName);
                if (mp != null)
                {
                    Type? t = SafeGetType(mp, "Microsoft.Phone.Controls." + local)
                              ?? SafeGetType(mp, "Microsoft.Phone.Shell." + local);
                    if (t != null) return t;
                }
            }

            if (xmlns.StartsWith("clr-namespace:", StringComparison.Ordinal))
            {
                (string ns, string? asmName) = ParseClrNamespace(xmlns);

                // Apply WPR redirect FIRST for namespaces we shim. This ensures we always use
                // our types instead of accidentally resolving to a stub/forwarder in a patched
                // user assembly that still claims to live in the original namespace.
                //
                // Two namespaces to try in the redirect-target assembly:
                //   1. The redirect's nominated namespace (e.g. "WPR.SilverlightCompability"
                //      for the System.Windows.* shims, or "Microsoft.Phone.Controls" for the
                //      gesture/PhoneApplication* types that now live in the Microsoft.Phone asm).
                //   2. The XAML's original namespace verbatim.
                // First match wins; second-place existence in a different assembly (the
                // user-bundled Microsoft.Phone.Controls.Toolkit.dll, which ships its own
                // copy of GestureEventArgs etc.) must not steal the lookup — that's exactly
                // why the Microsoft.Phone.Controls redirect points at OUR Microsoft.Phone asm.
                if (ClrNsRedirects.TryGetValue(ns, out var redir))
                {
                    Assembly? ra = FindRedirectAssembly(redir.AssemblyName);
                    if (ra != null)
                    {
                        Type? t = SafeGetType(ra, redir.Namespace + "." + local)
                                  ?? SafeGetType(ra, ns + "." + local);
                        if (t != null) return t;
                    }
                }

                // Try the literal namespace + assembly. CRITICAL: prefer assemblies
                // in the SAME ALC as the user assembly. A previous-launch's ALC may
                // still be alive (collectible ALCs unload lazily — GC has to drop
                // all roots first), and AppDomain.GetAssemblies() includes them.
                // If we pick an old-ALC assembly here, the user's
                // <c>(Panorama)FindName("panorama")</c> cast in their auto-generated
                // InitializeComponent will throw <c>InvalidCastException</c>: the
                // type identity is per-ALC, so [A]Panorama from old ALC ≠ [B]Panorama
                // from current ALC even though they're the same .dll on disk.
                if (asmName != null)
                {
                    Assembly? asm = EnumerateRelevantAssemblies()
                        .FirstOrDefault(a => string.Equals(a.GetName().Name, asmName, StringComparison.Ordinal));
                    if (asm != null)
                    {
                        Type? t = SafeGetType(asm, ns + "." + local);
                        if (t != null) return t;
                    }
                }

                // Fall back: try every relevant assembly for the namespace.
                foreach (Assembly a in EnumerateRelevantAssemblies())
                {
                    Type? t = SafeGetType(a, ns + "." + local);
                    if (t != null) return t;
                }
            }

            throw new XamlParseException($"Cannot resolve type '{{{xmlns}}}{local}'");
        }

        /// <summary>
        /// Enumerate assemblies relevant to the current launch's XAML resolution:
        /// the user assembly's AssemblyLoadContext (which holds the freshly-loaded
        /// Microsoft.Phone.Controls.dll and friends for this run), plus the
        /// Default ALC (where our shim assemblies and the BCL live).
        ///
        /// Falls back to <c>AppDomain.GetAssemblies()</c> when no user assembly is
        /// registered (rare — only happens if XAML is parsed before
        /// SilverlightAppHost.Boot has stashed it).
        /// </summary>
        /// <summary>
        /// Resolve a shim assembly by simple name for a XAML type redirect. The SL shim is this
        /// very assembly, returned directly (fast and ALC-correct). Microsoft.Phone is looked up
        /// among the relevant (user + Default ALC) assemblies, with an explicit load-by-name
        /// fallback in case no XAML has forced it to load yet.
        /// </summary>
        private static Assembly? FindRedirectAssembly(string name)
        {
            if (string.Equals(name, typeof(FrameworkElement).Assembly.GetName().Name, StringComparison.Ordinal))
                return typeof(FrameworkElement).Assembly;

            foreach (Assembly a in EnumerateRelevantAssemblies())
                if (string.Equals(a.GetName().Name, name, StringComparison.Ordinal))
                    return a;

            try { return Assembly.Load(new AssemblyName(name)); }
            catch { return null; }
        }

        private static IEnumerable<Assembly> EnumerateRelevantAssemblies()
        {
            Assembly? userAsm = HostContext.UserAssembly;
            if (userAsm == null)
            {
                foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                    yield return a;
                yield break;
            }

            var userAlc = System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(userAsm);
            var defaultAlc = System.Runtime.Loader.AssemblyLoadContext.Default;
            var seen = new System.Collections.Generic.HashSet<Assembly>();

            if (userAlc != null)
            {
                foreach (Assembly a in userAlc.Assemblies)
                    if (seen.Add(a)) yield return a;
            }
            if (defaultAlc != userAlc)
            {
                foreach (Assembly a in defaultAlc.Assemblies)
                    if (seen.Add(a)) yield return a;
            }
        }

        /// <summary>
        /// <see cref="Assembly.GetType(string, bool)"/> with <c>throwOnError:false</c>
        /// is not actually exception-free. If a loaded assembly contains a TypeForwardedTo
        /// attribute pointing at a target type that doesn't resolve (the .NET 4 Silverlight
        /// <c>System.Windows</c> assembly forwarding <c>System.Windows.Data.IValueConverter</c>
        /// to a target that doesn't exist on this runtime, for example), <c>GetType</c> will
        /// raise <see cref="TypeLoadException"/> regardless of the flag. <c>FileNotFoundException</c>
        /// fires for forwarders to a missing dependent assembly, and <c>BadImageFormatException</c>
        /// for malformed metadata. Probing across the entire AppDomain means we can't trust
        /// any single assembly to be well-formed — wrap once here and treat any failure as
        /// "this assembly doesn't have what I asked for, move on".
        /// </summary>
        private static Type? SafeGetType(Assembly a, string fullName)
        {
            try
            {
                return a.GetType(fullName, throwOnError: false);
            }
            catch (TypeLoadException) { return null; }
            catch (FileNotFoundException) { return null; }
            catch (BadImageFormatException) { return null; }
        }

        private static (string Namespace, string? Assembly) ParseClrNamespace(string xmlns)
        {
            // "clr-namespace:Microsoft.Phone.Controls;assembly=Microsoft.Phone"
            string body = xmlns.Substring("clr-namespace:".Length);
            int semi = body.IndexOf(';');
            if (semi < 0) return (body.Trim(), null);

            string ns = body.Substring(0, semi).Trim();
            string remainder = body.Substring(semi + 1).Trim();
            const string AsmKey = "assembly=";
            string? asm = remainder.StartsWith(AsmKey, StringComparison.Ordinal)
                ? remainder.Substring(AsmKey.Length).Trim()
                : null;
            return (ns, asm);
        }
    }
}
