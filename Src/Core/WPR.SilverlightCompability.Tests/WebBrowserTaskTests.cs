using System;
using System.Collections.Generic;
using Microsoft.Phone.Tasks;
using WPR.Engine.Launchers;
using Xunit;

namespace WPR.SilverlightCompability.Tests;

/// <summary>
/// WebBrowserTask's URI policy: the WP7 YouTube scheme is rewritten, and only http(s) leaves.
/// </summary>
public class WebBrowserTaskTests
{
    [Theory]
    // Cut the Rope's hardcoded Cartoons episode, verbatim.
    [InlineData("vnd.youtube:bj3cbCE56wQ?vndapp=youtube_mobile", "https://www.youtube.com/watch?v=bj3cbCE56wQ")]
    [InlineData("vnd.youtube://bj3cbCE56wQ", "https://www.youtube.com/watch?v=bj3cbCE56wQ")]
    [InlineData("http://v.youku.com/v_show/id_XNDIwMTM4Mjcy.html", "http://v.youku.com/v_show/id_XNDIwMTM4Mjcy.html")]
    [InlineData("https://example.com/a?b=c", "https://example.com/a?b=c")]
    [InlineData("www.example.com", "http://www.example.com/")]
    public void Normalise_OpensWebLinks(string raw, string expected)
    {
        Assert.Equal(expected, WebBrowserTask.Normalise(raw)!.AbsoluteUri);
    }

    [Theory]
    [InlineData("file:///sdcard/secret.txt")]
    [InlineData("intent://scan/#Intent;scheme=zxing;end")]
    [InlineData("javascript:alert(1)")]
    [InlineData("vnd.youtube:")]
    [InlineData("vnd.youtube:a/b")]
    [InlineData("not a uri")]
    public void Normalise_RefusesEverythingElse(string raw)
    {
        Assert.Null(WebBrowserTask.Normalise(raw));
    }

    [Fact]
    public void Show_HandsTheNormalisedUriToTheRegisteredLauncher()
    {
        var launcher = new RecordingLauncher();
        IUriLauncher? previous = LauncherBackend.Uri;
        LauncherBackend.SetUriLauncher(launcher);
        try
        {
            new WebBrowserTask { Uri = new Uri("vnd.youtube:bj3cbCE56wQ?vndapp=youtube_mobile", UriKind.Absolute) }.Show();
            new WebBrowserTask { URL = "file:///etc/passwd" }.Show();
            new WebBrowserTask().Show();

            Assert.Equal(new[] { "https://www.youtube.com/watch?v=bj3cbCE56wQ" }, launcher.Opened);
        }
        finally
        {
            LauncherBackend.SetUriLauncher(previous);
        }
    }

    [Fact]
    public void Show_WithNoLauncher_DoesNotThrow()
    {
        IUriLauncher? previous = LauncherBackend.Uri;
        LauncherBackend.SetUriLauncher(null);
        try
        {
            new WebBrowserTask { Uri = new Uri("https://example.com") }.Show();
        }
        finally
        {
            LauncherBackend.SetUriLauncher(previous);
        }
    }

    private sealed class RecordingLauncher : IUriLauncher
    {
        public List<string> Opened { get; } = new();

        public bool TryOpen(Uri uri)
        {
            Opened.Add(uri.AbsoluteUri);
            return true;
        }
    }
}
