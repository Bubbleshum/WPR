// System.Windows.Media.Imaging "imitation"

using System;

using SlImageSource = WPR.SilverlightCompability.ImageSource;

namespace WPR.WindowsCompability
{
    // projection: System.Windows.Media.Imaging.BitmapSource. Inherits from
    // WPR.SilverlightCompability.ImageSource so instances can be assigned to
    // Image.Source (which is typed ImageSource). Mirrors Silverlight's chain:
    //   ImageSource <- BitmapSource <- BitmapImage / WriteableBitmap.
    public class BitmapSource : SlImageSource
    {
        public BitmapSource()
        {
        }

        // Lets BitmapImage(Uri) seed the inherited Path field through the public
        // ImageSource(string) constructor, since Path itself is internal to the
        // SilverlightCompability assembly.
        protected BitmapSource(string path) : base(path)
        {
        }

        //static 
        //BitmapSource(int ActualWidth, int ActualHeight)
        //{
        /*
        writeableBitmap = new WriteableBitmap(
            (int)ActualWidth,
            (int)ActualHeight,
            96,
            96,
            default,//PixelFormats.Bgr32,
            null);
        */
        //    return;
        //}

        //RnD
        //public static void SetSource(System.IO.Stream stream)
        //{       
        //TODO
        //   return;
        //}

        //public static void SetSource()
        //{
        //    //TODO
        //    return;
        //}

        public void SetSource(System.IO.Stream stream)
        {
            //BitmapImage bit =
            //new BitmapImage(new Uri("/Resources/1.jpg", UriKind.Relative));
            //img.Source = bit;
            //TODO

            //Image image = new Image
            //{
            //    Source = ImageSource.FromFile("forest.png")
            //};

            //Content = image;
            //return;
        }

        public Int32 get_PixelWidth()
        {
            return (Int32)1; //RnD
        }

        public Int32 get_PixelHeight()
        {
            return (Int32)1; //RnD
        }

    }//BitmapSource

}

