using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Orbit.Reporting;

/// <summary>Renders a report's rows as a simple tabular PDF via QuestPDF (Community licence), with any charts it has.</summary>
public static class ReportPdfBuilder
{
    /// <summary>One part of the document: a <see cref="Table"/> or a set of <see cref="Charts"/>.</summary>
    public abstract record Section;

    /// <summary>
    /// A captioned table. <paramref name="Widths"/> gives each column a relative width; omitted, the columns share the page equally.
    /// <paramref name="FontSize"/> shrinks the text of a table too wide for the page's 10pt; the caption keeps its size.
    /// </summary>
    public sealed record Table(
        IReadOnlyList<string> Headers, IReadOnlyList<IReadOnlyList<string>> Rows, string? Caption = null, IReadOnlyList<float>? Widths = null,
        float? FontSize = null) : Section;

    /// <summary>A chart: its caption and its SVG (<see cref="SvgCharts"/>), drawn the width of its cell.</summary>
    public sealed record Chart(string Caption, string Svg);

    /// <summary>Charts side by side, <paramref name="PerRow"/> to a row.</summary>
    public sealed record Charts(IReadOnlyList<Chart> Items, int PerRow = 2) : Section;

    /// <summary>An A4 document of <paramref name="sections"/>; <paramref name="landscape"/> turns the page for a wide table.</summary>
    public static byte[] Build(string title, string subtitle, IReadOnlyList<Section> sections, bool landscape = false)
    {
        return Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(landscape ? PageSizes.A4.Landscape() : PageSizes.A4);
                page.Margin(36);
                page.DefaultTextStyle(x => x.FontSize(10));

                page.Header().Column(col =>
                {
                    col.Item().Text(title).FontSize(18).SemiBold();
                    col.Item().Text(subtitle).FontColor(Colors.Grey.Darken1);
                    col.Item().PaddingTop(6).LineHorizontal(1).LineColor(Colors.Grey.Lighten2);
                });

                page.Content().PaddingVertical(12).Column(col =>
                {
                    col.Spacing(16);
                    foreach (var part in sections)
                    {
                        if (part is Charts charts)
                        {
                            foreach (var row in charts.Items.Chunk(Math.Max(1, charts.PerRow)))
                                col.Item().ShowEntire().Row(r =>
                                {
                                    r.Spacing(24);
                                    foreach (var chart in row)
                                        r.RelativeItem().Column(c =>
                                        {
                                            c.Item().PaddingBottom(4).Text(chart.Caption).SemiBold().FontSize(12);
                                            c.Item().Svg(chart.Svg).FitWidth();
                                        });
                                    for (var empty = row.Length; empty < charts.PerRow; empty++) r.RelativeItem();
                                });
                            continue;
                        }
                        if (part is not Table table) continue;
                        col.Item().Column(section =>
                        {
                            if (!string.IsNullOrWhiteSpace(table.Caption))
                                section.Item().PaddingBottom(4).Text(table.Caption).SemiBold().FontSize(12);

                            if (table.Rows.Count == 0)
                            {
                                section.Item().Text("No data for the selected filters.").Italic().FontColor(Colors.Grey.Darken1);
                                return;
                            }

                            section.Item().DefaultTextStyle(x => table.FontSize is float size ? x.FontSize(size) : x).Table(t =>
                            {
                                t.ColumnsDefinition(cd =>
                                {
                                    for (var i = 0; i < table.Headers.Count; i++)
                                        cd.RelativeColumn(table.Widths is { } w && i < w.Count ? w[i] : 1);
                                });
                                t.Header(h =>
                                {
                                    foreach (var header in table.Headers)
                                        h.Cell().Element(HeaderCell).Text(header).SemiBold();
                                });
                                foreach (var row in table.Rows)
                                    foreach (var cell in row)
                                        t.Cell().Element(BodyCell).Text(cell);
                            });
                        });
                    }
                });

                page.Footer().AlignCenter().Text(x =>
                {
                    x.DefaultTextStyle(s => s.FontSize(8).FontColor(Colors.Grey.Darken1));
                    x.Span($"Orbit - generated {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC - page ");
                    x.CurrentPageNumber();
                    x.Span(" of ");
                    x.TotalPages();
                });
            });
        }).GeneratePdf();
    }

    private static IContainer HeaderCell(IContainer c) => c
        .Background(Colors.Grey.Lighten3)
        .BorderBottom(1).BorderColor(Colors.Grey.Lighten1)
        .PaddingVertical(5).PaddingHorizontal(6);

    private static IContainer BodyCell(IContainer c) => c
        .BorderBottom(1).BorderColor(Colors.Grey.Lighten3)
        .PaddingVertical(4).PaddingHorizontal(6);
}
