using Mono.Cecil;
using Mono.Cecil.Cil;
using System.Xml.Serialization;
using Mono.Cecil.Rocks;

using System;
using System.IO;
using System.Collections.Generic;
using System.Threading;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Diagnostics;
using WPR.Common;

namespace WPR
{
    public class ApplicationPatcher
    {
        public static int Version => 1;

        private AssemblyNameReference FNACompRef;
        private AssemblyNameReference FNARef;
        private AssemblyNameReference SystemRunTimeRef;

        private AssemblyNameReference WindowsCompRef;
        private AssemblyNameReference SilverlightCompRef;
        private AssemblyNameReference MicrosoftPhoneRef;

        private AssemblyNameReference StandardCompRef;
        private AssemblyNameReference ServiceModelPrimitivesRef;
        private AssemblyNameReference ServiceModelHTTPRef;
        private AssemblyNameReference GamerServicesCompRef;
        //private AssemblyNameReference SystemSecurityCryptographyRef; //!
        //private AssemblyNameReference SystemWindowsMediaImagingRef; //!

        private class TypePatchInfo
        {
            public String? NewName;
            public String? NewNamespace;
            public AssemblyNameReference? Reference;
        }

        private Dictionary<string, TypePatchInfo> Patches;
        private Dictionary<string, Type> MemberPatches;

