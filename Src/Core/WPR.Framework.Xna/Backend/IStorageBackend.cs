using System.IO;

namespace WPR.Xna.Rhi
{
	/// <summary>
	/// Where a title's saved games live — Stage 5c-5 (docs/STAGE5C-SCOPE.md). Backs
	/// <c>StorageDevice</c>/<c>StorageContainer</c>.
	///
	/// <para>Small, but a seam rather than a pair of hooks on <see cref="XnaBackend"/>, because this
	/// is a distinct platform facility that a non-desktop backend answers completely differently
	/// (Android scopes save data per-package; a console backend goes through a storage API rather
	/// than a filesystem path). Keeping it named means those backends implement an obligation
	/// instead of quietly inheriting a desktop assumption.</para>
	/// </summary>
	public interface IStorageBackend
	{
		/// <summary>Root directory for save containers — the platform's per-user application-data
		/// location. Read once into a static, so it must be stable for the process lifetime.</summary>
		string GetStorageRoot();

		/// <summary>The volume backing <paramref name="storageRoot"/>, for <c>StorageDevice</c>'s
		/// free/total-space properties. Null if the platform cannot report one.</summary>
		DriveInfo GetDriveInfo(string storageRoot);
	}
}
