namespace GevSharp.Cli.Commands;

/// <summary>열 너비를 내용에 맞춰 정렬하는 단순 텍스트 표. 셀은 문자열로만 받고 왼쪽 정렬한다.</summary>
public sealed class TextTable
{
    private readonly string[] _headers;
    private readonly List<string[]> _rows = new();

    public TextTable(params string[] headers)
    {
        if (headers is null || headers.Length == 0) throw new ArgumentException("at least one header is required", nameof(headers));
        _headers = headers;
    }

    public int RowCount => _rows.Count;

    public void AddRow(params string?[] cells)
    {
        if (cells is null || cells.Length != _headers.Length)
            throw new ArgumentException($"expected {_headers.Length} cells", nameof(cells));
        var row = new string[cells.Length];
        for (var i = 0; i < cells.Length; i++) row[i] = cells[i] ?? string.Empty;
        _rows.Add(row);
    }

    public void Write(TextWriter writer)
    {
        var widths = new int[_headers.Length];
        for (var i = 0; i < _headers.Length; i++) widths[i] = _headers[i].Length;
        foreach (var row in _rows)
        {
            for (var i = 0; i < row.Length; i++) widths[i] = Math.Max(widths[i], row[i].Length);
        }

        writer.WriteLine(Line(_headers, widths));
        writer.WriteLine(string.Join("  ", widths.Select(w => new string('-', w))));
        foreach (var row in _rows) writer.WriteLine(Line(row, widths));
    }

    private static string Line(string[] cells, int[] widths)
    {
        var parts = new string[cells.Length];
        for (var i = 0; i < cells.Length; i++)
        {
            // 마지막 열은 채우지 않는다 — 줄 끝 공백을 남기지 않기 위해.
            parts[i] = i == cells.Length - 1 ? cells[i] : cells[i].PadRight(widths[i]);
        }
        return string.Join("  ", parts).TrimEnd();
    }
}
