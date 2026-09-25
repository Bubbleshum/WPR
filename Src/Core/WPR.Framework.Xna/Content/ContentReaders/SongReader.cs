#region License
/* FNA - XNA4 Reimplementation for Desktop Platforms
 * Copyright 2009-2022 Ethan Lee and the MonoGame Team
 *
 * Released under the Microsoft Public License.
 * See LICENSE for details.
 */
#endregion

#region Using Statements
using System;
using System.IO;

using Microsoft.Xna.Framework.Media;
#endregion

namespace Microsoft.Xna.Framework.Content
{
	internal class SongReader : ContentTypeReader<Song>
	{
		#region Private Supported File Extensions Variable

		static string[] supportedExtensions = new string[] { ".ogg", ".oga", ".wma" };

		#endregion

		#region Internal Filename Normalizer Method

		internal static string Normalize(string fileName)
		{
			return Normalize(fileName, supportedExtensions);
		}

		/// <summary>
		/// What XNA reports as <see cref="Song.Name"/> for a content-loaded song: the asset name the
		/// game passed to <c>Load</c>, with XNA's cleaned-path separator (<c>\</c>).
		/// </summary>
		/// <remarks>
		/// FNA left this null, and games key on it. Sonic 4 Episode I's <c>MediaStateChanged</c>
		/// handler matches <c>MediaPlayer.Queue.ActiveSong.Name</c> against <c>"Sound\\" + file</c>
		/// (it loads with <c>"Sound/" + file</c>, so the backslash is XNA's normalisation, not the
		/// game's) and does nothing when it is null — so a non-repeating track ending (the 1-up
		/// jingle, the speed-shoes track) was never noticed and the level music never came back.
		/// </remarks>
		internal static string AssetNameOf(string assetName)
		{
			return assetName?.Replace('/', '\\');
		}

		#endregion

		#region Protected Read Method

		protected internal override Song Read(ContentReader input, Song existingInstance)
		{
			string path = MonoGame.Utilities.FileHelpers.ResolveRelativePath(
				Path.Combine(
					input.ContentManager.RootDirectoryFullPath,
					input.AssetName
				),
				input.ReadString()
			);

			/* The path string includes the ".wma" extension. Let's see if this
			 * file exists in a format we actually support...
			 */
			path = Normalize(path.Substring(0, path.Length - 4));
			if (String.IsNullOrEmpty(path))
			{
				throw new ContentLoadException();
			}

			int durationMs = input.ReadInt32();

			return new Song(path, durationMs, AssetNameOf(input.AssetName));
		}

		#endregion
	}
}
