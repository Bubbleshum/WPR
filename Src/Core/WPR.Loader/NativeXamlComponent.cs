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

            foreach (XmlNode node in paths)
            {
                string path = node.InnerText.Trim();
                if (path.Length == 0 || path.Equals("Microsoft.Xbox.dll", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                ZipArchiveEntry? entry = archive.GetEntry(path)
                    ?? archive.Entries.FirstOrDefault(e => e.FullName.Equals(path, StringComparison.OrdinalIgnoreCase));
                if (entry != null && IsNativeArm(entry))
                {
                    return entry.FullName;
                }
            }

            return null;
        }

        private static bool IsNativeArm(ZipArchiveEntry entry)
        {
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
                return machine == 0x01C4 && clrHeader == 0;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
