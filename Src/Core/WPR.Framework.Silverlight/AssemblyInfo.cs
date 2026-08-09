using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("WPR.SilverlightCompability.Tests")]
// The Microsoft.Phone assembly (built from the WPR.Framework.Phone project; AssemblyName kept as
// the WP7 identity Microsoft.Phone so games bind it directly — no forwarder) hosts
// PhoneApplicationFrameView (an Avalonia adapter) and the PhoneApplication*/gesture types, which use
// SL internals (SilverlightRenderer.ConvertBrush, HitTester, UIElement.MeasureInvalidatedEvent,
// ContentControl.Presenter, Popup.IsEffectivelyOpen). IVT matches the ASSEMBLY name, not the project.
[assembly: InternalsVisibleTo("Microsoft.Phone")]
