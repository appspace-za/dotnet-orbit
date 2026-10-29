using System.Globalization;
using System.Security;
using System.Text;

namespace Orbit.Reporting;

/// <summary>
/// Small charts drawn as SVG text, so a report's page and its PDF show the same picture (§12 Sprint status): the page puts the SVG
/// in the document, QuestPDF draws it into the PDF. Two forms, both stacked:
/// <list type="bullet">
/// <item><see cref="HorizontalBars"/>: one bar per item on a shared scale, a label column on the left and each bar's total at its
/// tip, so it needs no value axis.</item>
/// <item><see cref="Columns"/>: one column per item on a value axis with clean ticks, labels along the bottom.</item>
/// </list>
/// Marks follow the report charts' house style: bars at most 16px thick, a 2px gap between stacked segments, a 4px rounded data
/// end and a square base, hairline gridlines, a legend whenever there are two or more series, and text in the text colours, never
/// a series colour. Each mark carries a <c>&lt;title&gt;</c>, which the browser shows on hover and the PDF ignores. Every mark also
/// carries both a light-theme <c>fill</c> (what the PDF uses) and a class whose <c>app.css</c> rule takes the theme's colour on
/// screen, light or dark (CSS wins over the attribute). The layout is worked out apart from the drawing (<see cref="LayoutBars"/>,
/// <see cref="LayoutColumns"/>) so it can be tested.
/// </summary>
public static class SvgCharts
{
    /// <summary>A series: its legend label, the class that colours it on screen, and its light-theme colour for the PDF.</summary>
    public sealed record Series(string Label, string CssClass, string Fill);

    /// <summary>
    /// A bar or column: its label (a column's may have two lines, split by "\n"), one value per series, the text at a bar's tip
    /// (its total by default), and the name its tooltips use (its label by default). <see cref="Muted"/> greys the label;
    /// <see cref="GapBefore"/> sets a bar apart from the ones above it.
    /// </summary>
    public sealed record Item(
        string Label, IReadOnlyList<double> Values, string? Tip = null, string? Title = null, bool Muted = false, bool GapBefore = false)
    {
        public double Total => Values.Where(v => v > 0).Sum();
        public string Name => Title ?? Label.Replace('\n', ' ');
    }

    /// <summary>One stacked segment: its series, its value, and where it starts and how long it is along the value axis, in px.</summary>
    public sealed record Segment(int Series, double Value, double Start, double Length);

    public sealed record BarRow(Item Item, double Y, IReadOnlyList<Segment> Segments);

    /// <summary>The horizontal bars' layout: the value axis starts at <see cref="LabelWidth"/>; <see cref="Scale"/> is px per unit.</summary>
    public sealed record BarsLayout(double Width, double Height, double Top, double LabelWidth, double Scale, IReadOnlyList<BarRow> Rows);

    public sealed record ColumnRow(Item Item, double X, IReadOnlyList<Segment> Segments, bool Labelled);

    /// <summary>The columns' layout: the plot runs from <see cref="PlotTop"/> down to its baseline at <see cref="PlotBottom"/>.</summary>
    public sealed record ColumnsLayout(
        double Width, double Height, double PlotLeft, double PlotTop, double PlotBottom, double ColumnWidth, double Scale,
        IReadOnlyList<double> Ticks, IReadOnlyList<ColumnRow> Columns);

    private const double FontSize = 12, CharWidth = 6.6, LegendHeight = 28, RowHeight = 26, BarThickness = 16, Gap = 2, Radius = 4;
    private const double MaxColumnWidth = 24, LabelHeight = 34;
    // QuestPDF draws SVG text only in a font it holds itself - Lato, which it ships - and draws nothing for a system font or a list of
    // fonts. app.css gives the screen the page's own font, which overrides this attribute.
    private const string Font = "Lato";
    // Text and chrome, light theme (the PDF's); app.css gives the screen the theme's own.
    private const string TextFill = "#1f2937", SecondaryFill = "#6b7280", MutedFill = "#9ca3af", GridStroke = "#e5e7eb", AxisStroke = "#d1d5db";

    /// <summary>Roughly how wide <paramref name="text"/> is at the charts' font size: enough to lay labels out without measuring.</summary>
    public static double TextWidth(string text) => text.Length * CharWidth;

