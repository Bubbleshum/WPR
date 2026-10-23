using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Xna.Framework.GamerServices;
using Xunit;

namespace WPR.SilverlightCompability.Tests;

/// <summary>
/// XNA's <c>Guide.BeginShowMessageBox</c>: the head is handed the game's own labels and focus
/// button, answers an index or -1, and <c>EndShowMessageBox</c> gives the game that index, or null
/// when the box was closed without a button ("null if the user cancelled").
/// </summary>
[Collection("MessageBoxPresenter")]
public class GuideMessageBoxTests : IDisposable
{
    private readonly Func<string, string, IEnumerable<string>, int, MessageBoxIcon, Task<int>> _saved = Guide.ShowMessageBoxFunc;

    public void Dispose() => Guide.ShowMessageBoxFunc = _saved;

    [Theory]
    [InlineData(0, 2, 0)]
    [InlineData(1, 2, 1)]
    [InlineData(0, 1, 0)]
    [InlineData(-1, 2, null)]
    [InlineData(2, 2, null)]
    [InlineData(1, 1, null)]
    public void Resolve_IsTheIndexOrNull(int chosen, int count, int? expected)
    {
        Assert.Equal(expected, Guide.ResolveMessageBoxResult(chosen, count));
    }

    private static int? Run(string[] buttons, int focus, Func<IReadOnlyList<string>, int, Task<int>> presenter,
        out IReadOnlyList<string>? seen, out int seenFocus)
    {
        IReadOnlyList<string>? labels = null;
        int f = -99;
        Guide.ShowMessageBoxFunc = (_, _, b, fb, _) =>
        {
            labels = b.ToList();
            f = fb;
            return presenter(labels, fb);
        };

        int? fromCallback = -42;
        using var done = new ManualResetEventSlim();
        var ar = Guide.BeginShowMessageBox("Quit?", "Leave the game?", buttons, focus, MessageBoxIcon.None,
            r => { fromCallback = Guide.EndShowMessageBox(r); done.Set(); }, null);
        Assert.True(done.Wait(TimeSpan.FromSeconds(5)), "the callback never ran");
        int? fromResult = Guide.EndShowMessageBox(ar);
        Assert.Equal(fromResult, fromCallback);
        Assert.False(Guide.IsVisible);
        seen = labels;
        seenFocus = f;
        return fromResult;
    }

    [Fact]
    public void TheGamesLabelsAndFocusReachTheHead_AndTheIndexComesBack()
    {
        int? r = Run(new[] { "Yes", "No" }, 1, (_, _) => Task.FromResult(0), out var labels, out int focus);
        Assert.Equal(0, r);
        Assert.Equal(new[] { "Yes", "No" }, labels);
        Assert.Equal(1, focus);

        Assert.Equal(1, Run(new[] { "Yes", "No" }, 0, (_, _) => Task.FromResult(1), out _, out _));
    }

    [Fact]
    public void ADismissedBoxAnswersNull_NotTheFocusButton()
    {
        Assert.Null(Run(new[] { "Yes", "No" }, 0, (_, _) => Task.FromResult(-1), out _, out _));
    }

    [Fact]
    public void AFailingOrMissingHeadAnswersNull()
    {
        Assert.Null(Run(new[] { "OK" }, 0, (_, _) => throw new InvalidOperationException("no window"), out _, out _));
        Assert.Null(Run(new[] { "OK" }, 0, (_, _) => Task.FromException<int>(new MissingMethodException()), out _, out _));

        Guide.ShowMessageBoxFunc = null!;
        using var done = new ManualResetEventSlim();
        int? r = -42;
        Guide.BeginShowMessageBox("t", "x", new[] { "OK" }, 0, MessageBoxIcon.None, a => { r = Guide.EndShowMessageBox(a); done.Set(); }, null);
        Assert.True(done.Wait(TimeSpan.FromSeconds(5)));
        Assert.Null(r);
    }
}
