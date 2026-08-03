using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;

namespace ArkFramework
{
    public sealed class CsvTableRow
    {
        internal CsvTableRow(int rowNumber, IReadOnlyList<string> cells)
        {
            RowNumber = rowNumber;
            Cells = new ReadOnlyCollection<string>(
                new List<string>(cells));
        }

        public int RowNumber { get; }

        public IReadOnlyList<string> Cells { get; }
    }

    public sealed class CsvTableDocument
    {
        private CsvTableDocument(
            string sourceName,
            TableSchema schema,
            IReadOnlyList<CsvTableRow> rows)
        {
            SourceName = sourceName;
            Schema = schema;
            Rows = new ReadOnlyCollection<CsvTableRow>(
                new List<CsvTableRow>(rows));
        }

        public string SourceName { get; }

        public TableSchema Schema { get; }

        public IReadOnlyList<CsvTableRow> Rows { get; }

        public static CsvTableDocument Parse(
            string text,
            string sourceName = null)
        {
            if (text == null)
            {
                throw new ArgumentNullException(nameof(text));
            }

            // 直接传入 TextAsset.text 时也可能保留 UTF-8 BOM，统一在解析前移除。
            if (text.Length > 0 && text[0] == '\uFEFF')
            {
                text = text.Substring(1);
            }

            var source = string.IsNullOrWhiteSpace(sourceName)
                ? "<table>"
                : sourceName;
            var rawRows = ParseCsv(text, source);
            var directives = new Dictionary<string, RawRow>(
                StringComparer.OrdinalIgnoreCase);
            var dataRows = new List<RawRow>();
            var dataStarted = false;
            foreach (var row in rawRows)
            {
                if (IsBlank(row) || IsComment(row))
                {
                    continue;
                }

                var first = row.Cells[0].Trim();
                if (first.StartsWith("#", StringComparison.Ordinal))
                {
                    if (dataStarted)
                    {
                        throw new TableFormatException(
                            source,
                            row.RowNumber,
                            "Schema directives must appear before data rows.");
                    }

                    if (!IsKnownDirective(first))
                    {
                        throw new TableFormatException(
                            source,
                            row.RowNumber,
                            $"Unknown schema directive '{first}'.");
                    }

                    if (directives.ContainsKey(first))
                    {
                        throw new TableFormatException(
                            source,
                            row.RowNumber,
                            $"Duplicate schema directive '{first}'.");
                    }

                    directives.Add(first, row);
                    continue;
                }

                dataStarted = true;
                dataRows.Add(row);
            }

            var classRow = RequireDirective(directives, "#class", source);
            var fieldsRow = RequireDirective(directives, "#fields", source);
            var typesRow = RequireDirective(directives, "#types", source);
            var targetTypeName = ReadSingleValue(classRow, source);
            var fields = ReadValues(fieldsRow, source, allowEmpty: false);
            var types = ReadValues(typesRow, source, allowEmpty: false);
            if (fields.Count == 0)
            {
                throw new TableFormatException(
                    source,
                    fieldsRow.RowNumber,
                    "At least one field is required.");
            }

            if (types.Count != fields.Count)
            {
                throw new TableFormatException(
                    source,
                    typesRow.RowNumber,
                    $"#types has {types.Count} values, but #fields has " +
                    $"{fields.Count}.");
            }

            if (fields.Distinct(StringComparer.Ordinal).Count() != fields.Count)
            {
                throw new TableFormatException(
                    source,
                    fieldsRow.RowNumber,
                    "Field names must be unique and case-sensitive.");
            }

            var comments = directives.TryGetValue("#comments", out var commentsRow)
                ? ReadValues(commentsRow, source, allowEmpty: true)
                : Array.Empty<string>();
            if (comments.Count > fields.Count)
            {
                throw new TableFormatException(
                    source,
                    commentsRow.RowNumber,
                    "#comments cannot contain more values than #fields.");
            }

            var key = directives.TryGetValue("#key", out var keyRow)
                ? ReadSingleValue(keyRow, source)
                : null;
            if (key != null && !fields.Contains(key, StringComparer.Ordinal))
            {
                throw new TableFormatException(
                    source,
                    keyRow.RowNumber,
                    $"Key column '{key}' is not declared in #fields.");
            }

            var output = directives.TryGetValue("#output", out var outputRow)
                ? ReadSingleValue(outputRow, source)
                : null;
            var columns = new List<TableColumnSchema>(fields.Count);
            for (var index = 0; index < fields.Count; index++)
            {
                columns.Add(
                    new TableColumnSchema(
                        index,
                        fields[index],
                        types[index],
                        index < comments.Count
                            ? comments[index]
                            : string.Empty));
            }

            var rows = new List<CsvTableRow>(dataRows.Count);
            foreach (var row in dataRows)
            {
                IReadOnlyList<string> cells = row.Cells;
                if (cells.Count == fields.Count + 1 &&
                    string.IsNullOrWhiteSpace(cells[0]))
                {
                    // 表格软件中可将 A 列留给 #fields 等指令，数据从 B 列开始；
                    // 解析时移除这个仅用于视觉对齐的空白单元格。
                    cells = cells.Skip(1).ToArray();
                }

                if (cells.Count != fields.Count)
                {
                    throw new TableFormatException(
                        source,
                        row.RowNumber,
                        $"Data row has {row.Cells.Count} cells, expected " +
                        $"{fields.Count}.");
                }

                rows.Add(new CsvTableRow(row.RowNumber, cells));
            }

            return new CsvTableDocument(
                source,
                new TableSchema(targetTypeName, output, key, columns),
                rows);
        }

