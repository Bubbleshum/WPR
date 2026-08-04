using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("WPR.SilverlightCompability.Tests")]
// The Microsoft.Phone assembly hosts PhoneApplicationFrameView (an Avalonia adapter) and the
// PhoneApplication*/gesture types, which use SL internals (SilverlightRenderer.ConvertBrush,
// HitTester, UIElement.MeasureInvalidatedEvent, ContentControl.Presenter, Popup.IsEffectivelyOpen).
[assembly: InternalsVisibleTo("Microsoft.Phone")]
