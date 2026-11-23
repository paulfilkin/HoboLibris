using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace HoboLibris.Views
{
    /// <summary>
    /// An Assistant reply: read-only and selectable, with the simple Markdown models write shown
    /// formatted: **bold**, *italic*, `code`, # headings, - and 1. lists, | tables |, [links](https://...)
    /// and bare https:// addresses, which open in the browser.  Only http and https links are opened.
    /// Ported from Mycelia's Ui/ChatText.cs with HoboLibris's brushes, and a Markdown property to bind.
    /// </summary>
    public sealed class ChatText : RichTextBox
    {
        public static readonly DependencyProperty MarkdownProperty = DependencyProperty.Register(
            nameof(Markdown), typeof(string), typeof(ChatText),
            new PropertyMetadata(string.Empty, (d, e) => ((ChatText)d).Render((string?)e.NewValue ?? string.Empty)));

        public ChatText()
        {
            IsReadOnly = true;
            IsDocumentEnabled = true;
            BorderThickness = new Thickness(0);
            Background = Brushes.Transparent;
            Padding = new Thickness(0);
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            AddHandler(Hyperlink.RequestNavigateEvent, new System.Windows.Navigation.RequestNavigateEventHandler((_, e) =>
            {
                if (e.Uri.Scheme is "http" or "https")
                {
                    Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
                }
                e.Handled = true;
            }));
            Render(string.Empty);
        }

        /// <summary>The text shown, as Markdown; setting it draws it again.</summary>
        public string Markdown
        {
            get => (string)GetValue(MarkdownProperty);
            set => SetValue(MarkdownProperty, value);
        }

        private void Render(string markdown)
        {
            var document = new FlowDocument { PagePadding = new Thickness(0), FontSize = 13 };
            document.SetBinding(FlowDocument.FontFamilyProperty, new System.Windows.Data.Binding(nameof(FontFamily)) { Source = this });
            document.SetResourceReference(FlowDocument.ForegroundProperty, "TextPrimaryBrush");
            var lines = markdown.Replace("\r\n", "\n").Split('\n');
            List? list = null;
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                string trimmed = line.Trim();

                if (IsTableRow(trimmed))
                {
                    var rows = new List<string>();
                    while (i < lines.Length && IsTableRow(lines[i].Trim()))
                    {
                        rows.Add(lines[i++].Trim());
                    }
                    i--;
                    list = null;
                    document.Blocks.Add(BuildTable(rows));
                    continue;
                }

                var bullet = Regex.Match(line, @"^\s*(?:[-*•]|\d+[.)])\s+(.*)$");
                if (bullet.Success)
                {
                    if (list == null)
                    {
                        list = new List
                        {
                            MarkerStyle = Regex.IsMatch(line, @"^\s*\d") ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                            Margin = new Thickness(0, 2, 0, 4),
                            Padding = new Thickness(18, 0, 0, 0),
                        };
                        document.Blocks.Add(list);
                    }
                    var item = new Paragraph { Margin = new Thickness(0, 1, 0, 1) };
                    AddInlines(item.Inlines, bullet.Groups[1].Value);
                    list.ListItems.Add(new ListItem(item));
                    continue;
                }
                list = null;

                if (trimmed.Length == 0)
                {
                    continue;
                }
                var heading = Regex.Match(trimmed, @"^(#{1,6})\s+(.*)$");
                var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 6) };
                if (heading.Success)
                {
                    paragraph.FontWeight = FontWeights.SemiBold;
                    paragraph.Margin = new Thickness(0, 4, 0, 4);
                    AddInlines(paragraph.Inlines, heading.Groups[2].Value);
                }
                else
                {
                    AddInlines(paragraph.Inlines, trimmed);
                    // Lines of one paragraph that the model broke by hand stay together.
                    while (i + 1 < lines.Length && lines[i + 1].Trim().Length > 0 && !IsTableRow(lines[i + 1].Trim())
                           && !Regex.IsMatch(lines[i + 1], @"^\s*(?:[-*•]|\d+[.)])\s+|^\s*#{1,6}\s"))
                    {
                        paragraph.Inlines.Add(new LineBreak());
                        AddInlines(paragraph.Inlines, lines[++i].Trim());
                    }
                }
                document.Blocks.Add(paragraph);
            }
            if (document.Blocks.LastBlock is Block last)
            {
                last.Margin = new Thickness(last.Margin.Left, last.Margin.Top, last.Margin.Right, 0);
            }
            Document = document;
        }

        private static bool IsTableRow(string line) => line.StartsWith('|') && line.Length > 1 && line.IndexOf('|', 1) > 0;

        private static bool IsSeparator(string row) => Regex.IsMatch(row, @"^\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?$");

        private static string[] Cells(string row) =>
            row.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToArray();

        private static Table BuildTable(List<string> rows)
        {
            var data = rows.Where(r => !IsSeparator(r)).Select(Cells).ToList();
            bool hasHeader = rows.Count > 1 && IsSeparator(rows[1]);
            int columns = data.Count == 0 ? 1 : data.Max(r => r.Length);
            var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 2, 0, 8), BorderThickness = new Thickness(1, 1, 0, 0) };
            table.SetResourceReference(Table.BorderBrushProperty, "BorderBrush");
            for (int c = 0; c < columns; c++)
            {
                table.Columns.Add(new TableColumn());
            }
            var group = new TableRowGroup();
            for (int r = 0; r < data.Count; r++)
            {
                var row = new TableRow();
                bool header = hasHeader && r == 0;
                if (header)
                {
                    row.SetResourceReference(TableRow.BackgroundProperty, "BackgroundDeepBrush");
                }
                for (int c = 0; c < columns; c++)
                {
                    var paragraph = new Paragraph { Margin = new Thickness(0) };
                    if (header)
                    {
                        paragraph.FontWeight = FontWeights.SemiBold;
                    }
                    AddInlines(paragraph.Inlines, c < data[r].Length ? data[r][c] : string.Empty);
                    var cell = new TableCell(paragraph) { Padding = new Thickness(6, 3, 6, 3), BorderThickness = new Thickness(0, 0, 1, 1) };
                    cell.SetResourceReference(TableCell.BorderBrushProperty, "BorderBrush");
                    row.Cells.Add(cell);
                }
                group.Rows.Add(row);
            }
            table.RowGroups.Add(group);
            return table;
        }

        /// <summary>
        /// [text](url), bare http(s) addresses, **bold**, *italic*, `code`.  Italic needs text right inside both asterisks and
        /// no letter or asterisk outside them, so a "* " bullet, "2 * 3" or a_b*c isn't read as italic.
        /// </summary>
        private static readonly Regex InlinePattern = new(
            @"\[(?<text>[^\]]+)\]\((?<url>https?://[^\s)]+)\)|(?<bare>https?://[^\s<>()\[\]]+[^\s<>()\[\].,;:!?'""])|\*\*(?<bold>.+?)\*\*|(?<![*\w])\*(?<italic>[^*\s](?:[^*]*?[^*\s])?)\*(?![*\w])|`(?<code>[^`]+)`",
            RegexOptions.Compiled);

        private static void AddInlines(InlineCollection inlines, string text)
        {
            int at = 0;
            foreach (Match m in InlinePattern.Matches(text))
            {
                if (m.Index > at)
                {
                    inlines.Add(new Run(text[at..m.Index]));
                }
                if (m.Groups["url"].Success || m.Groups["bare"].Success)
                {
                    string url = m.Groups["url"].Success ? m.Groups["url"].Value : m.Groups["bare"].Value;
                    string shown = m.Groups["text"].Success ? m.Groups["text"].Value : url;
                    if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
                    {
                        var link = new Hyperlink(new Run(shown)) { NavigateUri = uri, ToolTip = uri.AbsoluteUri };
                        link.SetResourceReference(TextElement.ForegroundProperty, "PrimaryBrush");
                        inlines.Add(link);
                    }
                    else
                    {
                        inlines.Add(new Run(m.Value));
                    }
                }
                else if (m.Groups["bold"].Success)
                {
                    var bold = new Bold();
                    AddInlines(bold.Inlines, m.Groups["bold"].Value);
                    inlines.Add(bold);
                }
                else if (m.Groups["italic"].Success)
                {
                    var italic = new Italic();
                    AddInlines(italic.Inlines, m.Groups["italic"].Value);
                    inlines.Add(italic);
                }
                else if (m.Groups["code"].Success)
                {
                    var code = new Run(m.Groups["code"].Value) { FontFamily = new FontFamily("Cascadia Mono, Consolas") };
                    code.SetResourceReference(TextElement.BackgroundProperty, "BackgroundDeepBrush");
                    inlines.Add(code);
                }
                at = m.Index + m.Length;
            }
            if (at < text.Length)
            {
                inlines.Add(new Run(text[at..]));
            }
        }

        /// <summary>The text without Markdown marks, for Copy: links as "text (address)", bullets as "•", table cells separated by tabs.</summary>
        public static string ToPlainText(string markdown)
        {
            var lines = (markdown ?? string.Empty).Replace("\r\n", "\n").Split('\n')
                .Where(l => !IsSeparator(l.Trim()) || !IsTableRow(l.Trim()))
                .Select(l =>
                {
                    string t = l.TrimEnd();
                    if (IsTableRow(t.Trim()))
                    {
                        return string.Join("\t", Cells(t).Select(PlainInline));
                    }
                    t = Regex.Replace(t, @"^(\s*)[-*]\s+", "$1• ");
                    t = Regex.Replace(t, @"^\s*#{1,6}\s+", string.Empty);
                    return PlainInline(t);
                });
            return string.Join("\r\n", lines).Trim();
        }

        private static string PlainInline(string text) => InlinePattern.Replace(text, m =>
            m.Groups["url"].Success ? $"{m.Groups["text"].Value} ({m.Groups["url"].Value})"
            : m.Groups["bare"].Success ? m.Groups["bare"].Value
            : m.Groups["bold"].Success ? PlainInline(m.Groups["bold"].Value)
            : m.Groups["italic"].Success ? PlainInline(m.Groups["italic"].Value)
            : m.Groups["code"].Value);
    }
}
