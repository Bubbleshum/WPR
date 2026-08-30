// System.Windows.Media.Imaging "imitation"

using System;
using System.Windows;
//using System.Windows.Controls;
//using System.Windows.Media;
//using System.Windows.Media.Imaging;
using System.Windows.Input;

namespace WPR.WindowsCompability
{
    // projection: System.Windows.Media.Imaging.WriteableBitmap. In Silverlight
    // this derives from BitmapSource (-> ImageSource) — we mirror the chain so
    // Image.Source accepts a WriteableBitmap.
    public class WriteableBitmap : BitmapSource
    {

        Int32 ImgActualWidth;
        Int32 ImgActualHeight;


        public WriteableBitmap(Int32 ActualWidth, Int32 ActualHeight)
        {
            /*
            writeableBitmap = new WriteableBitmap(
                (int)ActualWidth,
                (int)ActualHeight,
                96,
                96,
                default,//PixelFormats.Bgr32,
                null);
            */
            
            ImgActualWidth = ActualWidth;
            ImgActualHeight = ActualHeight;
        }

        /// <summary>
        /// Copy-construct from an existing bitmap. Silverlight used this to snapshot a
        /// BitmapSource into a writable surface; WPR's BitmapSource carries no pixels yet, so
        /// this only inherits the dimensions. Kinectimals' <c>MediaUtils.MediaImage.CreateBitmap</c>
        /// takes this path, and without the overload it is a MissingMethodException.
        /// </summary>
        public WriteableBitmap(BitmapSource source)
            : this(source?.get_PixelWidth() ?? 0, source?.get_PixelHeight() ?? 0)
        {
        }

        public void Invalidate()
        {
            return;
        }

        public Int32[] get_Pixels()
        {
            int stride = ImgActualWidth;// * 4;
            int size = ImgActualHeight;// * stride;
            Int32[] pixels = new Int32[size];
            //img.CopyPixels(pixels, stride, 0);
            return pixels; //RnD
        }

    }
}
