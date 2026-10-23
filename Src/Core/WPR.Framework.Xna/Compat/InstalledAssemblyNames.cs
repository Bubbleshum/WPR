using System.Text;

namespace WPR.Xna.Compat
{
	/// <summary>
	/// Rewrites the assembly names inside an assembly-qualified type name the way the installer
	/// renamed the assemblies themselves.
	/// </summary>
	/// <remarks>
	/// <para>
	/// The patcher passes every game assembly's name through
	/// <c>WPR.AssemblyNameStandardization.Process</c>, which turns <c>*'",_&amp;#^@!</c> into
	/// <c>_</c>: Zuma's Revenge's <c>Zuma's Revenge!</c> is installed as <c>Zuma_s Revenge_</c>,
	/// file and identity both. Strings the game carries in its CONTENT are not rewritten, so its
	/// <c>levels.xnb</c> still names its reader <c>ZumasRevenge.LevelsXmlReader, Zuma's Revenge!</c>.
	/// </para>
	/// <para>
	/// Handed that string, the runtime's type-name parser throws <c>FileLoadException</c> ("The
	/// given assembly name was invalid") before any resolver is asked: an apostrophe opens a
	/// quoted token in an assembly display name. The content loader caught it and found the
	/// reader by a later fallback, so the game ran, but every load threw twice, and WPR Hub
	/// received each one as a <c>missing-assembly</c> compatibility problem. Rewriting the name
	/// first resolves it on the first attempt and to the assembly that actually exists.
	/// </para>
	/// <para>
	/// The character set is the patcher's, copied rather than referenced because
	/// <c>WPR.Loader</c> sits above this assembly; keep the two in step. <c>,</c> and <c>_</c>
	/// are left out here because a simple name cannot contain the first (it ends there) and the
	/// second maps to itself.
	/// </para>
	/// </remarks>
	public static class InstalledAssemblyNames
	{
		private const string RenamedCharacters = "*'\"&#^@!";

		/// <summary>
		/// The type name with the simple name of every assembly in it (the outer one and each
		/// generic argument's) standardised. Type names, versions, cultures and tokens are left
		/// alone. Returns the input itself when nothing needs changing.
		/// </summary>
		public static string StandardiseTypeName(string typeName)
		{
			if (string.IsNullOrEmpty(typeName) || typeName.IndexOfAny(RenamedCharacters.ToCharArray()) < 0)
			{
				return typeName;
			}

			// Depth counts square brackets. A generic argument LIST opens an odd depth, where a
			// comma separates arguments (and an array rank like [,] lives); each assembly-qualified
			// argument inside it opens an even depth, which is shaped like the whole name: a type,
			// then a comma, then its assembly. So a comma at an even depth, the first in its
			// segment, starts an assembly name, and its simple name runs to the next comma or the
			// segment's end.
			StringBuilder result = new StringBuilder(typeName.Length);
			int depth = 0;
			bool inAssembly = false;   // past this segment's assembly comma
			bool inSimpleName = false; // still inside that assembly's simple name
			for (int i = 0; i < typeName.Length; i++)
			{
				char c = typeName[i];
				if (c == '\\' && !inAssembly && i + 1 < typeName.Length)
				{
					// An escaped character in the type part is never structure.
					result.Append(c).Append(typeName[++i]);
					continue;
				}
				switch (c)
				{
					case '[':
						if (!inAssembly) depth++;
						break;
					case ']':
						if (depth > 0)
						{
							depth--;
							// Closing an even-depth argument ends its assembly; closing an
							// odd-depth list returns to the type part of the enclosing segment.
							inAssembly = false;
							inSimpleName = false;
						}
						break;
					case ',':
						if (depth % 2 == 0)
						{
							if (!inAssembly)
							{
								inAssembly = true;
								inSimpleName = true;
							}
							else
							{
								inSimpleName = false; // Version=, Culture=, PublicKeyToken= follow
							}
						}
						break;
					default:
						if (inSimpleName && RenamedCharacters.IndexOf(c) >= 0)
						{
							c = '_';
						}
						break;
				}
				result.Append(c);
			}
			return result.ToString();
		}
	}
}
