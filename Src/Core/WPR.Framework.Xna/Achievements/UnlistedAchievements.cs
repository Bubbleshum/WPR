using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Xna.Framework.GamerServices;

namespace WPR.Xna.Achievements
{
	/// <summary>
	/// An award for a key the title's catalogue does not list. On the phone the Xbox LIVE service
	/// knew every achievement of a title, so an award always found its achievement; here the
	/// catalogue is built from the package (or shipped with WPR), and a key the package hides
	/// (encrypted strings, a key built at run time) has no row. Such an award creates its row and
	/// is earned like any other, rather than going nowhere.
	/// </summary>
	/// <remarks>
	/// <para>A title with a <b>curated</b> catalogue shipped with WPR is left alone
	/// (<see cref="Allowed"/> false): its catalogue is authoritative, and its reconcile would delete
	/// such a row again at the next start. Set per launch by the launch sequence, which knows where
	/// the title's catalogue comes from; true when nobody set it.</para>
	/// </remarks>
	public static class UnlistedAchievements
	{
		/// <summary>Whether an award for an unlisted key creates its row, for the title running now.</summary>
		public static bool Allowed { get; set; } = true;

		/// <summary>
		/// The row for an unlisted key: the key made readable as its name, no description, no
		/// icon, no gamerscore (nothing in the award call says what it was worth). Not yet earned:
		/// the award path flips it as it does a catalogue row.
		/// </summary>
		public static Achievement NewRow(string productId, string key) => new Achievement
		{
			OwnProductId = productId,
			Key = key,
			Name = Readable(key),
			Description = string.Empty,
			HowToEarn = string.Empty,
			_IconPath = string.Empty,
			DisplayBeforeEarned = true,
			GamerScore = 0,
			IsEarned = false,
			EarnedOnline = false,
			EarnedDateTime = DateTime.MinValue,
		};

		/// <summary>
		/// A key as a display name: <c>ACHIEVEMENT_COLLECT_STARS</c> is "Collect Stars",
		/// <c>GET_BLUE_BIRD</c> "Get Blue Bird", <c>TourOfDuty</c> "Tour Of Duty"; a number is
		/// "Achievement 12". The same rule the package catalogue generator names its keys by.
		/// </summary>
		public static string Readable(string key)
		{
			if (key is null) return string.Empty;
			string text = key.Trim();
			if (text.Length == 0) return key;
			if (text.All(char.IsDigit)) return "Achievement " + text;

			// Split camel case, then separators.
			StringBuilder spaced = new();
			for (int i = 0; i < text.Length; i++)
			{
				char c = text[i];
				if (i > 0 && char.IsUpper(c) && char.IsLower(text[i - 1])) spaced.Append(' ');
				spaced.Append(c is '_' or '-' or '.' ? ' ' : c);
			}
			List<string> words = spaced.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
			string[] decorations = ["achievement", "achievements", "ach", "achiev", "achv", "acv", "xbox", "xbl", "live", "award"];
			while (words.Count > 1 && decorations.Contains(words[0].ToLowerInvariant())) words.RemoveAt(0);
			while (words.Count > 1 && decorations.Contains(words[^1].ToLowerInvariant())) words.RemoveAt(words.Count - 1);
			if (words.Count == 0) return text;
			return string.Join(' ', words.Select(w => w.Length == 0 ? w
				: w.All(char.IsDigit) ? w
				: char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant()));
		}
	}
}
