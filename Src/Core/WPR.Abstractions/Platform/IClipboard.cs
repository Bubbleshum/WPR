namespace WPR.Abstractions.Platform;

/// <summary>Platform clipboard access.</summary>
public interface IClipboard
{
    string? GetText();
    void SetText(string text);
}
