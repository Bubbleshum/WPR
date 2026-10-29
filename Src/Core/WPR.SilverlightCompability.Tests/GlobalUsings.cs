// The WP control types the tests exercise (PhoneApplicationFrame/Page/FrameView, gestures)
// moved from WPR.SilverlightCompability into the Microsoft.Phone assembly under their real
// namespaces. The test files sit in namespace WPR.SilverlightCompability.Tests and named those
// types unqualified via the enclosing namespace; these global usings keep that working without
// editing every test file.
global using Microsoft.Phone.Controls;
global using Microsoft.Phone.Shell;

// The Silverlight shims declare Silverlight's own namespaces.
global using System.Windows;
global using System.Windows.Controls;
global using System.Windows.Controls.Primitives;
global using System.Windows.Data;
global using System.Windows.Documents;
global using System.Windows.Input;
global using System.Windows.Markup;
global using System.Windows.Media;
global using System.Windows.Media.Animation;
global using System.Windows.Media.Imaging;
global using System.Windows.Navigation;
global using System.Windows.Threading;
