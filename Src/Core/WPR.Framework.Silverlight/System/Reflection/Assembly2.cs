using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

namespace WPR.WindowsCompability
{
    /// <summary>
    /// Target of every patched <c>Assembly.Load(string)</c> call. On the phone every assembly of
    /// the XAP lived in one domain, so <c>Assembly.Load("CutFruit")</c> from a library handed back
    /// the loaded entry assembly. Under WPR the game's main assembly sits in a collectible load
    /// context and its sibling libraries in the Default one, and the runtime refuses to bind from
    /// the Default context into a collectible one: <c>FileLoadException: Operation is not
    /// supported (0x80131515)</c>.
    ///
    /// <para>Microsoft's ad SDK hits exactly that in every title that ships it:
    /// <c>Microsoft.Advertising.AdManager</c>'s type initializer does
    /// <c>Assembly.Load(Deployment.Current.EntryPointAssembly)</c>, and the failure makes every
    /// later ad call throw <c>TypeInitializationException</c> (AE Fruit Slash).</para>
    ///
    /// <para>So an assembly already loaded anywhere is returned as it is, found by simple name the
    /// way <see cref="Type2"/> finds the assemblies a type name refers to (the caller's context
    /// first, then every context, XNA names to WPR.Framework.Xna). Anything else goes to the real
    /// <see cref="Assembly.Load(string)"/>, so a missing assembly still throws as it did.</para>
    /// </summary>
    public static class Assembly2
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static Assembly Load(string assemblyString)
        {
            if (assemblyString == null)
            {
                throw new ArgumentNullException(nameof(assemblyString));
            }

            AssemblyLoadContext? callerAlc = null;
            try
            {
                callerAlc = AssemblyLoadContext.GetLoadContext(Assembly.GetCallingAssembly());
            }
            catch { /* the unordered scan below still runs */ }

            AssemblyName? name = null;
            try
            {
                name = new AssemblyName(assemblyString);
            }
            catch { /* malformed: let the runtime report it */ }

            if (name != null && Type2.FindLoaded(name, callerAlc) is { } loaded)
            {
                return loaded;
            }

            return Assembly.Load(assemblyString);
        }
    }
}
