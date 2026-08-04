namespace Zdtllm.Core.Tui;

/// <summary>One visual (screen) row produced by soft-wrapping a logical editor line: which logical
/// line it came from, the column offset the segment starts at, and the segment text.</summary>
public readonly record struct VisualRow(int LogicalRow, int StartCol, string Text);

/// <summary>
/// Pure soft-wrap layout for the bottom input box. Turns the editor's LOGICAL lines into the VISUAL
/// rows a fixed-width box actually paints — a line longer than <c>width</c> flows onto continuation
/// rows instead of being clipped — and maps the (row, col) cursor to its visual row index and column
/// so the caller can position it. Console-free and unit-testable in isolation.
/// </summary>
public static class SoftWrap
{
    /// <param name="lines">The editor's logical lines (always ≥ 1; may contain empty strings).</param>
    /// <param name="width">Visible content columns per row (excludes the box prefix). Floored at 1.</param>
    /// <param name="cursorRow">Logical cursor row.</param>
    /// <param name="cursorCol">Logical cursor column within that row.</param>
    /// <returns>
    /// <c>Rows</c> — the visual rows top-to-bottom. <c>CursorIndex</c> — index into Rows where the
    /// cursor sits. <c>CursorCol</c> — the cursor's column within that visual row, in <c>[0, width]</c>
    /// (width means "just past the last character of a full segment", i.e. the insert point at the edge).
    /// </returns>
    public static (IReadOnlyList<VisualRow> Rows, int CursorIndex, int CursorCol) Layout(
        IReadOnlyList<string> lines, int width, int cursorRow, int cursorCol)
    {
        ArgumentNullException.ThrowIfNull(lines);
        width = Math.Max(1, width);

        var rows = new List<VisualRow>();
        int cursorIndex = 0, cursorViscol = 0;

        for (int lr = 0; lr < lines.Count; lr++)
        {
            var line = lines[lr] ?? string.Empty;
            int firstRowOfLine = rows.Count;

            // An empty line still occupies one visual row; otherwise ceil(len / width) segments.
            int segCount = line.Length == 0 ? 1 : (line.Length + width - 1) / width;
            for (int s = 0; s < segCount; s++)
            {
                int start = s * width;
                int len = Math.Min(width, line.Length - start);
                rows.Add(new VisualRow(lr, start, line.Substring(start, Math.Max(0, len))));
            }

            if (lr == cursorRow)
            {
                int col = Math.Clamp(cursorCol, 0, line.Length);
                // Cursor at the exact end of a full last segment (col a multiple of width) stays on
                // that segment at column == width, rather than spilling to a phantom empty row.
                int seg = Math.Min(col / width, segCount - 1);
                cursorIndex = firstRowOfLine + seg;
                cursorViscol = col - seg * width; // in [0, width]
            }
        }

        if (rows.Count == 0) rows.Add(new VisualRow(0, 0, string.Empty)); // defensive; editor never empty
        return (rows, cursorIndex, cursorViscol);
    }
}
