using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Microsoft.Phone.Shell
{
    public class StandardTileData
    {
        public string? Title { get; set; }
        public Uri? BackgroundImage { get; set; }
        // WP7's StandardTileData.Count is Nullable<int>, not plain int. Tentacles' live-tile
        // updater calls set_Count(int?) every frame from a component Update; with the wrong
        // signature it tripped MissingMethodException and was logged as
        //   [ex] ComponentsUpdate ex.: Method not found:
        //     'Void Microsoft.Phone.Shell.StandardTileData.set_Count(System.Nullable`1<Int32>)'
        // — that error fired ~once per frame on the main menu.
        public int? Count { get; set; }
        public string? BackContent { get; set; }
        public Uri? BackBackgroundImage { get; set; }
        public string? BackTitle { get; set; }
    }
}