    public static BarsLayout LayoutBars(IReadOnlyList<Item> items, IReadOnlyList<Series> series, IReadOnlyList<string> tips, double width)
    {
        var top = series.Count >= 2 ? LegendHeight : 4;
        var labelWidth = Math.Clamp(items.Select(i => TextWidth(i.Label)).DefaultIfEmpty(0).Max() + 12, 60, width * 0.34);
        var tipWidth = Math.Min(tips.Select(TextWidth).DefaultIfEmpty(0).Max() + 10, width * 0.3);
        var max = items.Select(i => i.Total).DefaultIfEmpty(0).Max();
        var scale = max <= 0 ? 0 : (width - labelWidth - tipWidth) / max;

        var rows = new List<BarRow>();
        var y = top;
        foreach (var item in items)
        {
            if (item.GapBefore && rows.Count > 0) y += RowHeight / 2;
            rows.Add(new BarRow(item, y, Stack(item, scale, labelWidth)));
            y += RowHeight;
        }
        return new BarsLayout(width, y + 4, top, labelWidth, scale, rows);
    }

    public static ColumnsLayout LayoutColumns(
        IReadOnlyList<Item> items, IReadOnlyList<Series> series, double tickUnit, double minTickStep, double width, double height)
    {
        var top = (series.Count >= 2 ? LegendHeight : 4) + FontSize / 2;
        var ticks = Ticks(items.Select(i => i.Total).DefaultIfEmpty(0).Max(), tickUnit, minTickStep);
        var plotLeft = 44.0;
        var plotBottom = height - LabelHeight;
        var scale = ticks[^1] <= 0 ? 0 : (plotBottom - top) / ticks[^1];
        var slot = items.Count == 0 ? 0 : (width - plotLeft) / items.Count;
        var columnWidth = Math.Min(MaxColumnWidth, slot * 0.6);
        // Label every column when there is room for its label, otherwise every few.
        var widest = items.SelectMany(i => i.Label.Split('\n')).Select(TextWidth).DefaultIfEmpty(0).Max() + 4;
        var every = slot <= 0 ? 1 : Math.Max(1, (int)Math.Ceiling(widest / slot));
        var columns = items.Select((item, i) => new ColumnRow(item, plotLeft + i * slot + (slot - columnWidth) / 2,
            Stack(item, scale, 0), i % every == 0)).ToList();
        return new ColumnsLayout(width, height, plotLeft, top, plotBottom, columnWidth, scale, ticks, columns);
    }

    /// <summary>
    /// A value axis's ticks from zero, in the values' unit: <paramref name="unit"/> (60 for minutes shown as hours) times 1, 2 or 5
    /// times a power of ten - never less than <paramref name="minStep"/> units (half an hour, say) - about four steps up to the
    /// first tick at or above <paramref name="max"/>.
    /// </summary>
    public static IReadOnlyList<double> Ticks(double max, double unit = 1, double minStep = 0)
    {
        if (max <= 0) return [0];
        var raw = max / unit / 4;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        var step = Math.Max(new[] { 1.0, 2, 5, 10 }.Select(m => m * magnitude).First(s => s >= raw), minStep) * unit;
        var ticks = new List<double>();
        for (var t = 0.0; ; t += step)
        {
            ticks.Add(Math.Round(t, 6));
            if (t >= max - 1e-9) break;
        }
        return ticks;
    }

    private static List<Segment> Stack(Item item, double scale, double start)
    {
        var segments = new List<Segment>();
        var at = start;
        for (var s = 0; s < item.Values.Count; s++)
        {
            var value = item.Values[s];
            if (value <= 0) continue; // nothing to draw, and nothing to hover
            segments.Add(new Segment(s, value, at, value * scale));
            at += value * scale;
        }
        return segments;
    }

