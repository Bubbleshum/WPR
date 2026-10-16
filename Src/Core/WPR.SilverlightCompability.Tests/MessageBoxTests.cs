using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WPR.WindowsCompability;
using Xunit;
using MessageBox = WPR.WindowsCompability.MessageBox;

namespace WPR.SilverlightCompability.Tests;

/// <summary>
/// Silverlight's blocking <c>MessageBox.Show</c>: the documented WP7 mapping (OK is OK "in all
/// cases"; OKCancel is OK only for the OK button, Cancel for anything else, Back included) and the
/// handoff to a head's presenter.
/// </summary>
[Collection("MessageBoxPresenter")]
public class MessageBoxTests : IDisposable
{
    public void Dispose() => MessageBox.Presenter = null;

    [Theory]
    [InlineData(MessageBoxButton.OK, 0, MessageBoxResult.OK)]
    [InlineData(MessageBoxButton.OK, -1, MessageBoxResult.OK)]
    [InlineData(MessageBoxButton.OKCancel, 0, MessageBoxResult.OK)]
    [InlineData(MessageBoxButton.OKCancel, 1, MessageBoxResult.Cancel)]
    [InlineData(MessageBoxButton.OKCancel, -1, MessageBoxResult.Cancel)]
    [InlineData(MessageBoxButton.YesNo, 0, MessageBoxResult.Yes)]
    [InlineData(MessageBoxButton.YesNo, 1, MessageBoxResult.No)]
    [InlineData(MessageBoxButton.YesNo, -1, MessageBoxResult.No)]
    [InlineData(MessageBoxButton.YesNoCancel, 2, MessageBoxResult.Cancel)]
    [InlineData(MessageBoxButton.YesNoCancel, -1, MessageBoxResult.Cancel)]
    [InlineData(MessageBoxButton.OKCancel, 7, MessageBoxResult.Cancel)]
    public void Resolve_FollowsTheDocumentedMapping(MessageBoxButton button, int chosen, MessageBoxResult expected)
    {
        Assert.Equal(expected, MessageBox.Resolve(button, chosen));
    }

    [Fact]
    public void Describe_UsesWp7Labels()
    {
        Assert.Equal(new[] { "ok" }, MessageBox.Describe(MessageBoxButton.OK).Labels);
        Assert.Equal(new[] { "ok", "cancel" }, MessageBox.Describe(MessageBoxButton.OKCancel).Labels);
    }

    [Fact]
    public void Show_PassesCaptionTextAndLabels_AndMapsTheAnswer()
    {
        string? seenCaption = null, seenText = null;
        IReadOnlyList<string>? seenLabels = null;
        MessageBox.Presenter = (caption, text, labels) =>
        {
            seenCaption = caption; seenText = text; seenLabels = labels;
            return Task.FromResult(0);
        };

        var result = MessageBox.Show("Start a new game?", "WARNING", MessageBoxButton.OKCancel);

        Assert.Equal(MessageBoxResult.OK, result);
        Assert.Equal("WARNING", seenCaption);
        Assert.Equal("Start a new game?", seenText);
        Assert.Equal(new[] { "ok", "cancel" }, seenLabels);
    }

    [Fact]
    public void Show_BackIsCancel()
    {
        MessageBox.Presenter = (_, _, _) => Task.FromResult(-1);
        Assert.Equal(MessageBoxResult.Cancel, MessageBox.Show("q", "c", MessageBoxButton.OKCancel));
        Assert.Equal(MessageBoxResult.OK, MessageBox.Show("only ok"));
    }

    [Fact]
    public void Show_WaitsForAnAnswerFromAnotherThread()
    {
        // The head answers on its UI thread; the game thread blocks until then.
        MessageBox.Presenter = (_, _, _) =>
        {
            var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            new Thread(() => { Thread.Sleep(50); tcs.SetResult(1); }).Start();
            return tcs.Task;
        };
        Assert.Equal(MessageBoxResult.Cancel, MessageBox.Show("q", "c", MessageBoxButton.OKCancel));
    }

    [Fact]
    public void Show_WithoutPresenterOrWithAFailingOne_AnswersAsDismissed()
    {
        MessageBox.Presenter = null;
        Assert.Equal(MessageBoxResult.Cancel, MessageBox.Show("q", "c", MessageBoxButton.OKCancel));
        MessageBox.Presenter = (_, _, _) => throw new InvalidOperationException("no UI");
        Assert.Equal(MessageBoxResult.Cancel, MessageBox.Show("q", "c", MessageBoxButton.OKCancel));
        Assert.Equal(MessageBoxResult.OK, MessageBox.Show(null!, null!, MessageBoxButton.OK));
    }
}
