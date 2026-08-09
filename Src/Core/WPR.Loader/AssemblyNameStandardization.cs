using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace WPR
{
    // Made public in the Stage 2 WPR.Runtime/WPR.Loader split: this type lives in
    // WPR.Loader but is called from WPR.Runtime (ApplicationLaunch, SilverlightAppHost).
    public class AssemblyNameStandardization
    {
        public static String Process(String previous)
        {
            return new Regex("[*'\",_&#^@!]").Replace(previous, "_");
        }
    }
}
