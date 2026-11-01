using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace TarkovMonitor.Updating
{
    /// <summary>
    /// Renders the small subset of Markdown used in GitHub release
    /// descriptions (headings, bullet lists, bold, inline code and links) as
    /// HTML for the update popup. Everything else is shown as plain text, and
    /// all text is encoded first, so a release body cannot inject markup.
    /// </summary>
    internal static partial class ReleaseNotes
    {
        internal static string ToHtml(string? markdown)
        {
            if (string.IsNullOrWhiteSpace(markdown))
            {
                return "";
            }

            var html = new StringBuilder();
            var inList = false;
            void CloseList()
            {
                if (inList)
                {
                    html.Append("</ul>");
                    inList = false;
                }
            }

            foreach (var raw in markdown.Replace("\r", "").Split('\n'))
            {
                var line = raw.TrimEnd();
                if (line.Length == 0)
                {
                    CloseList();
                    continue;
                }

                var heading = HeadingPattern().Match(line);
                if (heading.Success)
                {
                    CloseList();
                    html.Append("<div class=\"tm-notes-heading\">").Append(Inline(heading.Groups[2].Value)).Append("</div>");
                    continue;
                }

                var bullet = BulletPattern().Match(line);
                if (bullet.Success)
                {
                    if (!inList)
                    {
                        html.Append("<ul>");
                        inList = true;
                    }
                    html.Append("<li>").Append(Inline(bullet.Groups[1].Value)).Append("</li>");
                    continue;
                }

                CloseList();
                html.Append("<p>").Append(Inline(line.Trim())).Append("</p>");
            }
            CloseList();
            return html.ToString();
        }

        private static string Inline(string text)
        {
            var encoded = WebUtility.HtmlEncode(text);
            encoded = CodePattern().Replace(encoded, "<code>$1</code>");
            encoded = BoldPattern().Replace(encoded, "<b>$1</b>");
            encoded = LinkPattern().Replace(encoded, "<a href=\"$2\" target=\"_blank\">$1</a>");
            return encoded;
        }

        [GeneratedRegex(@"^(#{1,6})\s+(.*)$")]
        private static partial Regex HeadingPattern();

        [GeneratedRegex(@"^\s*[-*+]\s+(.*)$")]
        private static partial Regex BulletPattern();

        [GeneratedRegex(@"`([^`]+)`")]
        private static partial Regex CodePattern();

        [GeneratedRegex(@"\*\*(.+?)\*\*")]
        private static partial Regex BoldPattern();

        [GeneratedRegex(@"\[([^\]]+)\]\((https?://[^\s)""]+)\)")]
        private static partial Regex LinkPattern();
    }
}
