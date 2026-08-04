// The WP control types the tests exercise (PhoneApplicationFrame/Page/FrameView, gestures)
// moved from WPR.SilverlightCompability into the Microsoft.Phone assembly under their real
// namespaces. The test files sit in namespace WPR.SilverlightCompability.Tests and named those
// types unqualified via the enclosing namespace; these global usings keep that working without
// editing every test file.
global using Microsoft.Phone.Controls;
global using Microsoft.Phone.Shell;
