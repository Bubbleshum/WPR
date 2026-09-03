namespace Microsoft.Xna.Framework.Audio
{
	/// <summary>
	/// Converts the XNA <see cref="Vector3"/> the game-facing audio types speak into the
	/// <c>System.Numerics.Vector3</c> the audio seam speaks.
	///
	/// <para>The seam went neutral when the audio subsystem moved into <c>WPR.Engine.Audio</c>
	/// (2026-09-01): a contract in the engine tier must not name a game-facing framework identity,
	/// or the framework could not reference it back — the same call <c>IAccelerometerProvider</c> made,
	/// and for the same reason. Three floats cost one copy at the two places that build
	/// <c>Audio3DParams</c>, where the whole XNA vocabulary would have cost a dependency cycle.</para>
	///
	/// <para>3D audio is per-instance and per-emitter, not per-vertex, so this is nowhere near a
	/// hot path.</para>
	/// </summary>
	internal static class AudioVectorInterop
	{
		internal static System.Numerics.Vector3 ToNumerics(this Vector3 v) =>
			new System.Numerics.Vector3(v.X, v.Y, v.Z);
	}
}
