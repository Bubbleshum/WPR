using Avalonia.Controls;
using Avalonia.Controls.Templates;
using System;
using System.Collections.Generic;
using System.Reflection;
using WPR.Platform.Windows.ViewModels;

namespace WPR.Platform.Windows
{
    public class ViewLocator : IDataTemplate
    {
        // Assemblies searched for a view type, in order. Seeded with this assembly
        // (where the shared views live). Platform heads add their own so views they
        // own -- the desktop window, the mobile shell -- are reachable from here:
        // Type.GetType(string) only ever searches the calling assembly and corelib,
        // so without registration a view in WPR.Platform.* would silently resolve to
        // the "Not Found" TextBlock below rather than failing the build.
        private static readonly List<Assembly> _ViewAssemblies = new() { typeof(ViewLocator).Assembly };

        /// <summary>
        /// Register an assembly to search for view types. Call once from the platform
        /// head's startup, before the first view is resolved. Registering the same
        /// assembly twice is a no-op.
        /// </summary>
        public static void RegisterViewAssembly(Assembly assembly)
        {
            if (assembly == null)
            {
                return;
            }

            lock (_ViewAssemblies)
            {
                if (!_ViewAssemblies.Contains(assembly))
                {
                    _ViewAssemblies.Add(assembly);
                }
            }
        }

        public Control Build(object data)
        {
            // Note: this convention ("...ViewModels.FooViewModel" -> "...Views.FooView",
            // the "s" of "ViewModels" surviving as the "s" of "Views") does not currently
            // match any view in the repo -- every view/VM pairing is an explicit
            // <DataTemplate DataType="vm:..."> inside the page that uses it, so this
            // locator resolves nothing today. Left as-is deliberately: changing the
            // convention would alter runtime behaviour, not just structure.
            var name = data.GetType().FullName!.Replace("ViewModel", "View");
            var type = FindViewType(name);

            if (type != null)
            {
                return (Control)Activator.CreateInstance(type)!;
            }
            else
            {
                return new TextBlock { Text = "Not Found: " + name };
            }
        }

        private static Type? FindViewType(string name)
        {
            Assembly[] assemblies;
            lock (_ViewAssemblies)
            {
                assemblies = _ViewAssemblies.ToArray();
            }

            foreach (var assembly in assemblies)
            {
                var type = assembly.GetType(name);
                if (type != null)
                {
                    return type;
                }
            }

            // Last resort: an assembly-qualified name, or a type in an assembly nobody
            // registered but which is already loaded.
            return Type.GetType(name);
        }

        public bool Match(object data)
        {
            return data is ViewModelBase;
        }
    }
}