        private static IReadOnlyList<RawRow> ParseCsv(
            string text,
            string source)
        {
            // 使用状态机而非 Split，确保引号内的逗号、转义引号和换行不丢失。
            var rows = new List<RawRow>();
            var cells = new List<string>();
            var cell = new StringBuilder();
            var rowNumber = 1;
            var currentLine = 1;
            var quoted = false;
            var afterQuote = false;
            var rowTouched = false;

            for (var index = 0; index < text.Length; index++)
            {
                var character = text[index];
                if (quoted)
                {
                    if (character == '"')
                    {
                        if (index + 1 < text.Length && text[index + 1] == '"')
                        {
                            cell.Append('"');
                            index++;
                        }
                        else
                        {
                            quoted = false;
                            afterQuote = true;
                        }

                        continue;
                    }

                    if (character == '\r' || character == '\n')
                    {
                        if (character == '\r' &&
                            index + 1 < text.Length &&
                            text[index + 1] == '\n')
                        {
                            index++;
                        }

                        cell.Append('\n');
                        currentLine++;
                        continue;
                    }

                    cell.Append(character);
                    continue;
                }

                if (afterQuote &&
                    character != ',' &&
                    character != '\r' &&
                    character != '\n')
                {
                    throw new TableFormatException(
                        source,
                        currentLine,
                        "Only a comma or line break may follow a closing quote.");
                }

                if (character == '"')
                {
                    if (cell.Length != 0)
                    {
                        throw new TableFormatException(
                            source,
                            currentLine,
                            "A quoted cell must begin with a quote.");
                    }

                    quoted = true;
                    rowTouched = true;
                    continue;
                }

                if (character == ',')
                {
                    cells.Add(cell.ToString());
                    cell.Clear();
                    afterQuote = false;
                    rowTouched = true;
                    continue;
                }

                if (character == '\r' || character == '\n')
                {
                    cells.Add(cell.ToString());
                    cell.Clear();
                    rows.Add(new RawRow(rowNumber, cells));
                    cells = new List<string>();
                    afterQuote = false;
                    rowTouched = false;
                    if (character == '\r' &&
                        index + 1 < text.Length &&
                        text[index + 1] == '\n')
                    {
                        index++;
                    }

                    currentLine++;
                    rowNumber = currentLine;
                    continue;
                }

                cell.Append(character);
                afterQuote = false;
                rowTouched = true;
            }

            if (quoted)
            {
                throw new TableFormatException(
                    source,
                    rowNumber,
                    "Quoted cell is not closed.");
            }

            if (rowTouched || cells.Count > 0 || cell.Length > 0)
            {
                cells.Add(cell.ToString());
                rows.Add(new RawRow(rowNumber, cells));
            }

            return rows;
        }

        private static bool IsKnownDirective(string value)
        {
            return string.Equals(value, "#class", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "#output", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "#fields", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "#types", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "#key", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "#comments", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsBlank(RawRow row)
        {
            return row.Cells.All(string.IsNullOrWhiteSpace);
        }

        private static bool IsComment(RawRow row)
        {
            // A 列以 // 开头时整行忽略，可用于说明文字或临时禁用数据。
            return row.Cells.Count > 0 &&
                   row.Cells[0].TrimStart()
                       .StartsWith("//", StringComparison.Ordinal);
        }

        private static RawRow RequireDirective(
            IReadOnlyDictionary<string, RawRow> directives,
            string name,
            string source)
        {
            if (!directives.TryGetValue(name, out var row))
            {
                throw new TableFormatException(
                    source,
                    0,
                    $"Required schema directive '{name}' is missing.");
            }

            return row;
        }

        private static string ReadSingleValue(RawRow row, string source)
        {
            var values = ReadValues(row, source, allowEmpty: false);
            if (values.Count != 1)
            {
                throw new TableFormatException(
                    source,
                    row.RowNumber,
                    $"Directive '{row.Cells[0]}' requires exactly one value.");
            }

            return values[0];
        }

        private static IReadOnlyList<string> ReadValues(
            RawRow row,
            string source,
            bool allowEmpty)
        {
            if (row.Cells.Count < 2)
            {
                throw new TableFormatException(
                    source,
                    row.RowNumber,
                    $"Directive '{row.Cells[0]}' requires a value.");
            }

            var values = row.Cells.Skip(1)
                .Select(value => value.Trim())
                .ToArray();
            if (!allowEmpty && values.Any(string.IsNullOrEmpty))
            {
                throw new TableFormatException(
                    source,
                    row.RowNumber,
                    $"Directive '{row.Cells[0]}' contains an empty value.");
            }

            return values;
        }

        private sealed class RawRow
        {
            public RawRow(int rowNumber, IReadOnlyList<string> cells)
            {
                RowNumber = rowNumber;
                Cells = cells;
            }

            public int RowNumber { get; }

            public IReadOnlyList<string> Cells { get; }
        }
    }
}
