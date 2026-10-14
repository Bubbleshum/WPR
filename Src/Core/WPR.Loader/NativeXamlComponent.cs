using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml;

namespace WPR
{
    /// <summary>
    /// Finds the native game inside a WP8 Direct3D/XAML title: a XAP whose manifest says
    /// <c>RuntimeType="Silverlight"</c> but whose game is an ARM WinRT component DLL, driven by a
    /// thin managed page through <c>DrawingSurfaceBackgroundGrid</c>.
    /// </summary>
    /// <remarks>
    /// <para>These used to install as Silverlight, where the component was replaced by a managed
    /// stub and the page painted a placeholder: the game never ran on any platform. They are now
    /// installed as <see cref="Models.ApplicationType.ModernNative"/> with the component as their
    /// image, and <c>WPR.Engine.Wp8Native</c> hosts them the way it hosts an exe title, playing
    /// the page's part from a description of it (<c>XamlShells</c>). A title with no description
    /// yet fails at launch with that reason, which is more honest than the placeholder was.</para>
    /// <para>The component is the first <c>InProcessServer</c> the manifest registers whose DLL is
    /// native ARM (machine 0x01C4, no CLR header), skipping <c>Microsoft.Xbox.dll</c> - Xbox Live's
    /// own component, present in many managed titles too. Unity titles (<c>UnityPlayer.dll</c>) are
    /// left alone: their engine is not hosted this way.</para>
    /// </remarks>
    public static class NativeXamlComponent
    {
        public static string? Find(ZipArchive archive, XmlNode appNode)
        {
            if (archive.GetEntry("UnityPlayer.dll") != null)
            {
                return null;
            }

            XmlNodeList? paths = appNode.OwnerDocument?.SelectNodes("//ActivatableClasses/InProcessServer/Path");
            if (paths == null)
            {
                return null;
            }

            // A title may register several native components - Modern Combat 4 lists Gameloft's
            // promotions library (IGPLib.dll) and a push-notification one ahead of its game. The
            // game is the one that draws: the first that imports d3d11.dll, or whose metadata
            // declares CreateContentProvider (the page hands that to DrawingSurfaceBackgroundGrid),
            // else the first found. Assassin's Creed Pirates needs the second test: its SparkApp.dll
            // imports no d3d11 (its renderer is a Lua*.dll loaded at run time from Spark2Projects),
            // and the Moga controller library it registers first is native ARM too.
            string? first = null;
            foreach (XmlNode node in paths)
            {
                string path = node.InnerText.Trim();
                if (path.Length == 0 || IsMiddleware(path))
                {
                    continue;
                }

                ZipArchiveEntry? entry = archive.GetEntry(path)
                    ?? archive.Entries.FirstOrDefault(e => e.FullName.Equals(path, StringComparison.OrdinalIgnoreCase));
                if (entry != null && IsNativeArm(entry, out bool draws))
                {
                    draws |= DeclaresContentProvider(archive, entry.FullName);
                    if (draws)
                    {
                        return entry.FullName;
                    }

                    first ??= entry.FullName;
                }
            }

            // No component draws. That is a Direct3D/XAML game only if the manifest says so (the
            // PhoneDirect3DXamlAppInteropToken template); otherwise it is a managed game using a
            // native helper library - What In The World? registers Lumia.Imaging.dll, Nokia's
            // image filters - and installing it as native handed the WP8 host a library with no
            // game in it, which stopped at frame 0 ("no page description"). Managed, it is
            // attempted on SilverlightGame like any Silverlight title.
            if (first != null && !DeclaresDirect3DXamlTemplate(appNode))
            {
                return null;
            }

            return first;
        }

        /// <summary>
        /// Native components that are third-party libraries a MANAGED game calls, never the game:
        /// Xbox Live's own, SharpDX's WP8 interop (Microsoft Minesweeper draws with SharpDX from
        /// C#, and SharpDX.WP8.dll imports d3d11, so it looked exactly like a Direct3D/XAML game
        /// and was handed to the WP8 host, which stopped at frame 0), and Nokia's imaging SDK.
        /// </summary>
        private static bool IsMiddleware(string path)
        {
            string name = Path.GetFileName(path);
            return name.Equals("Microsoft.Xbox.dll", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("SharpDX", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("Lumia.", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("Nokia.Graphics", StringComparison.OrdinalIgnoreCase);
        }

        private static bool DeclaresDirect3DXamlTemplate(XmlNode appNode)
        {
            string? xml = appNode.OwnerDocument?.OuterXml ?? appNode.OuterXml;
            return xml != null && xml.Contains("Direct3DXaml", StringComparison.OrdinalIgnoreCase);
        }

        private static readonly byte[] CreateContentProviderName = System.Text.Encoding.ASCII.GetBytes("CreateContentProvider");

        /// <summary>Whether the component's own .winmd (same name beside it) names CreateContentProvider.</summary>
        private static bool DeclaresContentProvider(ZipArchive archive, string dllPath)
        {
            string winmdPath = Path.ChangeExtension(dllPath, ".winmd");
            ZipArchiveEntry? winmd = archive.GetEntry(winmdPath)
                ?? archive.Entries.FirstOrDefault(e => e.FullName.Equals(winmdPath, StringComparison.OrdinalIgnoreCase));
            if (winmd == null)
            {
                return false;
            }

            try
            {
                using Stream stream = winmd.Open();
                using MemoryStream copy = new();
                stream.CopyTo(copy);
                return copy.GetBuffer().AsSpan(0, (int)copy.Length).IndexOf(CreateContentProviderName) >= 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static readonly byte[] D3D11Import = System.Text.Encoding.ASCII.GetBytes("d3d11.dll");
        private static readonly byte[] D3D11ImportUpper = System.Text.Encoding.ASCII.GetBytes("D3D11.dll");

        private static bool IsNativeArm(ZipArchiveEntry entry, out bool importsD3D11)
        {
            importsD3D11 = false;
            try
            {
                using Stream stream = entry.Open();
                using MemoryStream copy = new();
                stream.CopyTo(copy);
                byte[] d = copy.GetBuffer();
                if (copy.Length < 0x200 || d[0] != (byte)'M' || d[1] != (byte)'Z')
                {
                    return false;
                }

                int pe = BitConverter.ToInt32(d, 0x3C);
                if (pe <= 0 || pe + 24 + 96 + 15 * 8 > copy.Length || BitConverter.ToUInt32(d, pe) != 0x4550)
                {
                    return false;
                }

                ushort machine = BitConverter.ToUInt16(d, pe + 4);
                uint clrHeader = BitConverter.ToUInt32(d, pe + 24 + 96 + 14 * 8);
                if (machine != 0x01C4 || clrHeader != 0)
                {
                    return false;
                }

                ReadOnlySpan<byte> image = d.AsSpan(0, (int)copy.Length);
                importsD3D11 = image.IndexOf(D3D11Import) >= 0 || image.IndexOf(D3D11ImportUpper) >= 0;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