    /// <summary>
    /// Horizontal stacked bars. <paramref name="valueText"/> writes a value for the tooltips and, unless an item has its own tip, the
    /// total at its bar's tip. Nothing to draw gives <paramref name="emptyText"/> on its own.
    /// </summary>
    public static string HorizontalBars(
        IReadOnlyList<Item> items, IReadOnlyList<Series> series, Func<double, string> valueText, string emptyText, string title,
        double width = 640)
    {
        if (items.All(i => i.Total <= 0)) return Empty(emptyText, title, width);
        var tips = items.Select(i => i.Tip ?? valueText(i.Total)).ToList();
        var layout = LayoutBars(items, series, tips, width);
        var svg = Open(layout.Width, layout.Height, title);
        Legend(svg, series);

        var first = layout.Rows[0].Y;
        var last = layout.Rows[^1].Y + RowHeight;
        Line(svg, layout.LabelWidth, first + 2, layout.LabelWidth, last - 2, "oc-axis", AxisStroke);
        for (var r = 0; r < layout.Rows.Count; r++)
        {
            var row = layout.Rows[r];
            var barTop = row.Y + (RowHeight - BarThickness) / 2;
            var middle = row.Y + RowHeight / 2;
            if (row.Item.GapBefore && r > 0)
                Line(svg, 0, row.Y - RowHeight / 4, layout.Width, row.Y - RowHeight / 4, "oc-grid", GridStroke);
            Text(svg, layout.LabelWidth - 8, middle, Fit(row.Item.Label, layout.LabelWidth - 12), row.Item.Muted ? "oc-muted" : "oc-strong",
                row.Item.Muted ? MutedFill : TextFill, "end", row.Item.Name);
            for (var i = 0; i < row.Segments.Count; i++)
            {
                var seg = row.Segments[i];
                var isEnd = i == row.Segments.Count - 1;
                var length = Math.Max(seg.Length - (isEnd ? 0 : Gap), 1);
                var mark = series[seg.Series];
                var tooltip = $"{row.Item.Name} · {mark.Label}: {valueText(seg.Value)}";
                if (isEnd) Shape(svg, BarEnd(seg.Start, barTop, length, BarThickness), mark, tooltip);
                else Rect(svg, seg.Start, barTop, length, BarThickness, mark, tooltip);
            }
            var end = row.Segments.Count == 0 ? layout.LabelWidth : row.Segments[^1].Start + row.Segments[^1].Length;
            Text(svg, end + 6, middle, tips[r], "oc-text", SecondaryFill, "start");
        }
        return svg.Append("</svg>").ToString();
    }

    /// <summary>
    /// Stacked columns on a value axis whose ticks are multiples of <paramref name="tickUnit"/>, at least
    /// <paramref name="minTickStep"/> units apart (<see cref="Ticks"/>), written by <paramref name="tickText"/>;
    /// <paramref name="valueText"/> writes the tooltips. Nothing to draw gives <paramref name="emptyText"/>.
    /// </summary>
    public static string Columns(
        IReadOnlyList<Item> items, IReadOnlyList<Series> series, Func<double, string> valueText, Func<double, string> tickText,
        double tickUnit, string emptyText, string title, double minTickStep = 0, double width = 640, double height = 240)
    {
        if (items.All(i => i.Total <= 0)) return Empty(emptyText, title, width);
        var layout = LayoutColumns(items, series, tickUnit, minTickStep, width, height);
        var svg = Open(layout.Width, layout.Height, title);
        Legend(svg, series);

        foreach (var tick in layout.Ticks)
        {
            var y = layout.PlotBottom - tick * layout.Scale;
            if (tick > 0) Line(svg, layout.PlotLeft, y, layout.Width, y, "oc-grid", GridStroke);
            Text(svg, layout.PlotLeft - 8, y, tickText(tick), "oc-text", SecondaryFill, "end");
        }
        Line(svg, layout.PlotLeft, layout.PlotBottom, layout.Width, layout.PlotBottom, "oc-axis", AxisStroke);

        foreach (var column in layout.Columns)
        {
            for (var i = 0; i < column.Segments.Count; i++)
            {
                var seg = column.Segments[i];
                var isEnd = i == column.Segments.Count - 1;
                var length = Math.Max(seg.Length - (isEnd ? 0 : Gap), 1);
                var mark = series[seg.Series];
                var tooltip = $"{column.Item.Name} · {mark.Label}: {valueText(seg.Value)}";
                var y = layout.PlotBottom - seg.Start - length;
                if (isEnd) Shape(svg, ColumnEnd(column.X, y, layout.ColumnWidth, length), mark, tooltip);
                else Rect(svg, column.X, y, layout.ColumnWidth, length, mark, tooltip);
            }
            if (!column.Labelled) continue;
            var lines = column.Item.Label.Split('\n');
            for (var l = 0; l < lines.Length; l++)
                Text(svg, column.X + layout.ColumnWidth / 2, layout.PlotBottom + 12 + l * 14, lines[l],
                    column.Item.Muted ? "oc-muted" : "oc-text", column.Item.Muted ? MutedFill : SecondaryFill, "middle", column.Item.Name);
        }
        return svg.Append("</svg>").ToString();
    }

