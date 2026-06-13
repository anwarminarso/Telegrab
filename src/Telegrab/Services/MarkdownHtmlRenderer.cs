using Markdig;

namespace Telegrab.Services;

/// <summary>
/// Konversi teks Markdown → dokumen HTML lengkap untuk ditampilkan di <c>WebView</c>
/// (penampil Markdown Fase 2, Requirement 10.1).
///
/// Tipe LOGIKA MURNI (string → string) tanpa dependensi MAUI, sehingga bisa dilink &amp;
/// diuji di proyek test. Bagian yang berurusan dengan <c>WebView</c>/HtmlWebViewSource berada
/// di <see cref="Telegrab.ViewModels.MarkdownViewerViewModel"/>.
/// </summary>
public static class MarkdownHtmlRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    /// <summary>
    /// Render <paramref name="markdown"/> menjadi dokumen HTML lengkap dengan styling dasar
    /// yang nyaman dibaca (font, lebar maksimum, ramah mode gelap/terang).
    ///
    /// Bila <paramref name="baseHref"/> diberikan (URI <c>file://</c> absolut ke folder dokumen,
    /// diakhiri pemisah), sebuah tag <c>&lt;base&gt;</c> disisipkan sehingga tautan media relatif
    /// di README (mis. <c>[file](file.jpg)</c>) ter-resolve ke path absolut yang benar
    /// (Requirement 10.2).
    /// </summary>
    public static string Render(string? markdown, string? baseHref = null)
    {
        var body = Markdown.ToHtml(markdown ?? string.Empty, Pipeline);

        var baseTag = string.IsNullOrWhiteSpace(baseHref)
            ? string.Empty
            : $"<base href=\"{HtmlAttributeEscape(baseHref!)}\">";

        return
            "<!DOCTYPE html>" +
            "<html><head>" +
            "<meta charset=\"utf-8\">" +
            "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">" +
            baseTag +
            "<style>" + Css + "</style>" +
            "</head><body><main class=\"telegrab-doc\">" +
            body +
            "</main></body></html>";
    }

    private static string HtmlAttributeEscape(string value) =>
        value.Replace("&", "&amp;").Replace("\"", "&quot;").Replace("<", "&lt;").Replace(">", "&gt;");

    private const string Css = """
        :root { color-scheme: light dark; }
        body {
            font-family: -apple-system, "Segoe UI", Roboto, Helvetica, Arial, sans-serif;
            font-size: 15px;
            line-height: 1.6;
            margin: 0;
            padding: 24px 20px 48px;
            color: #1f2329;
            background: #ffffff;
        }
        main.telegrab-doc { max-width: 820px; margin: 0 auto; }
        h1, h2, h3, h4 { line-height: 1.3; font-weight: 600; margin: 1.4em 0 0.5em; }
        h1 { font-size: 1.7em; } h2 { font-size: 1.4em; } h3 { font-size: 1.2em; }
        a { color: #2f80ed; text-decoration: none; word-break: break-word; }
        a:hover { text-decoration: underline; }
        code { font-family: "Cascadia Code", Consolas, monospace; font-size: 0.9em;
               background: rgba(127,127,127,0.15); padding: 0.15em 0.35em; border-radius: 4px; }
        pre { background: rgba(127,127,127,0.12); padding: 12px 14px; border-radius: 8px; overflow:auto; }
        pre code { background: none; padding: 0; }
        blockquote { margin: 0.8em 0; padding: 0.2em 1em; border-left: 3px solid rgba(127,127,127,0.4);
                     color: #555; }
        img { max-width: 100%; height: auto; border-radius: 8px; }
        table { border-collapse: collapse; width: 100%; margin: 1em 0; }
        th, td { border: 1px solid rgba(127,127,127,0.35); padding: 6px 10px; text-align: left; }
        hr { border: none; border-top: 1px solid rgba(127,127,127,0.3); margin: 1.5em 0; }
        ul, ol { padding-left: 1.4em; }
        @media (prefers-color-scheme: dark) {
            body { color: #e6e6e6; background: #1b1c1e; }
            a { color: #5aa3ff; }
            blockquote { color: #b0b0b0; }
        }
        """;
}
