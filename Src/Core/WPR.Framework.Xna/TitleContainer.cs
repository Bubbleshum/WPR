#region License
/* FNA - XNA4 Reimplementation for Desktop Platforms
 * Copyright 2009-2022 Ethan Lee and the MonoGame Team
 *
 * Released under the Microsoft Public License.
 * See LICENSE for details.
 */
#endregion

#region CASE_SENSITIVITY_HACK Option
// #define CASE_SENSITIVITY_HACK
/* On Linux, the file system is case sensitive.
 * This means that unless you really focused on it, there's a good chance that
 * your filenames are not actually accurate! The result: File/DirectoryNotFound.
 * This is a quick alternative to MONO_IOMAP=all, but the point is that you
 * should NOT depend on either of these two things. PLEASE fix your paths!
 * -flibit
 */
#endregion

#region Using Statements
using System;
using System.Diagnostics;
using System.IO;
#endregion

namespace Microsoft.Xna.Framework
{
	public static class TitleContainer
	{
		#region Public Static Methods

		// Per-call open log, gated by DEBUG via WprDebugTrace so Release builds elide the
		// formatting & dispatch entirely. Cap raised to 2000 (most games load a few hundred
		// asset files during init; capping too low hides which file the loader stalls on).
		private static int _wprOpenTraceCount;

		public static Stream OpenStream(string name)
		{
			string safeName = MonoGame.Utilities.FileHelpers.NormalizeFilePathSeparators(name);

#if CASE_SENSITIVITY_HACK
			if (Path.IsPathRooted(safeName))
			{
				safeName = GetCaseName(safeName);
			}
			safeName = GetCaseName(Path.Combine(XnaBackend.TitleLocation, safeName));
#endif
			bool wprTrace = _wprOpenTraceCount < 2000;
			if (wprTrace)
			{
				_wprOpenTraceCount++;
				WprDebugTrace.WriteLine($"[wpr-trace] TitleContainer.OpenStream #{_wprOpenTraceCount}: name=\"{name}\" safeName=\"{safeName}\" rooted={Path.IsPathRooted(safeName)} titleLoc=\"{XnaBackend.TitleLocation}\"");
			}
			if (Path.IsPathRooted(safeName))
			{
				try
				{
					return File.OpenRead(safeName);
				}
				catch (Exception ex)
				{
					WprDebugTrace.WriteLine($"[wpr-ex] TitleContainer.OpenStream rooted path threw for \"{safeName}\": {ex.GetType().Name}: {ex.Message}");
					throw;
				}
			}

			string full = Path.Combine(XnaBackend.TitleLocation, safeName);
			try
			{
				return File.OpenRead(full);
			}
			catch (FileNotFoundException)
			{
				// FFWD-style games (PressPlay's Tentacles etc.) call
				// `Application.LoadLevel("MyLevel")` with a bare name even though every
				// level XNB ships under `Content/Scenes/`. ContentManager produces the
				// path `Content/MyLevel.xnb`; the file isn't there, the load fails
				// silently inside AssetHelper, and the game hangs waiting on
				// `loadingProgress == 1.0f`. Try the Scenes/ subdir as a fallback when
				// the original path is directly under Content/ — covers the Tentacles
				// case (LoadLevel("MainMenu") → Content/Scenes/MainMenu.xnb if present,
				// LoadLevel("Veins_…") → Content/Scenes/Veins_….xnb) without affecting
				// games that don't use this layout (the fallback file simply won't
				// exist either, and we rethrow so the caller's own catch runs).
				string sep = Path.DirectorySeparatorChar.ToString();
				string altRel = null;
				string contentDir = "Content" + sep;
				if (safeName.StartsWith(contentDir, StringComparison.OrdinalIgnoreCase))
				{
					string rest = safeName.Substring(contentDir.Length);
					// Only retry if the asset is a direct child of Content/ (no other
					// folder hint) — otherwise we'd send Content/Textures/foo to
					// Content/Scenes/Textures/foo, which is nonsense.
					if (!rest.Contains(sep))
					{
						altRel = Path.Combine("Content", "Scenes", rest);
					}
				}

				if (altRel != null)
				{
					string altFull = Path.Combine(XnaBackend.TitleLocation, altRel);
					if (File.Exists(altFull))
					{
						WprDebugTrace.WriteLine($"[wpr-trace] TitleContainer.OpenStream Scenes/ fallback hit: \"{full}\" -> \"{altFull}\"");
						try { return File.OpenRead(altFull); }
						catch (Exception ex2)
						{
							WprDebugTrace.WriteLine($"[wpr-ex] TitleContainer.OpenStream fallback FAILED full=\"{altFull}\": {ex2.GetType().Name}: {ex2.Message}");
							throw;
						}
					}
				}

				// Rethrow so games that do `try { OpenStream("foo.en-GB") } catch { OpenStream("foo") }`
				// (a very common WP7 localization-fallback idiom — Castlevania Puzzle's
				// DashResourceProvider.getLocalizedBinary is the motivating case) actually
				// see the exception. Returning null here silently breaks the fallback
				// path and the resulting BinaryReader(null) blows up inside Game.Update,
				// which Game.Tick swallows, leaving the user staring at a working UI
				// whose buttons appear to do nothing.
				WprDebugTrace.WriteLine($"[wpr-ex] TitleContainer.OpenStream FAILED full=\"{full}\": FileNotFoundException (no Scenes/ fallback either) — rethrowing");
				Debug.WriteLine("[ex] Exception : File not found: " + full);
				throw;
			}
			catch (Exception ex)
			{
				WprDebugTrace.WriteLine($"[wpr-ex] TitleContainer.OpenStream FAILED full=\"{full}\": {ex.GetType().Name}: {ex.Message} — rethrowing");
				Debug.WriteLine("[ex] Exception : " + ex.Message);
				throw;
			}
		}

		#endregion

		#region Private Static fcaseopen Method

#if CASE_SENSITIVITY_HACK
		private static string GetCaseName(string name)
		{
			if (File.Exists(name))
			{
				return name;
			}

			string[] splits = name.Split(Path.DirectorySeparatorChar);
			splits[0] = "/";
			int i;

			// The directories...
			for (i = 1; i < splits.Length - 1; i += 1)
			{
				splits[0] += SearchCase(
					splits[i],
					Directory.GetDirectories(splits[0])
				);
			}

			// The file...
			splits[0] += SearchCase(
				splits[i],
				Directory.GetFiles(splits[0])
			);

			// Finally.
			splits[0] = splits[0].Remove(0, 1);
			FNALoggerEXT.LogError(
				"Case sensitivity!\n\t" +
				name.Substring(XnaBackend.TitleLocation.Length) + "\n\t" +
				splits[0].Substring(XnaBackend.TitleLocation.Length)
			);
			return splits[0];
		}

		private static string SearchCase(string name, string[] list)
		{
			foreach (string l in list)
			{
				string li = l.Substring(l.LastIndexOf("/") + 1);
				if (name.ToLower().Equals(li.ToLower()))
				{
					return Path.DirectorySeparatorChar + li;
				}
			}
			// If you got here, get ready to crash!
			return Path.DirectorySeparatorChar + name;
		}
#endif

		#endregion
	}
}