    private static string Empty(string text, string title, double width)
    {
        var svg = Open(width, 32, title);
        Text(svg, 0, 16, text, "oc-text", SecondaryFill, "start");
        return svg.Append("</svg>").ToString();
    }

    private static StringBuilder Open(double width, double height, string title) =>
        new StringBuilder()
            .Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {N(width)} {N(height)}\" width=\"{N(width)}\" height=\"{N(height)}\"")
            .Append($" font-family=\"{Font}\" font-size=\"{N(FontSize)}\" role=\"img\" aria-label=\"{Escape(title)}\">");

    private static void Legend(StringBuilder svg, IReadOnlyList<Series> series)
    {
        if (series.Count < 2) return; // one series: the chart's title says what it is
        var x = 0.0;
        foreach (var s in series)
        {
            svg.Append($"<rect x=\"{N(x)}\" y=\"6\" width=\"10\" height=\"10\" rx=\"2\" class=\"{s.CssClass}\" fill=\"{s.Fill}\"/>");
            Text(svg, x + 14, 11, s.Label, "oc-text", SecondaryFill, "start");
            x += 14 + TextWidth(s.Label) + 18;
        }
    }

    private static void Rect(StringBuilder svg, double x, double y, double w, double h, Series s, string tooltip) =>
        svg.Append($"<rect x=\"{N(x)}\" y=\"{N(y)}\" width=\"{N(w)}\" height=\"{N(h)}\" class=\"{s.CssClass}\" fill=\"{s.Fill}\">")
            .Append($"<title>{Escape(tooltip)}</title></rect>");

    private static void Shape(StringBuilder svg, string path, Series s, string tooltip) =>
        svg.Append($"<path d=\"{path}\" class=\"{s.CssClass}\" fill=\"{s.Fill}\"><title>{Escape(tooltip)}</title></path>");

    /// <summary>A bar's last segment: square where it joins the one before, rounded at the data end.</summary>
    private static string BarEnd(double x, double y, double w, double h)
    {
        var r = Math.Min(Radius, Math.Min(w, h / 2));
        return $"M{N(x)},{N(y)} H{N(x + w - r)} A{N(r)},{N(r)} 0 0 1 {N(x + w)},{N(y + r)} V{N(y + h - r)} " +
               $"A{N(r)},{N(r)} 0 0 1 {N(x + w - r)},{N(y + h)} H{N(x)} Z";
    }

    /// <summary>A column's top segment: square at the bottom, rounded at the top.</summary>
    private static string ColumnEnd(double x, double y, double w, double h)
    {
        var r = Math.Min(Radius, Math.Min(h, w / 2));
        return $"M{N(x)},{N(y + h)} V{N(y + r)} A{N(r)},{N(r)} 0 0 1 {N(x + r)},{N(y)} H{N(x + w - r)} " +
               $"A{N(r)},{N(r)} 0 0 1 {N(x + w)},{N(y + r)} V{N(y + h)} Z";
    }

    private static void Line(StringBuilder svg, double x1, double y1, double x2, double y2, string css, string stroke) =>
        svg.Append($"<line x1=\"{N(x1)}\" y1=\"{N(y1)}\" x2=\"{N(x2)}\" y2=\"{N(y2)}\" class=\"{css}\" stroke=\"{stroke}\" stroke-width=\"1\"/>");

    /// <summary>Text centred on <paramref name="middle"/> (SVG places text by its baseline, so it drops by a third of the font size).</summary>
    private static void Text(StringBuilder svg, double x, double middle, string text, string css, string fill, string anchor, string? tooltip = null)
    {
        svg.Append($"<text x=\"{N(x)}\" y=\"{N(middle + FontSize / 3)}\" text-anchor=\"{anchor}\" class=\"{css}\" fill=\"{fill}\">{Escape(text)}");
        if (tooltip is not null && tooltip != text) svg.Append($"<title>{Escape(tooltip)}</title>");
        svg.Append("</text>");
    }

    /// <summary>A label cut with "…" to fit <paramref name="width"/>; the full text is in its tooltip.</summary>
    private static string Fit(string text, double width)
    {
        var max = Math.Max(1, (int)Math.Floor(width / CharWidth + 1e-9)); // a label laid out to fit exactly still fits
        return text.Length <= max ? text : text[..Math.Max(0, max - 1)].TrimEnd() + "…";
    }

    private static string Escape(string text) => SecurityElement.Escape(text) ?? "";

    private static string N(double v) => Math.Round(v, 2).ToString("0.##", CultureInfo.InvariantCulture);
}