        public ApplicationPatcher()
        {
            FNARef = AssemblyNameReference.Parse("FNA");
            FNACompRef = AssemblyNameReference.Parse("WPR.XnaCompability");
            SystemRunTimeRef = AssemblyNameReference.Parse("System.Runtime");
            WindowsCompRef = AssemblyNameReference.Parse("WPR.WindowsCompability");
            SilverlightCompRef = AssemblyNameReference.Parse("WPR.SilverlightCompability");
            MicrosoftPhoneRef = AssemblyNameReference.Parse("Microsoft.Phone");

            ServiceModelPrimitivesRef = AssemblyNameReference.Parse("System.ServiceModel.Primitives");
            ServiceModelHTTPRef = AssemblyNameReference.Parse("System.ServiceModel.Http");

            StandardCompRef = AssemblyNameReference.Parse("WPR.StandardCompability");

            // The shim for GamerServicesComponent lives in our managed assembly
            // Microsoft.Xna.Framework.GamerServices (Src/Core/...). The user
            // assembly's GamerServicesComponent type refs must be rescoped to it,
            // otherwise they stay bound to the WP7 assembly
            // Microsoft.Xna.Framework.GamerServicesExtensions, which doesn't exist
            // at runtime -> FileNotFoundException aborts game init (black screen).
            GamerServicesCompRef =
                AssemblyNameReference.Parse("Microsoft.Xna.Framework.GamerServices");

            //SystemSecurityCryptographyRef = AssemblyNameReference.Parse("WPR.WindowsCompability");
            //SystemWindowsMediaImagingRef =  AssemblyNameReference.Parse("WPR.WindowsCompability");

            // *** Patches ***
            Patches = new Dictionary<string, TypePatchInfo>()
            {
                { "System.Diagnostics.Stopwatch", new TypePatchInfo()
                {
                    Reference = SystemRunTimeRef
                }
                },
                { "Microsoft.Xna.Framework.GraphicsDeviceManager", new TypePatchInfo()
                {
                    NewName = "GraphicsDeviceManager2",
                    NewNamespace = "WPR.XnaCompability",
                    Reference = FNACompRef
                }
                },
                { "System.Windows.Application", new TypePatchInfo()
                {
                    Reference = WindowsCompRef,
                    NewNamespace = "WPR.WindowsCompability"
                }
                },
                { "System.Windows.ApplicationUnhandledExceptionEventArgs", new TypePatchInfo()
                {
                    Reference = WindowsCompRef,
                    NewNamespace = "WPR.WindowsCompability"
                }
                },
                { "System.IO.IsolatedStorage.IsolatedStorageSettings", new TypePatchInfo()
                {
                    Reference = WindowsCompRef,
                    NewName="IsolatedStorageSettings2", //RnD
                    NewNamespace = "WPR.WindowsCompability"
                }
                },
                { "Microsoft.Xna.Framework.Media.MediaSource", new TypePatchInfo()
                {
                    Reference = FNACompRef,
                    NewNamespace = "WPR.XnaCompability.Media"
                }
                },
                { "Microsoft.Xna.Framework.Media.MediaSourceType", new TypePatchInfo()
                {
                    Reference = FNACompRef,
                    NewNamespace = "WPR.XnaCompability.Media"
                }
                },
                { "Microsoft.Xna.Framework.Media.SongCollection", new TypePatchInfo()
                {
                    Reference = FNACompRef,
                    NewNamespace = "WPR.XnaCompability.Media"
                }
                },
                { "Microsoft.Xna.Framework.Media.Artist", new TypePatchInfo()
                {
                    Reference = FNACompRef,
                    NewNamespace = "WPR.XnaCompability.Media"
                }
                },
                { "Microsoft.Xna.Framework.Media.ArtistCollection", new TypePatchInfo()
                {
                    Reference = FNACompRef,
                    NewNamespace = "WPR.XnaCompability.Media"
                }
                },
                { "Microsoft.Xna.Framework.Media.Album", new TypePatchInfo()
                {
                    Reference = FNACompRef,
                    NewNamespace = "WPR.XnaCompability.Media"
                }
                },
                { "Microsoft.Xna.Framework.Media.AlbumCollection", new TypePatchInfo()
                {
                    Reference = FNACompRef,
                    NewNamespace = "WPR.XnaCompability.Media"
                }
                },
                { "Microsoft.Xna.Framework.Media.Genre", new TypePatchInfo()
                {
                    Reference = FNACompRef,
                    NewNamespace = "WPR.XnaCompability.Media"
                }
                },
                { "Microsoft.Xna.Framework.Media.MediaLibrary", new TypePatchInfo()
                {
                    Reference = FNACompRef,
                    NewNamespace = "WPR.XnaCompability.Media"
                }
                },
                { "Microsoft.Xna.Framework.Media.Picture", new TypePatchInfo()
                {
                    Reference = FNACompRef,
                    NewNamespace = "WPR.XnaCompability.Media"
                }
                },
                { "Microsoft.Xna.Framework.Media.PictureCollection", new TypePatchInfo()
                {
                    Reference = FNACompRef,
                    NewNamespace = "WPR.XnaCompability.Media"
                }
                },
                { "System.Windows.Media.SolidColorBrush", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Media.Color", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Media.Colors", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Media.Brush", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Media.ImageBrush", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Media.ImageSource", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Media.Animation.Timeline", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Media.Animation.Storyboard", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Media.Animation.TimelineCollection", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Media.Animation.DoubleAnimation", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Media.Animation.DoubleAnimationUsingKeyFrames", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Media.Animation.DoubleKeyFrame", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Media.Animation.EasingDoubleKeyFrame", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Media.Animation.LinearDoubleKeyFrame", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Media.Animation.DoubleKeyFrameCollection", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Media.Animation.KeyTime", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Media.Animation.KeyTimeType", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Media.Animation.RepeatBehavior", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Media.Animation.ClockState", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Media.Animation.FillBehavior", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Media.Animation.IEasingFunction", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                // NOTE: the WP control/shell types that used to be redirected here
                // (Microsoft.Phone.Controls.GestureService / GestureListener / *GestureEventArgs,
                // PhoneApplicationFrame, PhoneApplicationPage, and every Microsoft.Phone.Shell.*
                // lifecycle type) now live in the Microsoft.Phone assembly under their real
                // namespaces, so user IL binds them natively — no patch entry needed.

                // UriMapperBase / UriMapper / UriMapping moved the OTHER way: out of the
                // Microsoft.Phone facade into WPR.SilverlightCompability, so the SL Frame's
                // UriMapper property can reference UriMapperBase without SL depending on the
                // Microsoft.Phone assembly (which now references SL). They keep their real
                // System.Windows.Navigation namespace, so only the assembly scope is retargeted
                // (like the gesture types used to be).
                { "System.Windows.Navigation.UriMapperBase", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                }
                },
                { "System.Windows.Navigation.UriMapper", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                }
                },
                { "System.Windows.Navigation.UriMapping", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                }
                },
                // Toolkit gesture types: on real WP7 these ship in
                // Microsoft.Phone.Controls.Toolkit.dll (namespace Microsoft.Phone.Controls), so
                // user IL references them from THAT assembly — unlike PhoneApplicationPage/Shell
                // (canonically in Microsoft.Phone.dll, which now resolve natively), the gesture
                // typerefs would otherwise bind the user-bundled toolkit dll. Retarget the
                // assembly scope to our Microsoft.Phone shim (namespace unchanged).
                { "Microsoft.Phone.Controls.GestureService", new TypePatchInfo()
                {
                    Reference = MicrosoftPhoneRef,
                }
                },
                { "Microsoft.Phone.Controls.GestureListener", new TypePatchInfo()
                {
                    Reference = MicrosoftPhoneRef,
                }
                },
                { "Microsoft.Phone.Controls.GestureEventArgs", new TypePatchInfo()
                {
                    Reference = MicrosoftPhoneRef,
                }
                },
                { "Microsoft.Phone.Controls.FlickGestureEventArgs", new TypePatchInfo()
                {
                    Reference = MicrosoftPhoneRef,
                }
                },
                { "Microsoft.Phone.Controls.DragStartedGestureEventArgs", new TypePatchInfo()
                {
                    Reference = MicrosoftPhoneRef,
                }
                },
                { "Microsoft.Phone.Controls.DragDeltaGestureEventArgs", new TypePatchInfo()
                {
                    Reference = MicrosoftPhoneRef,
                }
                },
                { "Microsoft.Phone.Controls.DragCompletedGestureEventArgs", new TypePatchInfo()
                {
                    Reference = MicrosoftPhoneRef,
                }
                },
                { "System.Windows.Controls.Frame", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Navigation.NavigationService", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Navigation.NavigationMode", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Navigation.NavigationEventArgs", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Navigation.NavigatingCancelEventArgs", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Navigation.NavigationFailedEventArgs", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Navigation.NavigatedEventHandler", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Navigation.NavigatingCancelEventHandler", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Navigation.NavigationFailedEventHandler", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Navigation.NavigationStoppedEventHandler", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Navigation.JournalEntry", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Markup.XmlLanguage", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Net.HttpUtility", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.FlowDirection", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Markup.XamlReader", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Markup.XamlParseException", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Markup.ContentPropertyAttribute", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Controls.Panel", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Controls.StackPanel", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Controls.Orientation", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Controls.UIElementCollection", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Controls.Grid", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Controls.DrawingSurfaceBackgroundGrid", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Controls.ScrollViewer", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Controls.ScrollBarVisibility", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Controls.TextBox", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Controls.Control", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Controls.TextChangedEventArgs", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Input.Touch", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Input.TouchFrameEventHandler", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Input.TouchFrameEventArgs", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Input.TouchPoint", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Input.TouchPointCollection", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Input.TouchDevice", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Input.TouchAction", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Controls.ColumnDefinition", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Controls.RowDefinition", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Controls.ColumnDefinitionCollection", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Controls.RowDefinitionCollection", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Controls.TextBlock", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.TextAlignment", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.TextWrapping", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Controls.Canvas", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Controls.Primitives.Popup", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Controls.UserControl", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Controls.ContentControl", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Controls.Button", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Controls.ItemsControl", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Controls.Border", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Shapes.Shape", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Shapes.Rectangle", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.StyleTypedPropertyAttribute", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.TemplatePartAttribute", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.TemplateVisualStateAttribute", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Markup.XmlnsDefinitionAttribute", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability.Markup"
                }
                },
                { "System.Windows.Markup.XmlnsPrefixAttribute", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability.Markup"
                }
                },
                { "System.Windows.VisualState", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.VisualStateGroup", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.VisualStateManager", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.VisualTransition", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.VisualStateChangedEventArgs", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.SizeChangedEventHandler", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.SizeChangedEventArgs", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Media.CompositionTarget", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.ComponentModel.DesignerProperties", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                // Bulk: input
                { "System.Windows.Input.ManipulationStartedEventArgs", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                { "System.Windows.Input.ManipulationDeltaEventArgs", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                { "System.Windows.Input.ManipulationCompletedEventArgs", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                { "System.Windows.Input.ManipulationDelta", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                { "System.Windows.Input.ManipulationVelocities", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                { "System.Windows.Input.MouseButtonEventArgs", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                { "System.Windows.Input.KeyEventArgs", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                { "System.Windows.Input.Key", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                // Bulk: media — transforms
                { "System.Windows.Media.GeneralTransform", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                { "System.Windows.Media.Transform", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                { "System.Windows.Media.TransformCollection", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                { "System.Windows.Media.TransformGroup", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                { "System.Windows.Media.TranslateTransform", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                { "System.Windows.Media.CompositeTransform", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                { "System.Windows.Media.Projection", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                { "System.Windows.Media.PlaneProjection", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                // Bulk: media — misc
                { "System.Windows.Media.Geometry", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                { "System.Windows.Media.RectangleGeometry", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                { "System.Windows.Media.GradientBrush", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                { "System.Windows.Media.TileBrush", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                { "System.Windows.Media.AlignmentX", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                { "System.Windows.Media.AlignmentY", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                { "System.Windows.Media.CacheMode", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                { "System.Windows.Media.BitmapCache", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                { "System.Windows.Media.VisualTreeHelper", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                { "System.Windows.Media.Imaging.BitmapCreateOptions", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                // Bulk: animation easing
                { "System.Windows.Media.Animation.ExponentialEase", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                { "System.Windows.Media.Animation.QuarticEase", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                { "System.Windows.Media.Animation.EasingMode", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                // Bulk: controls
                { "System.Windows.Controls.CheckBox", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                { "System.Windows.Controls.Primitives.ToggleButton", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                { "System.Windows.Controls.Primitives.Selector", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                { "System.Windows.Controls.Page", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                { "System.Windows.Controls.ContentPresenter", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                { "System.Windows.Controls.ItemsPresenter", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                { "System.Windows.Controls.ItemCollection", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                { "System.Windows.Controls.ItemContainerGenerator", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                // Bulk: data binding helpers
                { "System.Windows.Data.IValueConverter", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                { "System.Windows.Data.BindingExpressionBase", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                { "System.Windows.Data.RelativeSource", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                { "System.Windows.Data.RelativeSourceMode", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                // Bulk: fonts
                { "System.Windows.FontWeight", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                { "System.Windows.FontWeights", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                // Bulk: misc top-level
                { "System.Windows.Style", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                { "System.Windows.PropertyPath", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                { "System.Windows.Deployment", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                { "System.Windows.PresentationFrameworkCollection`1", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                // Bulk: threading
                { "System.Windows.Threading.Dispatcher", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability.Threading" } },
                { "System.Windows.Threading.DispatcherOperation", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability.Threading" } },
                { "System.Windows.Threading.DispatcherTimer", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability.Threading" } },
                { "System.Windows.Controls.ListBox", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Controls.SelectionMode", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Controls.SelectionChangedEventArgs", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Controls.SelectionChangedEventHandler", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.RoutedEventArgs", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.RoutedEventHandler", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.ExceptionRoutedEventArgs", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Controls.Image", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Media.Stretch", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Data.Binding", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Data.BindingMode", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.DataTemplate", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Resources.StreamResourceInfo", new TypePatchInfo()
                {
                    Reference = WindowsCompRef,
                    NewNamespace = "WPR.WindowsCompability"
                }
                },
                { "System.Windows.Interop.SilverlightHost", new TypePatchInfo()
                {
                    Reference = WindowsCompRef,
                    NewNamespace = "WPR.WindowsCompability"
                }
                },
                { "System.Windows.Interop.Content", new TypePatchInfo()
                {
                    Reference = WindowsCompRef,
                    NewName = "SilverlightHostContent",
                    NewNamespace = "WPR.WindowsCompability"
                }
                },
                { "System.Windows.Interop.Settings", new TypePatchInfo()
                {
                    Reference = WindowsCompRef,
                    NewName = "SilverlightHostSettings",
                    NewNamespace = "WPR.WindowsCompability"
                }
                },
                { "System.Windows.Thickness", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.DependencyObject", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.DependencyProperty", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.PropertyMetadata", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.PropertyChangedCallback", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.DependencyPropertyChangedEventArgs", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.UIElement", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.FrameworkElement", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Visibility", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.HorizontalAlignment", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.VerticalAlignment", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.GridLength", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.GridUnitType", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.CornerRadius", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Size", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Point", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Rect", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Duration", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.ResourceDictionary", new TypePatchInfo()
                {
                    Reference = WindowsCompRef,
                    NewNamespace = "WPR.WindowsCompability"
                }
                },
                { "System.ServiceModel.XmlSerializerFormatAttribute", new TypePatchInfo()
                {
                    Reference = ServiceModelPrimitivesRef
                }
                },
                { "System.ServiceModel.BasicHttpBinding", new TypePatchInfo()
                {
                    Reference = ServiceModelHTTPRef
                }
                },
                { "System.ServiceModel.BasicHttpSecurity", new TypePatchInfo()
                {
                    Reference = ServiceModelHTTPRef
                }
                },
                { "System.ServiceModel.BasicHttpSecurityMode", new TypePatchInfo()
                {
                    Reference = ServiceModelHTTPRef
                }
                },
                //!
                { "System.Security.Cryptography.ProtectedData", new TypePatchInfo()
                {
                    Reference = WindowsCompRef,
                    //RnD : if uncomment it, WPR.WindowsCompabilityProtectedData class will be used
                    NewName = "ProtectedData",
                    NewNamespace = "WPR.WindowsCompability"
                }
                },
                //!
                { "System.Windows.Media.Imaging.BitmapImage", new TypePatchInfo()
                {
                    Reference = WindowsCompRef,
                    NewName = "BitmapImage",//RnD
                    NewNamespace = "WPR.WindowsCompability"
                }
                },
                //!
                { "System.Windows.Media.Imaging.WriteableBitmap", new TypePatchInfo()
                {
                    Reference = WindowsCompRef,
                    NewNamespace = "WPR.WindowsCompability"
                }
                },
                 //!
                { "System.Windows.Media.Imaging.BitmapSource", new TypePatchInfo()
                {
                    Reference = WindowsCompRef,
                    NewNamespace = "WPR.WindowsCompability"
                }
                },
                { "System.Windows.MessageBox", new TypePatchInfo()
                {
                    Reference = WindowsCompRef,
                    NewNamespace = "WPR.WindowsCompability"
                }
                },
                { "System.Windows.MessageBoxResult", new TypePatchInfo()
                {
                    Reference = WindowsCompRef,
                    NewNamespace = "WPR.WindowsCompability"
                }
                },
                { "System.Windows.MessageBoxButton", new TypePatchInfo()
                {
                    Reference = WindowsCompRef,
                    NewNamespace = "WPR.WindowsCompability"
                }
                }
            };

            // *** Member Patches ***
            MemberPatches = new Dictionary<string, Type>
            {
                //TODO
                //{
                //    "System.Byte[] System.......::MethodName(System.Byte[],System.Byte[])",
                //    typeof(WPR.WindowsCompability.WebServices)
                //},

                // RnD ***************************************
                //{
                //    "Microsoft.Xna.Framework.GamerServices.LeaderboardReader Microsoft.Xna.Framework.GamerServices.LeaderboardReader::Read(Microsoft.Xna.Framework.GamerServices.LeaderboardIdentity, Microsoft.Xna.Framework.GamerServices.Gamer, Int32)",
                //    typeof(Microsoft.Xna.Framework.GamerServices2.LeaderboardReader)
                //},
                {
                    "System.String Microsoft.Phone.Info.DeviceStatus::get_DeviceName()",
                    typeof(WPR.WindowsCompability.DeviceStatus)
                },
                {
                    "System.String Microsoft.Phone.Info.DeviceStatus::get_DeviceManufacturer()",
                    typeof(WPR.WindowsCompability.DeviceStatus)
                },
                // *******************************************
                {
                    "System.Boolean System.IO.IsolatedStorage.IsolatedStorageSettings::TryGetValue(System.String, ByRef)",
                    typeof(WPR.WindowsCompability.IsolatedStorageSettings2)
                },
                {
                    "System.IO.IsolatedStorage.IsolatedStorageSettings System.IO.IsolatedStorage.IsolatedStorageSettings::get_ApplicationSettings()",
                    typeof(WPR.WindowsCompability.IsolatedStorageSettings2)
                },

                // Open IsolatedStorage file streams with FileShare.ReadWrite. WP7 was single-
                // process so games never needed to share; under WPR (one process, collectible
                // ALCs) a static stream left open by a prior launch — or a second thread racing
                // an unsynchronised open — otherwise throws "being used by another process"
                // (e.g. Battleship's Profiler debug.log). Redirects the two ctors games use to a
                // subclass that adds the share flag; same store, same access otherwise.
                {
                    "System.Void System.IO.IsolatedStorage.IsolatedStorageFileStream::.ctor(System.String,System.IO.FileMode,System.IO.IsolatedStorage.IsolatedStorageFile)",
                    typeof(WPR.WindowsCompability.SharedIsolatedStorageFileStream)
                },
                {
                    "System.Void System.IO.IsolatedStorage.IsolatedStorageFileStream::.ctor(System.String,System.IO.FileMode,System.IO.FileAccess,System.IO.IsolatedStorage.IsolatedStorageFile)",
                    typeof(WPR.WindowsCompability.SharedIsolatedStorageFileStream)
                },

                {
                    "System.Byte[] System.Security.Cryptography.ProtectedData::Protect(System.Byte[],System.Byte[])",
                    typeof(WPR.WindowsCompability.ProtectedData)
                },

                {
                    "System.Byte[] System.Security.Cryptography.ProtectedData::Unprotect(System.Byte[],System.Byte[])",
                    typeof(WPR.WindowsCompability.ProtectedData)
                },
                 
                //{
                //    "System.Windows.Media.Imaging.WriteableBitmap System.Windows.Media.Imaging.WriteableBitmap(System.Integer,System.Integer)",
                //    typeof(WPR.WindowsCompability.WriteableBitmap)
                //},
                //{
                //    "System.Void System.Windows.Media.Imaging.BitmapSource::SetSource()",
                //    typeof(WPR.WindowsCompability.BitmapSource)
                //},

                {
                    "System.Type System.Type::GetType(System.String,System.Boolean)",
                    typeof(WPR.WindowsCompability.Type2)
                },
                {
                    "Microsoft.Xna.Framework.Graphics.DisplayMode Microsoft.Xna.Framework.Graphics.GraphicsDevice::get_DisplayMode()",
                    typeof(WPR.XnaCompability.Graphics.GraphicsDevice2)
                },
                {
                    "Microsoft.Xna.Framework.Graphics.DisplayMode Microsoft.Xna.Framework.Graphics.GraphicsAdapter::get_CurrentDisplayMode()",
                    typeof(WPR.XnaCompability.Graphics.GraphicsAdapter2)
                },

                {
                    "System.String System.IO.Path::GetDirectoryName(System.String)",
                    typeof(WPR.WindowsCompability.Path2)
                },
                {
                    "System.String System.IO.Path::GetFileName(System.String)",
                    typeof(WPR.WindowsCompability.Path2)
                },
                {
                    "System.String System.IO.Path::GetFileNameWithoutExtension(System.String)",
                    typeof(WPR.WindowsCompability.Path2)
                },
                {
                    "System.Void System.GC::Collect()",
                    typeof(WPR.WindowsCompability.GC2)
                },

                {
                    "System.Xml.Linq.XElement System.Xml.Linq.XElement::Load(System.String)",
                    typeof(WPR.StandardCompability.Xml.Linq.XElement2)
                },

            };

        }//ApplicationPatcher

        private void PatchRelaxedXmlNullableAttribTextSerialize(ModuleDefinition? module)
        {
            Queue<TypeDefinition> typeScanQueue = new Queue<TypeDefinition>();
            foreach (var typeDef in module!.Types)
            {
                typeScanQueue.Enqueue(typeDef);
            }

            CustomAttribute? xmlIgnoreAttrib = null;

            // Patch type for resolve XML library incompability
            while (typeScanQueue.Count != 0)
            {
                TypeDefinition type = typeScanQueue.Dequeue();

                if (type.HasNestedTypes)
                {
                    foreach (var typeNested in type.NestedTypes)
                    {
                        typeScanQueue.Enqueue(typeNested);
                    }
                }

                foreach (var field in type.Fields)
                {
                    CustomAttribute? xmlNonNullableProp = null;

                    foreach (var attrib in field.CustomAttributes)
                    {
                        if (attrib.AttributeType.FullName == typeof(XmlAttributeAttribute).FullName)
                        {
                            xmlNonNullableProp = attrib;
                            break;
                        }
                    }

                    if (xmlNonNullableProp == null)
                    {
                        continue;
                    }

                    if (field.FieldType.FullName.Contains("System.Nullable"))
                    {
                        var actualFieldType = (field.FieldType as GenericInstanceType)!.GenericArguments[0];

                        // Generate holder getter/setter
                        var getterMethod = new MethodDefinition($"get_{field.Name}SerializableHolder",
                            MethodAttributes.Public, actualFieldType);

                        var getterGen = getterMethod.Body.GetILProcessor();

                        var nullableRefTypeGeneric = module.ImportReference(
                            Type.GetType("System.Nullable`1")!);

                        var nullableRefType =
                            nullableRefTypeGeneric.MakeGenericInstanceType(new TypeReference[]
                            { actualFieldType });

                        // Emit getter
                        getterGen.Emit(OpCodes.Ldarg_0);
                        getterGen.Emit(OpCodes.Ldflda, field);
                        getterGen.Emit(OpCodes.Call, new MethodReference("get_Value",
                            nullableRefTypeGeneric.GenericParameters[0])
                        {
                            HasThis = true,
                            DeclaringType = nullableRefType
                        });

                        getterGen.Emit(OpCodes.Ret);

                        // Emit setter
                        var setterMethod = new MethodDefinition($"set_{field.Name}SerializableHolder",
                            MethodAttributes.Public, module.TypeSystem.Void)
                        {
                            Parameters = { new ParameterDefinition(actualFieldType) },
                            HasThis = true
                        };
                        var setterGen = setterMethod.Body.GetILProcessor();

                        setterGen.Emit(OpCodes.Ldarg_0);
                        setterGen.Emit(OpCodes.Ldarg_1);
                        setterGen.Emit(OpCodes.Newobj, new MethodReference(".ctor",
                            module.TypeSystem.Void, nullableRefType)
                        {
                            Parameters = { new ParameterDefinition(
                                nullableRefTypeGeneric.GenericParameters[0]) },
                            HasThis = true
                        });

                        setterGen.Emit(OpCodes.Stfld, field);
                        setterGen.Emit(OpCodes.Ret);

                        // Emit skip serialize consideration
                        var shouldSerializeMethod = new MethodDefinition(
                            $"ShouldSerialize{field.Name}SerializableHolder",
                            MethodAttributes.Public, module.TypeSystem.Boolean);

                        var shouldSerializeGen = shouldSerializeMethod.Body.GetILProcessor();

                        shouldSerializeGen.Emit(OpCodes.Ldarg_0);
                        shouldSerializeGen.Emit(OpCodes.Ldflda, field);
                        shouldSerializeGen.Emit(OpCodes.Call, new MethodReference(
                            "HasValue", module.TypeSystem.Boolean, nullableRefType)
                        {
                            HasThis = true
                        });
                        shouldSerializeGen.Emit(OpCodes.Ret);

                        type.Methods.Add(shouldSerializeMethod);
                        type.Methods.Add(getterMethod);
                        type.Methods.Add(setterMethod);

                        var propSeri = new PropertyDefinition(
                            $"{field.Name}SerializableHolder", PropertyAttributes.None, actualFieldType)
                        {
                            GetMethod = getterMethod,
                            SetMethod = setterMethod
                        };

                        type.Properties.Add(propSeri);

                        if (xmlIgnoreAttrib == null)
                        {
                            xmlIgnoreAttrib = new CustomAttribute(module.ImportReference(typeof(XmlIgnoreAttribute).
                                GetConstructor(Type.EmptyTypes)));
                        }

                        field.CustomAttributes.Remove(xmlNonNullableProp);
                        field.CustomAttributes.Add(xmlIgnoreAttrib);

                        // Add attribute if they already gave name, else we need to be creative
                        if (xmlNonNullableProp.HasConstructorArguments)
                        {
                            propSeri.CustomAttributes.Add(xmlNonNullableProp);
                        }
                        else
                        {
                            var attributeType = (xmlNonNullableProp.AttributeType.FullName
                                == typeof(XmlAttributeAttribute).FullName)
                                    ? typeof(XmlAttributeAttribute)
                                    : typeof(XmlTextAttribute);

                            MethodReference methodConstructor = module.ImportReference(attributeType
                                .GetConstructor(new Type[] { typeof(String) }));

                            propSeri.CustomAttributes.Add(new CustomAttribute(methodConstructor)
                            {
                                ConstructorArguments = {
                                    new CustomAttributeArgument(module.TypeSystem.String, field.Name) }
                            });
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Per-game IL fixups that can't be expressed as reference redirects (the
        /// <see cref="Patches"/> / <see cref="MemberPatches"/> tables only retarget type/member
        /// references — they don't rewrite a game's own method bodies). Runs after all reference
        /// patching, immediately before the module is written. Every fixup is guarded so it only
        /// touches the exact game it targets and no-ops (rather than corrupting the DLL) if the
        /// expected IL isn't present, e.g. a different build of the title.
        /// </summary>
        private static void ApplyGameSpecificFixups(ModuleDefinition module)
        {
            // Star Wars: The Battle for Hoth (SWTheBattleForHoth.dll). On a *fresh* game the
            // in-game HUD and the tutorial popups are revealed by animating the sprites in from
            // hidden via CInGameUI/CInGameHelpMode.PlayAnimationForwards, which calls the 4-arg
            // FLAnimation.play overload (animate from tick 0). Under WPR that animated reveal
            // leaves the sprites invisible, so a new game shows an empty HUD (no wave counter,
            // command-point/score panel, buttons or minimap) and blank tutorial boxes. A *resumed*
            // game is unaffected because its restore path (CInGameUI.LoadState) uses the 6-arg
            // play overload, whose startFrame maps past the animation's end so it snaps straight to
            // the final (visible) frame. This rewrites PlayAnimationForwards to use that same
            // snap-open overload so the fresh-game UI is visible too. (The reveal animation is
            // cosmetic; snapping loses the unfold flourish but restores the missing UI. Closing
            // still animates normally — PlayAnimationBackwards is untouched.)
            if (module.GetType("SWTheBattleForHoth.CInGameUI") == null) return;

            foreach (string typeName in new[] { "SWTheBattleForHoth.CInGameUI", "SWTheBattleForHoth.CInGameHelpMode" })
            {
                try
                {
                    TypeDefinition? ty = module.GetType(typeName);
                    MethodDefinition? paf = ty?.Methods.FirstOrDefault(m => m.Name == "PlayAnimationForwards" && m.HasBody);
                    if (paf == null)
                    {
                        Debug.WriteLine($"[hoth-fixup] {typeName}.PlayAnimationForwards not found — skip.");
                        continue;
                    }
                    Debug.WriteLine(SnapOpenReveal(paf)
                        ? $"[hoth-fixup] snapped {typeName}.PlayAnimationForwards to the 6-arg (snap-open) reveal."
                        : $"[hoth-fixup] IL pattern not matched in {typeName}.PlayAnimationForwards — skip.");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[hoth-fixup] {typeName} threw: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Rewrites a <c>PlayAnimationForwards</c> body from
        /// <c>group.play(startTime, playFlags, speed, notify)</c> (4-arg animate-from-hidden) to
        /// <c>group.play(startTime, 1000, -1, playFlags, speed, notify)</c> (6-arg). The 6-arg
        /// overload multiplies startFrame by ~33.3 to a tick value past the animation's authored
        /// length, so the reveal lands on its final (visible) frame immediately — the same thing
        /// LoadState does on resume. Returns false (leaving the body untouched) if the expected
        /// call sites aren't present.
        /// </summary>
        private static bool SnapOpenReveal(MethodDefinition m)
        {
            Instruction? getScreenTime = null, play4 = null;
            foreach (Instruction i in m.Body.Instructions)
            {
                if ((i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt) && i.Operand is MethodReference mr)
                {
                    if (mr.Name == "GetScreenAnimationTime") getScreenTime = i;
                    if (mr.Name == "play" && mr.Parameters.Count == 4) play4 = i;
                }
            }
            if (getScreenTime == null || play4 == null) return false;

            // Find the 6-arg play overload on the same declaring type (or a base type).
            MethodReference play4Ref = (MethodReference)play4.Operand;
            MethodDefinition? play6def = null;
            for (TypeDefinition? t = play4Ref.DeclaringType.Resolve(); t != null && play6def == null; t = t.BaseType?.Resolve())
            {
                play6def = t.Methods.FirstOrDefault(x => x.Name == "play" && x.Parameters.Count == 6);
            }
            if (play6def == null) return false;

            ILProcessor il = m.Body.GetILProcessor();
            // Insert startFrame=1000, endFrame=-1 immediately after startTime is pushed, so the
            // stack for the call becomes (group, startTime, 1000, -1, playFlags, speed, notify).
            il.InsertAfter(getScreenTime, il.Create(OpCodes.Ldc_I4, 1000));
            il.InsertAfter(getScreenTime.Next, il.Create(OpCodes.Ldc_I4_M1));
            play4.Operand = m.Module.ImportReference(play6def);
            return true;
        }

        // PatchDll(string modulePath)
        public void PatchDll(string modulePath)
        {
            // Cecil resolves type references when it *serialises* the module — most
            // notably to find the underlying integer type of an enum used as a field
            // constant (MetadataBuilder.GetConstantType -> CheckedResolve). A user
            // assembly with a field constant typed as an enum from
            // Microsoft.Xna.Framework.Graphics (rescoped to FNA below) or from one of
            // our shims therefore forces an assembly resolve at Write time. The
            // default resolver only searches the module's own directory, so it can't
            // find FNA / the WPR shim assemblies and Write throws
            // AssemblyResolutionException — which the catch below used to swallow
            // silently, leaving that one DLL unpatched (its [System.Windows] typerefs
            // never redirected -> TypeLoadException at launch). Point the resolver at
            // the install dir *and* the running WPR bin (where FNA + the shims are
            // deployed) so those resolves succeed. Repro'd on "Beards and Beaks.dll".
            var resolver = new DefaultAssemblyResolver();
            resolver.AddSearchDirectory(Path.GetDirectoryName(modulePath)!);
            resolver.AddSearchDirectory(AppContext.BaseDirectory);

            // ReadAssembly
            AssemblyDefinition assemblyData =
                Mono.Cecil.AssemblyDefinition.ReadAssembly(
                    modulePath, new ReaderParameters { AssemblyResolver = resolver });

            Mono.Cecil.ModuleDefinition module = assemblyData.MainModule;

            assemblyData.Name.Name = AssemblyNameStandardization.Process(assemblyData.Name.Name);

            string modulePathNameStandardized = Path.Combine(
                Path.GetDirectoryName(modulePath)!,
               AssemblyNameStandardization.Process(
                    Path.GetFileNameWithoutExtension(modulePath)) +
                Path.GetExtension(modulePath));

            AssemblyNameReference? xnaGameServices = null;
            //RnD
            AssemblyNameReference? xnaGameServicesExtensions = null;

            // Remove unneeded attribute (pretty sure!)
            foreach (var attrib in module.Assembly.CustomAttributes)
            {
                if (attrib.AttributeType.FullName ==
                    "System.Runtime.CompilerServices.CodeGenerationAttribute")
                {
                    module.Assembly.CustomAttributes.Remove(attrib);
                    break;
                }
            }

            // module.AssemblyReferences cycle 
            foreach (var refer in module.AssemblyReferences)
            {
                if (refer.Name.Contains("Microsoft.Xna"))
                {
                    // Test the more specific "GamerServicesExtensions" first —
                    // "GamerServicesExtensions".Contains("GamerServices") is true,
                    // so the broad check must not run before it (otherwise the
                    // Extensions branch is dead code).
                    if (refer.Name.Contains("GamerServicesExtensions"))
                    {
                        //RnD
                        xnaGameServicesExtensions = refer;
                    }
                    else if (refer.Name.Contains("GamerServices"))
                    {
                        xnaGameServices = refer;
                    }
                    else
                    {
                        refer.Name = FNARef.Name;
                        refer.Version = FNARef.Version;
                        refer.PublicKey = FNARef.PublicKey;
                    }
                }
                else if (refer.Name.Equals("mscorlib.Extensions",
                    StringComparison.OrdinalIgnoreCase))
                {
                    refer.Name = SystemRunTimeRef.Name;
                    refer.Version = SystemRunTimeRef.Version;
                    refer.PublicKey = SystemRunTimeRef.PublicKey;
                }
                else if (refer.Name.Equals("System.ServiceModel",
                    StringComparison.OrdinalIgnoreCase))
                {
                    refer.Name = ServiceModelPrimitivesRef.Name;
                    refer.Version = ServiceModelPrimitivesRef.Version;
                    refer.PublicKey = ServiceModelPrimitivesRef.PublicKey;
                }
            }

            //RnD
            PatchRelaxedXmlNullableAttribTextSerialize(module);

            // Add AssemblyReferences
            module.AssemblyReferences.Add(FNACompRef);
            module.AssemblyReferences.Add(WindowsCompRef);
            module.AssemblyReferences.Add(SilverlightCompRef);
            module.AssemblyReferences.Add(SystemRunTimeRef);
            module.AssemblyReferences.Add(ServiceModelPrimitivesRef);
            module.AssemblyReferences.Add(ServiceModelHTTPRef);
            module.AssemblyReferences.Add(StandardCompRef);
            module.AssemblyReferences.Add(GamerServicesCompRef);
            //module.AssemblyReferences.Add(SystemSecurityCryptographyRef);//!
            //module.AssemblyReferences.Add(SystemWindowsMediaImagingRef);//

            // create Ref. Patch Cache
            Dictionary<string, TypeReference> typeRefPatchCache
                = new Dictionary<string, TypeReference>();

            // module.GetMemberReferences cycle
            foreach (var memberRef in module.GetMemberReferences())
            {
                //if (memberRef.FullName.Contains("Collect"))
                //{
                //    Debug.WriteLine("[Collect] memberRef fullname: "
                //        + memberRef.FullName);
                //}

                foreach (var patch in MemberPatches)
                {
                    /*
                    if (memberRef.FullName.Contains("Collect"))
                    {
                        //Debug.WriteLine("[TeSTING] memberRef.FullName.Contains : Collect");
                        Debug.WriteLine("[TeSTING] memberRef.FullName Contains Collect: " 
                            + memberRef.FullName);
                    }
                    */

                    if (memberRef.FullName == patch.Key)
                    {
                        if (typeRefPatchCache.ContainsKey(patch.Value.FullName!))
                        {
                            memberRef.DeclaringType = typeRefPatchCache[patch.Value.FullName!];
                        }
                        else
                        {
                            memberRef.DeclaringType = module.ImportReference(patch.Value);
                            typeRefPatchCache.Add(patch.Value.FullName!, memberRef.DeclaringType);
                        }
                    }
                }
            }

            // cycle existing refs...
            foreach (var existingRef in module.GetTypeReferences())
            {
                existingRef.Name = AssemblyNameStandardization.Process(existingRef.Name);

                if (existingRef.FullName
                    == "Microsoft.Xna.Framework.GamerServices.GamerServicesComponent")
                {
                    // Bind to our shim assembly directly. The captured WP7 ref
                    // (xnaGameServices) may be absent or point at the missing
                    // GamerServicesExtensions assembly, so don't rely on it.
                    existingRef.Scope = GamerServicesCompRef;
                }
                else if (existingRef.FullName
                    == "Microsoft.Xna.Framework.GamerServicesExtensions.GamerServicesComponent")
                {
                    //RnD
                    existingRef.Scope = GamerServicesCompRef;
                }
                else
                {
                    if (Patches.ContainsKey(existingRef.FullName))
                    {
                        TypePatchInfo patch = Patches[existingRef.FullName];
                        if (patch != null)
                        {
                            if (patch.NewName != null)
                            {
                                existingRef.Name = patch.NewName;
                            }

                            if (patch.NewNamespace != null)
                            {
                                existingRef.Namespace = patch.NewNamespace;
                            }

                            if (patch.Reference != null)
                            {
                                existingRef.Scope = patch.Reference;
                            }
                        }
                    }
                }
            }//for...


            // Game-specific IL fixups that don't fit the reference-redirect tables above.
            ApplyGameSpecificFixups(module);

            // create .dll.new
            try
            {
                assemblyData.Write(modulePath + ".new");
            }
            catch (Exception ex)
            {
                // With the resolver supplied above this should be rare, but if a
                // constant still references a genuinely unresolvable assembly Cecil
                // throws here. Log it loudly — a bare Debug.WriteLine never reached
                // the install log, which is exactly how an unpatched DLL slipped
                // through unnoticed before. Leave the original in place.
                Log.Error(LogCategory.AppInstall,
                    $"Cecil failed to write patched assembly '{modulePath}'. It will be left UNPATCHED. Error:\n{ex}");

                assemblyData.Dispose();

                // Drop the truncated/empty .new so a stale 0-byte file can't linger
                // or be mistaken for a real patched output.
                try { File.Delete(modulePath + ".new"); } catch { /* best-effort */ }
                return;
            }

            assemblyData.Dispose();

            // .dll -> .dll.original
            File.Move(modulePath, modulePathNameStandardized + ".original", true);

            // .dll.new - > .dll
            File.Move(modulePath + ".new", modulePathNameStandardized, true);
        }//PatchDll

        public void Patch(string appRootPath, Action<int> progress, CancellationToken token)
        {
            List<string> filenameList = Directory.EnumerateFiles(appRootPath,
                "*.dll", SearchOption.AllDirectories).ToList();
            int totalCount = filenameList.Count;
            int current = 0;

            foreach (var filename in filenameList)
            {
                if (token.IsCancellationRequested)
                {
                    return;
                }

                try
                {
                    PatchDll(filename);
                    Debug.WriteLine($"[i] Patching DLL with path: {filename}.\n");
                }
                catch (Exception ex)
                {
                    Log.Error(LogCategory.AppInstall, $"Fail to patch DLL with path: {filename}. Error:\n{ex}");
                    continue;
                }

                current++;
                progress((int)(current * 100.0 / totalCount));
            }

            // Post-patch cleanup: delete bundled DLLs that contribute nothing
            // useful after typeref retargeting. Real WP toolkits ship copies of
            // System.Windows.Interactivity / Microsoft.Expression.Interactions /
            // Microsoft.Advertising.Mobile that the user game references in
            // metadata but never actually exercises. Removing them eliminates
            // duplicate type definitions (a common source of cross-assembly
            // type-identity bugs — see GestureEventArgs) and shaves install
            // size. The cleanup is conservative: it removes a DLL only when
            // nothing else in the install dir (post-patch) still imports its
            // assembly name as a reference.
            try { CleanupUnreferencedBundledDlls(appRootPath); }
            catch (Exception ex)
            {
                Debug.WriteLine($"[i] CleanupUnreferencedBundledDlls failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Iteratively remove DLLs that no longer have inbound references from any
        /// other DLL in <paramref name="appRootPath"/>. The user's main assembly
        /// (the entry point) is treated as a permanent root. We loop until no
        /// further DLLs can be pruned — handles transitive deletions
        /// (e.g. Interactions referenced only by Expression, both then go).
        /// </summary>
        private static void CleanupUnreferencedBundledDlls(string appRootPath)
        {
            // Names of DLLs we'd consider stripping (anything that isn't a WPR
            // shim and isn't the user's primary assembly. The user's assembly is
            // identified loosely: keep every DLL whose presence the patcher
            // explicitly touched at least once — for simplicity, "anything not
            // in the strip-candidate list below stays").
            // We start conservatively with a known-safe candidate set; can grow
            // it later as we shim more types.
            var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Microsoft.Expression.Interactions",
                "System.Windows.Interactivity",
            };

            // Loop: each pass removes any candidate whose name appears in no
            // remaining DLL's AssemblyReferences.
            bool changed;
            do
            {
                changed = false;
                string[] dlls = Directory.GetFiles(appRootPath, "*.dll", SearchOption.AllDirectories);

                // Build inbound-reference set: which assembly names are still imported by SOMEONE?
                var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string dll in dlls)
                {
                    AssemblyDefinition? asm = null;
                    try { asm = AssemblyDefinition.ReadAssembly(dll); }
                    catch { continue; }
                    using (asm)
                    {
                        foreach (var r in asm.MainModule.AssemblyReferences)
                            referenced.Add(r.Name);
                    }
                }

                foreach (string dll in dlls)
                {
                    string name;
                    AssemblyDefinition? asm = null;
                    try { asm = AssemblyDefinition.ReadAssembly(dll); name = asm.Name.Name; }
                    catch { asm?.Dispose(); continue; }
                    asm.Dispose();

                    if (!candidates.Contains(name)) continue;
                    if (referenced.Contains(name)) continue; // someone still imports it; skip this pass

                    // Safe to remove: nobody imports this assembly anymore.
                    try
                    {
                        File.Delete(dll);
                        string original = dll + ".original";
                        if (File.Exists(original)) File.Delete(original);
                        Debug.WriteLine($"[i] Removed unreferenced bundled DLL: {Path.GetFileName(dll)}");
                        changed = true;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[i] Couldn't delete '{dll}': {ex.Message}");
                    }
                }
            } while (changed);
        }
    }
}
