using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("WPR.SilverlightCompability.Tests")]
// The Microsoft.Phone assembly (built from the WPR.Framework.Phone project; AssemblyName kept as
// the WP7 identity Microsoft.Phone so games bind it directly — no forwarder) hosts
// PhoneApplicationFrameView (an Avalonia adapter) and the PhoneApplication*/gesture types, which use
// SL internals (SilverlightRenderer.ConvertBrush, HitTester, UIElement.MeasureInvalidatedEvent,
// ContentControl.Presenter, Popup.IsEffectivelyOpen). IVT matches the ASSEMBLY name, not the project.
[assembly: InternalsVisibleTo("Microsoft.Phone")]
// The mixed-mode host (MixedModeGame) drives two shims that have no clock of their own and are
// pumped once per frame instead: MediaElement.PumpPending and the singleton resets both it and
// the host's Silverlight boot need at teardown. Host hooks rather than API — a WP7 MediaElement
// has no such member, so they stay internal.
[assembly: InternalsVisibleTo("WPR.Engine.GameLoop")]
// SilverlightAppHost drives the Application lifecycle from outside the type — it raises Startup
// at the point Silverlight did, between constructing the App and the first navigation. Those
// raisers are internal because they are host plumbing, not part of the API a game binds.
[assembly: InternalsVisibleTo("WPR.Runtime")]
