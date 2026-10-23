using System.IO.Compression;
using System.Text;
using SyncDrive.Web.App;

namespace SyncDrive.Web.Tests;

/// <summary>Anzeige von Dateien in der Oberfläche (0.34.0): lesbar, aber nie als ungeprüftes HTML.</summary>
public sealed class DocumentViewsTests
{
    [Fact]
    public void Markdown_is_rendered_without_raw_html_or_script_links()
    {
        var html = DocumentViews.MarkdownToHtml(
            "# Titel\n\n**fett** und [gut](https://example.org) und [böse](javascript:alert(1))\n\n<script>alert(1)</script>\n\n| a | b |\n|---|---|\n| 1 | 2 |");

        Assert.Contains("<h1", html, StringComparison.Ordinal);
        Assert.Contains("<strong>fett</strong>", html, StringComparison.Ordinal);
        Assert.Contains("href=\"https://example.org\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("javascript:", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<table>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void A_word_document_becomes_headings_paragraphs_lists_and_tables_with_escaped_text()
    {
        const string w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        var xml = $"""
            <w:document xmlns:w="{w}"><w:body>
              <w:p><w:pPr><w:pStyle w:val="Heading1"/></w:pPr><w:r><w:t>Angebot</w:t></w:r></w:p>
              <w:p><w:r><w:rPr><w:b/></w:rPr><w:t>Preis:</w:t></w:r><w:r><w:t xml:space="preserve"> 5 &lt;script&gt;</w:t></w:r></w:p>
              <w:p><w:pPr><w:numPr/></w:pPr><w:r><w:t>Punkt</w:t></w:r></w:p>
              <w:tbl><w:tr><w:tc><w:p><w:r><w:t>Zelle</w:t></w:r></w:p></w:tc></w:tr></w:tbl>
            </w:body></w:document>
            """;
        using var docx = new MemoryStream();
        using (var zip = new ZipArchive(docx, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var entry = new StreamWriter(zip.CreateEntry("word/document.xml").Open());
            entry.Write(xml);
        }

        var html = DocumentViews.DocxToHtml(docx.ToArray());

        Assert.Equal(
            "<h1>Angebot</h1><p><strong>Preis:</strong> 5 &lt;script&gt;</p><ul><li>Punkt</li></ul><table><tr><td><p>Zelle</p></td></tr></table>",
            html);
        Assert.Null(DocumentViews.DocxToHtml("kein zip"u8.ToArray()));
    }

    [Fact]
    public void Rtf_keeps_text_paragraphs_and_special_characters_and_drops_formatting()
    {
        const string rtf = @"{\rtf1\ansi\ansicpg1252{\fonttbl{\f0 Arial;}}{\colortbl;\red0\green0\blue0;}{\*\generator Test;}" +
            @"\f0\fs24 Gr\'fc\'dfe aus \b K\'f6ln\b0 !\par Zweite Zeile\tab mit \" + "u8364? Euro" + @"\par}"; // Unicode-Steuerwort u8364

        Assert.Equal("Grüße aus Köln!\nZweite Zeile\tmit € Euro", DocumentViews.RtfToText(rtf)); // das Leerzeichen nach \b0 trennt nur
    }

    [Fact]
    public void Text_is_read_as_utf8_or_else_as_windows_1252()
    {
        Assert.Equal("Grüße", DocumentViews.DecodeText(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("Grüße")).ToArray()));
        Assert.Equal("Grüße €", DocumentViews.DecodeText([0x47, 0x72, 0xFC, 0xDF, 0x65, 0x20, 0x80]));
    }

    [Theory]
    [InlineData("Bericht.PDF", ViewerKind.Pdf)]
    [InlineData("notiz.md", ViewerKind.Markdown)]
    [InlineData("brief.docx", ViewerKind.Docx)]
    [InlineData("alt.rtf", ViewerKind.Rtf)]
    [InlineData("log.txt", ViewerKind.Text)]
    [InlineData("seite.html", ViewerKind.Text)] // HTML nur als Quelltext, nie ausgeführt
    [InlineData("foto.jpg", ViewerKind.Image)]
    [InlineData("tabelle.xlsx", ViewerKind.None)]
    public void Files_are_shown_by_their_extension(string name, ViewerKind kind) =>
        Assert.Equal(kind, ViewerViewModel.KindOf(new DriveItem(name, false, 1, DateTimeOffset.UtcNow, 1)));
}
