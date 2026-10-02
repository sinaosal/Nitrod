using System;
using System.Text;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Nitrox.Launcher.Models.Utils;

internal static class MarkdownRichTextConverter
{
    private static readonly MarkdownPipeline pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

    public static string Convert(string markdown)
    {
        MarkdownDocument document = Markdown.Parse(markdown, pipeline);
        StringBuilder output = new();
        AppendBlocks(document, output);
        return output.ToString().Trim();
    }

    private static void AppendBlocks(ContainerBlock blocks, StringBuilder output)
    {
        foreach (Block block in blocks)
        {
            switch (block)
            {
                case HeadingBlock heading:
                    output.Append("[b]");
                    AppendInlines(heading.Inline, output);
                    output.Append("[/b]\n\n");
                    break;
                case ParagraphBlock paragraph:
                    AppendInlines(paragraph.Inline, output);
                    output.Append("\n\n");
                    break;
                case ListBlock list:
                    foreach (Block item in list)
                    {
                        output.Append("• ");
                        AppendBlocks((ContainerBlock)item, output);
                    }
                    output.Append('\n');
                    break;
                case QuoteBlock quote:
                    AppendBlocks(quote, output);
                    break;
                case FencedCodeBlock code:
                    output.Append("[i]");
                    output.Append(code.Lines.ToString());
                    output.Append("[/i]\n\n");
                    break;
                case LeafBlock { Inline: { } inlines }:
                    AppendInlines(inlines, output);
                    output.Append("\n\n");
                    break;
                case ContainerBlock nested:
                    AppendBlocks(nested, output);
                    break;
            }
        }
    }

    private static void AppendInlines(ContainerInline? inlines, StringBuilder output)
    {
        if (inlines == null)
        {
            return;
        }

        foreach (Inline inline in inlines)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    output.Append(literal.Content.ToString());
                    break;
                case EmphasisInline emphasis:
                    string tag = emphasis.DelimiterCount > 1 ? "b" : "i";
                    output.Append('[').Append(tag).Append(']');
                    AppendInlines(emphasis, output);
                    output.Append("[/").Append(tag).Append(']');
                    break;
                case LinkInline link:
                    output.Append('[');
                    AppendInlines(link, output);
                    output.Append("](").Append(link.Url).Append(')');
                    break;
                case CodeInline code:
                    output.Append("[i]").Append(code.Content).Append("[/i]");
                    break;
                case LineBreakInline:
                    output.Append('\n');
                    break;
                case HtmlInline:
                    break;
                case ContainerInline container:
                    AppendInlines(container, output);
                    break;
            }
        }
    }
}