using System.IO;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using MarkdownEditor.Core;
using MarkdownEditor.Latex;
using SkiaSharp;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;

namespace MarkdownEditor.Export;

/// <summary>
/// 将 Markdown AST 导出为 Word DOCX。
/// 公式：当前以占位文本或嵌图方式输出；后续可扩展 OMML 矢量导出。
/// </summary>
public sealed class DocxExporter : IMarkdownExporter
{
    public string FormatId => "docx";
    public string DisplayName => "Word (DOCX)";
    public string[] FileExtensions => ["docx"];

    public Task<ExportResult> ExportAsync(
        string markdown,
        string documentBasePath,
        string outputPath,
        ExportOptions? options = null,
        CancellationToken ct = default)
    {
        try
        {
            var doc = MarkdownParser.Parse(markdown);
            using (var wordDoc = WordprocessingDocument.Create(outputPath, WordprocessingDocumentType.Document))
            {
                var mainPart = wordDoc.AddMainDocumentPart();
                mainPart.Document = new Document();
                var body = mainPart.Document.AppendChild(new Body());

                AddStyles(mainPart);

                foreach (var child in doc.Children)
                    AppendBlock(body, child, documentBasePath, mainPart);
            }
            return Task.FromResult(new ExportResult(true));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ExportResult(false, ex.Message));
        }
    }

    private static void AddStyles(MainDocumentPart mainPart)
    {
        var stylePart = mainPart.AddNewPart<StyleDefinitionsPart>();
        stylePart.Styles = new Styles();
        var styles = stylePart.Styles;

        styles.AppendChild(new DocDefaults(
            new RunPropertiesDefault(new RunProperties(new RunFonts { Ascii = "Calibri", HighAnsi = "Calibri" }, new FontSize { Val = "22" }))));

        var style1 = new Style();
        style1.Type = StyleValues.Paragraph;
        style1.StyleId = "Normal";
        style1.CustomStyle = true;
        style1.StyleName = new StyleName { Val = "Normal" };
        style1.StyleParagraphProperties = new StyleParagraphProperties();
        styles.AppendChild(style1);

        for (int level = 1; level <= 6; level++)
        {
            var fontSize = (24 - level * 2).ToString();
            var style = new Style();
            style.Type = StyleValues.Paragraph;
            style.StyleId = "Heading" + level;
            style.CustomStyle = true;
            style.StyleName = new StyleName { Val = "Heading " + level };
            style.StyleParagraphProperties = new StyleParagraphProperties();
            style.StyleRunProperties = new StyleRunProperties { Bold = new Bold(), FontSize = new FontSize { Val = fontSize } };
            styles.AppendChild(style);
        }

        var codeStyle = new Style();
        codeStyle.Type = StyleValues.Paragraph;
        codeStyle.StyleId = "Code";
        codeStyle.CustomStyle = true;
        codeStyle.StyleName = new StyleName { Val = "Code" };
        codeStyle.StyleRunProperties = new StyleRunProperties { RunFonts = new RunFonts { Ascii = "Consolas", HighAnsi = "Consolas" }, FontSize = new FontSize { Val = "20" } };
        styles.AppendChild(codeStyle);
    }

    private static void AppendBlock(Body body, MarkdownNode node, string documentBasePath, MainDocumentPart mainPart)
    {
        switch (node)
        {
            case HeadingNode h:
                body.AppendChild(CreateParagraphWithStyle("Heading" + Math.Clamp(h.Level, 1, 6), FlattenInlineToText(h.Content)));
                break;
            case ParagraphNode p:
                body.AppendChild(CreateParagraphWithRuns(p.Content, documentBasePath));
                break;
            case CodeBlockNode c:
                foreach (var line in (c.Code ?? "").Split('\n'))
                    body.AppendChild(CreateParagraphWithStyle("Code", line));
                break;
            case BlockquoteNode bq:
                foreach (var child in bq.Children)
                    AppendBlock(body, child, documentBasePath, mainPart);
                break;
            case BulletListNode ul:
                foreach (var item in ul.Items)
                    AppendListItem(body, item, documentBasePath, mainPart);
                break;
            case OrderedListNode ol:
                int num = ol.StartNumber;
                foreach (var item in ol.Items)
                {
                    AppendOrderedListItem(body, item, documentBasePath, mainPart, num);
                    num++;
                }
                break;
            case TableNode t:
                body.AppendChild(CreateTable(t));
                break;
            case HorizontalRuleNode:
                body.AppendChild(new Paragraph(new Run(new Text(""))));
                break;
            case EmptyLineNode e:
                for (int i = 0; i < Math.Min(e.LineCount, 2); i++)
                    body.AppendChild(new Paragraph());
                break;
            case MathBlockNode m:
                var mathPara = CreateFormulaImageParagraph(mainPart, m.LaTeX ?? "");
                if (mathPara != null)
                    body.AppendChild(mathPara);
                else
                    body.AppendChild(CreateParagraphWithStyle("Normal", "[公式: " + (m.LaTeX ?? "") + "]"));
                break;
            case HtmlBlockNode html:
                body.AppendChild(CreateParagraphWithStyle("Normal", (html.RawHtml ?? "").Replace("\n", " ")));
                break;
            case DefinitionListNode dl:
                foreach (var item in dl.Items)
                {
                    body.AppendChild(CreateParagraphWithRuns(item.Term, documentBasePath));
                    foreach (var def in item.Definitions)
                        AppendBlock(body, def, documentBasePath, mainPart);
                }
                break;
            default:
                break;
        }
    }

    private static Paragraph CreateParagraphWithStyle(string styleId, string text)
    {
        var p = new Paragraph(
            new ParagraphProperties(new ParagraphStyleId { Val = styleId }),
            new Run(new Text(text) { Space = SpaceProcessingModeValues.Preserve }));
        return p;
    }

    private static Paragraph CreateParagraphWithRuns(List<InlineNode> inlines, string documentBasePath)
    {
        var p = new Paragraph(new ParagraphProperties());
        foreach (var run in CreateRunsFromInlines(inlines, documentBasePath))
            p.AppendChild(run);
        if (p.ChildElements.Count == 0)
            p.AppendChild(new Run(new Text("")));
        return p;
    }

    private static List<Run> CreateRunsFromInlines(List<InlineNode> inlines, string documentBasePath)
    {
        var list = new List<Run>();
        foreach (var n in inlines)
        {
            switch (n)
            {
                case TextNode t:
                    list.Add(new Run(new Text(t.Content ?? "") { Space = SpaceProcessingModeValues.Preserve }));
                    break;
                case BoldNode b:
                    list.Add(new Run(new RunProperties(new Bold()), new Text(FlattenInlineToText(b.Content)) { Space = SpaceProcessingModeValues.Preserve }));
                    break;
                case ItalicNode i:
                    list.Add(new Run(new RunProperties(new Italic()), new Text(FlattenInlineToText(i.Content)) { Space = SpaceProcessingModeValues.Preserve }));
                    break;
                case StrikethroughNode s:
                    list.Add(new Run(new RunProperties(new Strike()), new Text(FlattenInlineToText(s.Content)) { Space = SpaceProcessingModeValues.Preserve }));
                    break;
                case CodeNode c:
                    list.Add(new Run(new RunProperties(new RunFonts { Ascii = "Consolas", HighAnsi = "Consolas" }), new Text(c.Content ?? "") { Space = SpaceProcessingModeValues.Preserve }));
                    break;
                case LinkNode ln:
                    list.Add(new Run(new Text(ln.Text ?? "") { Space = SpaceProcessingModeValues.Preserve }));
                    break;
                case ImageNode img:
                    list.Add(new Run(new Text("[" + (img.Alt ?? "图") + "]") { Space = SpaceProcessingModeValues.Preserve }));
                    break;
                case MathInlineNode math:
                    list.Add(new Run(new Text(" " + (math.LaTeX ?? "") + " ") { Space = SpaceProcessingModeValues.Preserve }));
                    break;
                case FootnoteRefNode fn:
                    list.Add(new Run(new Text("[^" + (fn.Id ?? "") + "]") { Space = SpaceProcessingModeValues.Preserve }));
                    break;
                default:
                    break;
            }
        }
        return list;
    }

    private static void AppendListItem(Body body, ListItemNode item, string documentBasePath, MainDocumentPart mainPart)
    {
        var p = new Paragraph(new ParagraphProperties(new Indentation { Left = "720" }));
        if (item.IsTask)
            p.AppendChild(new Run(new Text((item.IsChecked ? "[x] " : "[ ] ")) { Space = SpaceProcessingModeValues.Preserve }));
        foreach (var child in item.Content)
        {
            if (child is ParagraphNode para)
                foreach (var run in CreateRunsFromInlines(para.Content, documentBasePath))
                    p.AppendChild(run);
            else
                AppendBlock(body, child, documentBasePath, mainPart);
        }
        body.AppendChild(p);
    }

    private static void AppendOrderedListItem(Body body, ListItemNode item, string documentBasePath, MainDocumentPart mainPart, int number)
    {
        var p = new Paragraph(new ParagraphProperties(new Indentation { Left = "720" }));
        p.AppendChild(new Run(new Text(number + ". ") { Space = SpaceProcessingModeValues.Preserve }));
        foreach (var child in item.Content)
        {
            if (child is ParagraphNode para)
                foreach (var run in CreateRunsFromInlines(para.Content, documentBasePath))
                    p.AppendChild(run);
            else
                AppendBlock(body, child, documentBasePath, mainPart);
        }
        body.AppendChild(p);
    }

    private static Table CreateTable(TableNode t)
    {
        var table = new Table(
            new TableProperties(
                new TableBorders(
                    new TopBorder { Val = new DocumentFormat.OpenXml.EnumValue<BorderValues>(BorderValues.Single), Size = 4 },
                    new BottomBorder { Val = new DocumentFormat.OpenXml.EnumValue<BorderValues>(BorderValues.Single), Size = 4 },
                    new LeftBorder { Val = new DocumentFormat.OpenXml.EnumValue<BorderValues>(BorderValues.Single), Size = 4 },
                    new RightBorder { Val = new DocumentFormat.OpenXml.EnumValue<BorderValues>(BorderValues.Single), Size = 4 },
                    new InsideHorizontalBorder { Val = new DocumentFormat.OpenXml.EnumValue<BorderValues>(BorderValues.Single), Size = 4 },
                    new InsideVerticalBorder { Val = new DocumentFormat.OpenXml.EnumValue<BorderValues>(BorderValues.Single), Size = 4 })));

        var headerRow = new TableRow();
        foreach (var h in t.Headers)
            headerRow.AppendChild(new TableCell(new Paragraph(new Run(new Text(h)))) { TableCellProperties = new TableCellProperties(new Shading { Val = ShadingPatternValues.Clear, Fill = "E0E0E0" }) });
        table.AppendChild(headerRow);

        foreach (var row in t.Rows)
        {
            var tr = new TableRow();
            foreach (var cell in row)
                tr.AppendChild(new TableCell(new Paragraph(new Run(new Text(cell)))));
            table.AppendChild(tr);
        }
        return table;
    }

    private static string FlattenInlineToText(List<InlineNode> inlines)
    {
        var sb = new StringBuilder();
        foreach (var n in inlines)
        {
            if (n is TextNode t) sb.Append(t.Content);
            else if (n is BoldNode b) sb.Append(FlattenInlineToText(b.Content));
            else if (n is ItalicNode i) sb.Append(FlattenInlineToText(i.Content));
            else if (n is StrikethroughNode s) sb.Append(FlattenInlineToText(s.Content));
            else if (n is CodeNode c) sb.Append(c.Content);
            else if (n is LinkNode ln) sb.Append(ln.Text);
            else if (n is ImageNode img) sb.Append(img.Alt);
            else if (n is MathInlineNode m) sb.Append(m.LaTeX);
            else if (n is FootnoteRefNode fn) sb.Append("[^" + fn.Id + "]");
        }
        return sb.ToString();
    }

    private const float FormulaFontSize = 20f;
    private const float FormulaScale = 2f;
    private const float FormulaPadding = 8f;

    /// <summary>将 LaTeX 公式渲染为 PNG 流（用于 DOCX 嵌图），并返回像素宽高。</summary>
    private static (byte[]? pngBytes, int widthPx, int heightPx) RenderFormulaToPng(string latex)
    {
        if (string.IsNullOrWhiteSpace(latex)) return (null, 0, 0);
        SKTypeface? bodyTf = null;
        SKTypeface? mathTf = null;
        try
        {
            bodyTf = SKTypeface.FromFamilyName("Times New Roman", SKFontStyleWeight.Normal, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);
            mathTf = SKTypeface.FromFamilyName("Latin Modern Math", SKFontStyleWeight.Normal, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);
            if (mathTf?.FamilyName != "Latin Modern Math") mathTf = SKTypeface.FromFamilyName("Cambria Math", SKFontStyleWeight.Normal, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);
            mathTf ??= bodyTf;
            bodyTf ??= SKTypeface.Default;
        }
        catch
        {
            bodyTf ??= SKTypeface.Default;
            mathTf ??= bodyTf;
        }

        var (width, height, depth) = MathSkiaRenderer.MeasureFormula(latex, bodyTf, mathTf, FormulaFontSize);
        if (width <= 0 || (height + depth) <= 0) return (null, 0, 0);

        float totalW = width + FormulaPadding;
        float totalH = height + depth + FormulaPadding;
        int pxW = Math.Max(1, (int)Math.Ceiling(totalW * FormulaScale));
        int pxH = Math.Max(1, (int)Math.Ceiling(totalH * FormulaScale));

        using var bmp = new SKBitmap(pxW, pxH, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(SKColors.White);
            canvas.Scale(FormulaScale);
            var bounds = new SKRect(0, 0, totalW, totalH);
            MathSkiaRenderer.DrawFormula(canvas, bounds, latex, bodyTf, mathTf, FormulaFontSize);
        }

        using var ms = new MemoryStream();
        if (!bmp.Encode(ms, SKEncodedImageFormat.Png, 100)) return (null, 0, 0);
        return (ms.ToArray(), pxW, pxH);
    }

    /// <summary>创建包含居中公式图片的段落，用于块级公式。</summary>
    private static Paragraph? CreateFormulaImageParagraph(MainDocumentPart mainPart, string latex)
    {
        var (pngBytes, pxW, pxH) = RenderFormulaToPng(latex);
        if (pngBytes == null || pngBytes.Length == 0 || pxW <= 0 || pxH <= 0) return null;

        ImagePart imagePart = mainPart.AddImagePart(ImagePartType.Png);
        using (var stream = new MemoryStream(pngBytes))
            imagePart.FeedData(stream);

        string relationshipId = mainPart.GetIdOfPart(imagePart);

        // 96 DPI: 1 pixel = 914400/96 EMU
        long cx = (long)Math.Round(pxW * 914400.0 / 96.0);
        long cy = (long)Math.Round(pxH * 914400.0 / 96.0);

        var drawing = new Drawing(
            new DW.Inline(
                new DW.Extent() { Cx = cx, Cy = cy },
                new DW.EffectExtent() { LeftEdge = 0L, TopEdge = 0L, RightEdge = 0L, BottomEdge = 0L },
                new DW.DocProperties() { Id = (UInt32Value)1U, Name = "Formula" },
                new DW.NonVisualGraphicFrameDrawingProperties(new A.GraphicFrameLocks() { NoChangeAspect = true }),
                new A.Graphic(
                    new A.GraphicData(
                        new PIC.Picture(
                            new PIC.NonVisualPictureProperties(
                                new PIC.NonVisualDrawingProperties() { Id = (UInt32Value)0U, Name = "Formula.png" },
                                new PIC.NonVisualPictureDrawingProperties()),
                            new PIC.BlipFill(
                                new A.Blip(
                                    new A.BlipExtensionList(
                                        new A.BlipExtension() { Uri = "{28A0092B-C50C-407E-A947-70E740481C1C}" }))
                                { Embed = relationshipId, CompressionState = A.BlipCompressionValues.Print },
                                new A.Stretch(new A.FillRectangle())),
                            new PIC.ShapeProperties(
                                new A.Transform2D(new A.Offset() { X = 0L, Y = 0L }, new A.Extents() { Cx = cx, Cy = cy }),
                                new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle })))
                    { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" })
            )
            {
                DistanceFromTop = (UInt32Value)0U,
                DistanceFromBottom = (UInt32Value)0U,
                DistanceFromLeft = (UInt32Value)0U,
                DistanceFromRight = (UInt32Value)0U,
                EditId = "50D07946"
            });

        var run = new Run(drawing);
        var para = new Paragraph(
            new ParagraphProperties(new Justification() { Val = JustificationValues.Center }),
            run);
        return para;
    }
}
