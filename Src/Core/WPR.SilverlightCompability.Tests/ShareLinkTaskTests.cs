using System;
using System.Collections.Generic;
using Microsoft.Phone.Tasks;
using WPR.Engine.Launchers;
using Xunit;

namespace WPR.SilverlightCompability.Tests;

/// <summary>
/// ShareLinkTask / ShareStatusTask: what text reaches the share sheet.
/// </summary>
public class ShareLinkTaskTests
{
    [Fact]
    public void ShareLink_SendsMessageThenLink_WithTitleAsSubject_AndRewritesWp7YouTube()
    {
        var shared = Capture(() => new ShareLinkTask
        {
            // Cut the Rope's Share button on the Cartoons after-watch screen.
            Title = "Share",
            Message = "Cut the Rope Cartoons: Episode 1",
            LinkUri = new Uri("vnd.youtube:bj3cbCE56wQ?vndapp=youtube_mobile", UriKind.Absolute),
        }.Show());

        Assert.Equal(new[] { ((string?)"Share", "Cut the Rope Cartoons: Episode 1 https://www.youtube.com/watch?v=bj3cbCE56wQ") }, shared);
    }

    [Fact]
    public void ShareLink_KeepsANonWebLinkAsText()
    {
        var shared = Capture(() => new ShareLinkTask { LinkUri = new Uri("zune://app/123") }.Show());

        Assert.Equal(new[] { ((string?)null, "zune://app/123") }, shared);
    }

    [Fact]
    public void ShareStatus_SendsTheStatus_AndEmptyTasksShareNothing()
    {
        var shared = Capture(() =>
        {
            new ShareStatusTask { Status = "  I scored 3 stars!  " }.Show();
            new ShareStatusTask().Show();
            new ShareLinkTask { Title = "only a title" }.Show();
        });

        Assert.Equal(new[] { ((string?)null, "I scored 3 stars!") }, shared);
    }

    [Fact]
    public void Show_WithNoShareSheet_DoesNotThrow()
    {
        IShareSheet? previous = LauncherBackend.Share;
        LauncherBackend.SetShareSheet(null);
        try
        {
            new ShareLinkTask { LinkUri = new Uri("https://example.com") }.Show();
        }
        finally
        {
            LauncherBackend.SetShareSheet(previous);
        }
    }

    private static List<(string?, string)> Capture(Action act)
    {
        var sheet = new RecordingSheet();
        IShareSheet? previous = LauncherBackend.Share;
        LauncherBackend.SetShareSheet(sheet);
        try
        {
            act();
        }
        finally
        {
            LauncherBackend.SetShareSheet(previous);
        }
        return sheet.Shared;
    }

    private sealed class RecordingSheet : IShareSheet
    {
        public List<(string?, string)> Shared { get; } = new();

        public bool TryShare(string? subject, string text)
        {
            Shared.Add((subject, text));
            return true;
        }
    }
}
