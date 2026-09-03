#nullable enable
using Microsoft.Xna.Framework;

namespace WPR.Xna.Rhi
{
	/// <summary>
	/// One tick of a synthesised touch, produced by <see cref="IKeyboardEmulationHost"/> and
	/// written into the touch pipeline by the backend's injector.
	///
	/// <para>Carries explicit <see cref="JustPressed"/> / <see cref="JustReleased"/> edges rather
	/// than leaving the injector to diff positions, because the two channels a synthetic touch has
	/// to feed need different things and neither can infer the other:</para>
	///
	/// <list type="bullet">
	/// <item><c>TouchPanel.SetFinger</c> fills what <c>TouchPanel.GetState()</c> returns. It works
	/// out Pressed-vs-Moved itself from the previous tick, so it only needs a position.</item>
	/// <item><c>TouchPanel.INTERNAL_onTouchEvent</c> feeds <c>GestureDetector</c>, which is what
	/// <c>TouchPanel.ReadGesture()</c> returns. That one is edge-driven: it needs a distinct
	/// Pressed, Moved and Released call, and a missed Released leaves a gesture open forever.</item>
	/// </list>
	///
	/// <para>Games use both APIs, so an injector that writes only one makes half of them see
	/// nothing at all.</para>
	/// </summary>
	public readonly struct SyntheticTouchSample
	{
		public SyntheticTouchSample(bool active, Vector2 position, Vector2 delta, bool justPressed, bool justReleased)
		{
			Active = active;
			Position = position;
			Delta = delta;
			JustPressed = justPressed;
			JustReleased = justReleased;
		}

		/// <summary>False when no gesture is in flight — the overwhelmingly common case, and the
		/// signal for the injector to leave its slot empty.</summary>
		public bool Active { get; }

		/// <summary>Position in display space (<c>TouchPanel.DisplayWidth</c>/<c>Height</c>), the
		/// same space <c>SetFinger</c> expects and the space a binding is authored in.</summary>
		public Vector2 Position { get; }

		/// <summary>Movement since the previous tick, display space. Gesture consumers that
		/// integrate deltas (drag, flick) read this rather than differencing positions.</summary>
		public Vector2 Delta { get; }

		public bool JustPressed { get; }
		public bool JustReleased { get; }

		public static SyntheticTouchSample Inactive => default;
	}
}
