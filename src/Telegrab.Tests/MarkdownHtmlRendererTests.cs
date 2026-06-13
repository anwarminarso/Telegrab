using Telegrab.Services;

namespace Telegrab.Tests;

/// <summary>
/// Tes unit untuk <see cref="MarkdownHtmlRenderer"/> (penampil Markdown Fase 2, Requirement 10).
/// Konversi Markdown → HTML adalah logika murni (string → string) tanpa MAUI.
/// </summary>
public class MarkdownHtmlRendererTests
{
    [Fact]
    public void Render_WrapsBodyInHtmlDocument()
    {
        var html = MarkdownHtmlRenderer.Render("# Title\n\nHello **world**.");

        Assert.StartsWith("<!DOCTYPE html>", html);
        Assert.Contains("<html>", html);
        Assert.Contains("</html>", html);
        // Heading & bold dirender oleh Markdig.
        Assert.Contains("<h1", html);
        Assert.Contains("<strong>world</strong>", html);
    }

    [Fact]
    public void Render_EmitsBaseHref_WhenProvided()
    {
        var html = MarkdownHtmlRenderer.Render("[file](file.jpg)", "file:///C:/docs/chat/");

        Assert.Contains("<base href=\"file:///C:/docs/chat/\">", html);
        // Tautan relatif tetap relatif di markup; <base> yang membuatnya ter-resolve di WebView.
        Assert.Contains("href=\"file.jpg\"", html);
    }

    [Fact]
    public void Render_OmitsBaseHref_WhenNotProvided()
    {
        var html = MarkdownHtmlRenderer.Render("plain text");

        Assert.DoesNotContain("<base", html);
    }

    [Fact]
    public void Render_HandlesNullOrEmpty_WithoutThrowing()
    {
        var fromNull = MarkdownHtmlRenderer.Render(null);
        var fromEmpty = MarkdownHtmlRenderer.Render(string.Empty);

        Assert.Contains("<body>", fromNull);
        Assert.Contains("<body>", fromEmpty);
    }
}
