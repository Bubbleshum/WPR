using System.Xml.Linq;
using System.IO;
using WPR.Common;

namespace WPR.StandardCompability.Xml.Linq
{
    public class XElement2
    {
        public static XElement Load(string path)
        {
            string normalized = (Path.DirectorySeparatorChar == '\\')
                ? path
                : path.Replace('\\', Path.DirectorySeparatorChar);

            // WP7 titles read data files (e.g. XboxLIVESettings.xml) with a bare relative
            // path. On real WP7 the working directory WAS the install root; under WPR a
            // Silverlight app runs in-process so the CWD is the host exe dir and the file
            // isn't there. If the path is relative and not found in the CWD, fall back to
            // the current game's install folder (published by the launch path). Absolute
            // paths and CWD-relative paths that already resolve keep their existing behaviour.
            if (!Path.IsPathRooted(normalized) && !File.Exists(normalized))
            {
                string? installFolder = WprHostEnvironment.CurrentInstallFolder;
                if (!string.IsNullOrEmpty(installFolder))
                {
                    string candidate = Path.Combine(installFolder, normalized);
                    if (File.Exists(candidate))
                        return XElement.Load(candidate);
                }
            }

            return XElement.Load(normalized);
        }
    }
}