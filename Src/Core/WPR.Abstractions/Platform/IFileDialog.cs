namespace WPR.Abstractions.Platform;

/// <summary>
/// Native open/save file dialogs. Returns <c>null</c> when the user cancels.
/// <paramref name="filter"/> is a platform-neutral pattern (e.g. <c>"*.xap"</c>).
/// </summary>
public interface IFileDialog
{
    string? OpenFile(string filter);
    string? SaveFile(string filter);
}
