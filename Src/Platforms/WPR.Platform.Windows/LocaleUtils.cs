using System;

namespace WPR.Platform.Windows
{
    public static class LocaleUtils
    {
        public static string GetDisplayName(this Enum e)
        {
            var resourceDisplayName = Properties.Resources.ResourceManager.GetString(e.GetType().Name + "_" + e);
            return string.IsNullOrWhiteSpace(resourceDisplayName) ? string.Format("[[{0}]]", e) :
                resourceDisplayName;
        }
    }
}
