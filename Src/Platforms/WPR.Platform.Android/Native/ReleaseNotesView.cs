using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

using Android.Content;
using Android.Graphics;
using Android.Text;
using Android.Text.Method;
using Android.Views;
using Android.Widget;

using WPR.Shell;

namespace WPR.Platform.Android.Native
{
    /// <summary>
    /// The release notes bundled into the APK (<c>assets/ReleaseNotes/&lt;version&gt;.md</c>, from
    /// <c>Docs/ReleaseNotes</c>), and the views the About page's "what's new" pivot draws them with.
    /// </summary>
    /// <remarks>
    /// The notes are written for GitHub, so this reads the small part of Markdown they actually use:
    /// headings, paragraphs, bullet lists (with wrapped continuation lines), the two-column emoji
    /// tables, block quotes, horizontal rules, and inline bold, italics, code and links. Anything
    /// else comes through as plain text, which is the safe way to be wrong.
    /// </remarks>
    internal static class ReleaseNotesView
    {
        private const string AssetFolder = "ReleaseNotes";

        /// <summary>Every bundled version, newest first.</summary>
        public static IReadOnlyList<string> BundledVersions(Context context)
        {
            try
            {
                string[] files = context.Assets!.List(AssetFolder) ?? Array.Empty<string>();
                List<string> versions = files
                    .Where(f => f.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                    .Select(f => f.Substring(0, f.Length - 3))
                    .Where(v => !v.EndsWith("-full", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                versions.Sort((a, b) => AppUpdates.CompareVersions(b, a));
                return versions;
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Warn("WPR", "could not list release notes: " + ex.Message);
                return Array.Empty<string>();
            }
        }

        public static string? Read(Context context, string version)
        {
            try
            {
                using Stream stream = context.Assets!.Open($"{AssetFolder}/{version}.md");
                using StreamReader reader = new StreamReader(stream, Encoding.UTF8);
                return reader.ReadToEnd();
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Warn("WPR", $"could not read release notes {version}: {ex.Message}");
                return null;
            }
        }

        // ------------------------------------------------------------------ rendering

        /// <summary>Appends <paramref name="markdown"/> to <paramref name="into"/> as WP-styled views.</summary>
        public static void Render(Context context, LinearLayout into, string markdown)
        {
            string[] lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            List<string> paragraph = new List<string>();
            List<string> quote = new List<string>();

            void FlushParagraph()
            {
                if (paragraph.Count == 0) return;
                into.AddView(Body(context, string.Join(" ", paragraph)), Spaced(context, 10));
                paragraph.Clear();
            }

            void FlushQuote()
            {
                if (quote.Count == 0) return;
                into.AddView(Quote(context, quote), Spaced(context, 12));
                quote.Clear();
            }

            for (int i = 0; i < lines.Length; i++)
            {
                string raw = lines[i];
                string line = raw.Trim();

                if (line.StartsWith(">"))
                {
                    FlushParagraph();
                    quote.Add(line.Substring(1).Trim());
                    continue;
                }
                FlushQuote();

                if (line.Length == 0) { FlushParagraph(); continue; }

                if (Regex.IsMatch(line, @"^(-{3,}|\*{3,}|_{3,})$"))
                {
                    FlushParagraph();
                    View rule = new View(context);
                    rule.SetBackgroundColor(Color.Argb(0x40, 0xFF, 0xFF, 0xFF));
                    LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 1)
                    {
                        TopMargin = HubViews.Dp(context, 18),
                        BottomMargin = HubViews.Dp(context, 4),
                    };
                    into.AddView(rule, lp);
                    continue;
                }

                if (line.StartsWith("#"))
                {
                    FlushParagraph();
                    string heading = line.TrimStart('#').Trim();
                    TextView header = HubViews.Text(context, "", 20, WpTheme.Foreground, light: true);
                    header.TextFormatted = Inline(heading);
                    into.AddView(header, Spaced(context, 22));
                    continue;
                }

                if (line.StartsWith("|"))
                {
                    FlushParagraph();
                    string[] cells = line.Trim('|').Split('|').Select(c => c.Trim()).ToArray();
                    bool separator = cells.All(c => Regex.IsMatch(c, @"^:?-*:?$"));
                    if (separator || cells.All(c => c.Length == 0)) continue;
                    into.AddView(TableRow(context, cells), Spaced(context, 6));
                    continue;
                }

                if (line.StartsWith("- ") || line.StartsWith("* "))
                {
                    FlushParagraph();
                    StringBuilder item = new StringBuilder(line.Substring(2).Trim());
                    // Wrapped continuation lines are indented under the bullet.
                    while (i + 1 < lines.Length && lines[i + 1].StartsWith("  ") && lines[i + 1].Trim().Length > 0
                           && !lines[i + 1].TrimStart().StartsWith("- ") && !lines[i + 1].TrimStart().StartsWith("* "))
                    {
                        item.Append(' ').Append(lines[++i].Trim());
                    }
                    into.AddView(TableRow(context, new[] { "•", item.ToString() }), Spaced(context, 6));
                    continue;
                }

                paragraph.Add(line);
            }
            FlushParagraph();
            FlushQuote();
        }

        private static LinearLayout.LayoutParams Spaced(Context context, int topDp) =>
            new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
            {
                TopMargin = HubViews.Dp(context, topDp),
            };

        private static TextView Body(Context context, string text, float sp = 15)
        {
            TextView view = HubViews.Text(context, "", sp, WpTheme.Foreground);
            view.TextFormatted = Inline(text);
            view.SetLineSpacing(HubViews.Dp(context, 3), 1f);
            view.MovementMethod = LinkMovementMethod.Instance;
            view.SetLinkTextColor(WpTheme.Accent);
            return view;
        }

        /// <summary>A table row or a bullet: a narrow first column (the emoji, or "•") and the text.</summary>
        private static View TableRow(Context context, string[] cells)
        {
            if (cells.Length < 2) return Body(context, cells.FirstOrDefault() ?? "");

            LinearLayout row = new LinearLayout(context) { Orientation = Orientation.Horizontal };
            TextView lead = HubViews.Text(context, "", 15, WpTheme.Foreground);
            lead.TextFormatted = Inline(cells[0]);
            row.AddView(lead, new LinearLayout.LayoutParams(HubViews.Dp(context, 30), ViewGroup.LayoutParams.WrapContent));
            row.AddView(Body(context, string.Join(" · ", cells.Skip(1).Where(c => c.Length > 0))),
                new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
            return row;
        }

        /// <summary>A block quote: an accent bar beside subtle text, with "&gt;" blank lines as paragraph breaks.</summary>
        private static View Quote(Context context, List<string> lines)
        {
            LinearLayout row = new LinearLayout(context) { Orientation = Orientation.Horizontal };
            View bar = new View(context);
            bar.SetBackgroundColor(WpTheme.Accent);
            row.AddView(bar, new LinearLayout.LayoutParams(HubViews.Dp(context, 3), ViewGroup.LayoutParams.MatchParent)
            {
                RightMargin = HubViews.Dp(context, 12),
            });

            LinearLayout text = new LinearLayout(context) { Orientation = Orientation.Vertical };
            List<string> current = new List<string>();
            void Flush()
            {
                if (current.Count == 0) return;
                TextView body = Body(context, string.Join(" ", current), 14);
                body.SetTextColor(HubViews.Subtle);
                text.AddView(body, Spaced(context, text.ChildCount == 0 ? 0 : 8));
                current.Clear();
            }
            foreach (string line in lines)
            {
                if (line.Length == 0) Flush(); else current.Add(line);
            }
            Flush();
            row.AddView(text, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
            return row;
        }

        /// <summary>Inline Markdown to a styled span: bold, italics, code and links, everything else escaped.</summary>
        private static ISpanned Inline(string markdown)
        {
            string html = WebUtility.HtmlEncode(markdown);
            html = Regex.Replace(html, @"\[([^\]]+)\]\(([^)\s]+)\)", m =>
                $"<a href=\"{m.Groups[2].Value}\">{m.Groups[1].Value}</a>");
            html = Regex.Replace(html, @"`([^`]+)`", "<tt>$1</tt>");
            html = Regex.Replace(html, @"\*\*(.+?)\*\*", "<b>$1</b>");
            html = Regex.Replace(html, @"(?<![\w*])\*(?!\s)(.+?)(?<!\s)\*(?![\w*])", "<i>$1</i>");
            html = Regex.Replace(html, @"(?<!\w)_(?!\s)(.+?)(?<!\s)_(?!\w)", "<i>$1</i>");
            return Html.FromHtml(html, FromHtmlOptions.ModeCompact)!;
        }
    }
}
