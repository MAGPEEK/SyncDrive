using System.IO.Compression;
using System.Text;
using System.Text.Encodings.Web;
using System.Xml.Linq;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace SyncDrive.Web.App;

/// <summary>
/// Wandelt Dateiinhalte in etwas, das die Weboberfläche gefahrlos zeigen kann: Text bleibt Text, Markdown und DOCX werden zu HTML aus
/// einer kleinen, festen Menge an Elementen. Nichts aus der Datei landet ungeprüft als HTML auf der Seite (kein Roh-HTML aus Markdown,
/// Links nur http, https, mailto oder Anker, alles andere maskiert).
/// </summary>
public static class DocumentViews
{
    private static readonly MarkdownPipeline Markdown = new MarkdownPipelineBuilder().UseAdvancedExtensions().DisableHtml().Build();

    /// <summary>UTF-8 (mit oder ohne BOM), UTF-16 mit BOM; was kein gültiges UTF-8 ist, gilt als Windows-1252.</summary>
    public static string DecodeText(byte[] bytes)
    {
        if (bytes is [0xFF, 0xFE, ..])
        {
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }

        if (bytes is [0xFE, 0xFF, ..])
        {
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }

        var start = bytes is [0xEF, 0xBB, 0xBF, ..] ? 3 : 0;
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes, start, bytes.Length - start);
        }
        catch (DecoderFallbackException)
        {
            var text = new StringBuilder(bytes.Length);
            foreach (var b in bytes)
            {
                text.Append(Windows1252(b));
            }

            return text.ToString();
        }
    }

    public static string MarkdownToHtml(string markdown)
    {
        var document = Markdig.Markdown.Parse(markdown, Markdown);
        foreach (var link in document.Descendants<LinkInline>())
        {
            if (!IsSafeUrl(link.Url))
            {
                link.Url = string.Empty;
            }
        }

        foreach (var link in document.Descendants<AutolinkInline>())
        {
            if (!IsSafeUrl(link.Url))
            {
                link.Url = string.Empty;
            }
        }

        using var writer = new StringWriter();
        var renderer = new Markdig.Renderers.HtmlRenderer(writer);
        Markdown.Setup(renderer);
        renderer.Render(document);
        return writer.ToString();
    }

    private static bool IsSafeUrl(string? url) =>
        url is null or ""
        || url.StartsWith('#')
        || Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "mailto"
        || !url.Contains(':', StringComparison.Ordinal); // relativ

    private const long MaxDocumentXml = 20 * 1024 * 1024;
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    /// <summary>
    /// Word-Dokument (DOCX) als einfaches HTML: Überschriften, Absätze, Fett/Kursiv/Unterstrichen, Aufzählungen, Tabellen, Zeilenumbrüche.
    /// Null, wenn die Datei kein lesbares DOCX ist.
    /// </summary>
    public static string? DocxToHtml(byte[] docx)
    {
        try
        {
            using var zip = new ZipArchive(new MemoryStream(docx), ZipArchiveMode.Read);
            if (zip.GetEntry("word/document.xml") is not { Length: > 0 and <= MaxDocumentXml } entry)
            {
                return null;
            }

            using var stream = entry.Open();
            var body = XDocument.Load(stream).Root?.Element(W + "body");
            if (body is null)
            {
                return null;
            }

            var html = new StringBuilder();
            WriteBlocks(body.Elements(), html);
            return html.ToString();
        }
        catch (Exception e) when (e is InvalidDataException or System.Xml.XmlException)
        {
            return null;
        }
    }

    private static void WriteBlocks(IEnumerable<XElement> blocks, StringBuilder html)
    {
        var inList = false;
        foreach (var block in blocks)
        {
            var isListItem = block.Name == W + "p" && block.Element(W + "pPr")?.Element(W + "numPr") is not null;
            if (inList && !isListItem)
            {
                html.Append("</ul>");
                inList = false;
            }

            if (block.Name == W + "tbl")
            {
                html.Append("<table>");
                foreach (var row in block.Elements(W + "tr"))
                {
                    html.Append("<tr>");
                    foreach (var cell in row.Elements(W + "tc"))
                    {
                        html.Append("<td>");
                        WriteBlocks(cell.Elements(), html);
                        html.Append("</td>");
                    }

                    html.Append("</tr>");
                }

                html.Append("</table>");
            }
            else if (block.Name == W + "p")
            {
                if (isListItem && !inList)
                {
                    html.Append("<ul>");
                    inList = true;
                }

                var tag = isListItem ? "li" : HeadingTag(block.Element(W + "pPr")?.Element(W + "pStyle")?.Attribute(W + "val")?.Value);
                html.Append('<').Append(tag).Append('>');
                foreach (var run in block.Descendants(W + "r"))
                {
                    WriteRun(run, html);
                }

                html.Append("</").Append(tag).Append('>');
            }
        }

        if (inList)
        {
            html.Append("</ul>");
        }
    }

    /// <summary>„Heading1“ … „Heading6“ und „Title“ werden zu Überschriften, alles andere zum Absatz.</summary>
    private static string HeadingTag(string? style) => style switch
    {
        "Title" => "h1",
        ['H', 'e', 'a', 'd', 'i', 'n', 'g', var level and >= '1' and <= '6'] => "h" + level,
        _ => "p",
    };

    private static void WriteRun(XElement run, StringBuilder html)
    {
        var format = run.Element(W + "rPr");
        string[] tags = [.. new[] { ("b", "strong"), ("i", "em"), ("u", "u") }
            .Where(f => format?.Element(W + f.Item1) is { } on && on.Attribute(W + "val")?.Value is not ("0" or "false" or "none"))
            .Select(f => f.Item2)];
        foreach (var tag in tags)
        {
            html.Append('<').Append(tag).Append('>');
        }

        foreach (var part in run.Elements())
        {
            if (part.Name == W + "t")
            {
                html.Append(HtmlEncoder.Default.Encode(part.Value));
            }
            else if (part.Name == W + "tab")
            {
                html.Append("&emsp;");
            }
            else if (part.Name == W + "br" || part.Name == W + "cr")
            {
                html.Append("<br>");
            }
        }

        foreach (var tag in tags.Reverse())
        {
            html.Append("</").Append(tag).Append('>');
        }
    }

    /// <summary>Gruppen, die keinen Fließtext enthalten (Schriften, Farben, Formatvorlagen, Metadaten, Bilder).</summary>
    private static readonly HashSet<string> RtfSkipped = new(StringComparer.Ordinal)
    {
        "fonttbl", "colortbl", "stylesheet", "info", "pict", "header", "footer", "listtable", "listoverridetable", "themedata",
        "colorschememapping", "latentstyles", "datastore", "xmlnstbl", "rsidtbl", "generator", "object", "fldinst",
    };

    /// <summary>RTF als lesbarer Text: Absätze, Tabulatoren und Sonderzeichen bleiben, Formatierung und eingebettete Objekte fallen weg.</summary>
    public static string RtfToText(string rtf)
    {
        var text = new StringBuilder(rtf.Length / 2);
        var skipDepth = new Stack<bool>();
        var skipping = false;
        var unicodeSkip = 1;
        var pendingSkip = 0;
        for (var i = 0; i < rtf.Length; i++)
        {
            var c = rtf[i];
            if (c == '{')
            {
                skipDepth.Push(skipping);
                continue;
            }

            if (c == '}')
            {
                skipping = skipDepth.Count > 0 && skipDepth.Pop();
                continue;
            }

            if (c == '\\' && i + 1 < rtf.Length)
            {
                var next = rtf[i + 1];
                if (next is '\\' or '{' or '}')
                {
                    if (!skipping)
                    {
                        text.Append(next);
                    }

                    i++;
                    continue;
                }

                if (next == '*')
                {
                    skipping = true; // {\* …}: unbekannte Zieldaten
                    i++;
                    continue;
                }

                if (next == '\'' && i + 3 < rtf.Length)
                {
                    if (!skipping && pendingSkip == 0 && byte.TryParse(rtf.AsSpan(i + 2, 2), System.Globalization.NumberStyles.HexNumber, null, out var code))
                    {
                        text.Append(Windows1252(code));
                    }

                    pendingSkip = Math.Max(0, pendingSkip - 1);
                    i += 3;
                    continue;
                }

                var start = i + 1;
                var end = start;
                while (end < rtf.Length && char.IsAsciiLetter(rtf[end]))
                {
                    end++;
                }

                var word = rtf[start..end];
                var numberStart = end;
                if (end < rtf.Length && rtf[end] == '-')
                {
                    end++;
                }

                while (end < rtf.Length && char.IsAsciiDigit(rtf[end]))
                {
                    end++;
                }

                int? number = end > numberStart && int.TryParse(rtf.AsSpan(numberStart, end - numberStart), out var n) ? n : null;
                if (end < rtf.Length && rtf[end] == ' ')
                {
                    end++; // Trenner gehört zum Steuerwort
                }

                i = end - 1;
                if (RtfSkipped.Contains(word))
                {
                    skipping = true;
                    continue;
                }

                if (skipping)
                {
                    continue;
                }

                switch (word)
                {
                    case "par" or "line" or "row":
                        text.Append('\n');
                        break;
                    case "tab" or "cell":
                        text.Append('\t');
                        break;
                    case "uc" when number is { } count:
                        unicodeSkip = count;
                        break;
                    case "u" when number is { } value:
                        text.Append((char)(value < 0 ? value + 65536 : value));
                        pendingSkip = unicodeSkip;
                        break;
                    case "emdash":
                        text.Append('—');
                        break;
                    case "endash":
                        text.Append('–');
                        break;
                    case "bullet":
                        text.Append('•');
                        break;
                    case "lquote":
                        text.Append('‘');
                        break;
                    case "rquote":
                        text.Append('’');
                        break;
                    case "ldblquote":
                        text.Append('“');
                        break;
                    case "rdblquote":
                        text.Append('”');
                        break;
                }

                continue;
            }

            if (skipping || c is '\r' or '\n')
            {
                continue;
            }

            if (pendingSkip > 0)
            {
                pendingSkip--; // Ersatzzeichen nach \uN
                continue;
            }

            text.Append(c);
        }

        return text.ToString().Trim();
    }

    /// <summary>Windows-1252 ohne Codepage-Anbieter (im Browser nicht vorhanden): Latin-1 bis auf den Bereich 0x80–0x9F.</summary>
    private static char Windows1252(byte b) => b is >= 0x80 and <= 0x9F ? Cp1252High[b - 0x80] : (char)b;

    private const string Cp1252High = "€\u0081‚ƒ„…†‡ˆ‰Š‹Œ\u008DŽ\u008F\u0090‘’“”•–—˜™š›œ\u009DžŸ";
}
