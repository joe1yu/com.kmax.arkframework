using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace ArkFramework
{
    public sealed class TableColumnSchema
    {
        internal TableColumnSchema(
            int index,
            string name,
            string typeName,
            string comment)
        {
            Index = index;
            Name = name;
            TypeName = typeName;
            Comment = comment ?? string.Empty;
        }

        public int Index { get; }

        public string Name { get; }

        public string TypeName { get; }

        public string Comment { get; }
    }

    public sealed class TableSchema
    {
        internal TableSchema(
            string targetTypeName,
            string outputDirectory,
            string keyColumnName,
            IReadOnlyList<TableColumnSchema> columns)
        {
            TargetTypeName = targetTypeName;
            OutputDirectory = outputDirectory;
            KeyColumnName = keyColumnName;
            Columns = new ReadOnlyCollection<TableColumnSchema>(
                new List<TableColumnSchema>(columns));
        }

        public string TargetTypeName { get; }

        public string OutputDirectory { get; }

        public string KeyColumnName { get; }

        public IReadOnlyList<TableColumnSchema> Columns { get; }

        public bool HasKey => !string.IsNullOrEmpty(KeyColumnName);
    }

    public sealed class TableFormatException : FormatException
    {
        public TableFormatException(
            string sourceName,
            int rowNumber,
            string message,
            Exception innerException = null)
            : base(FormatMessage(sourceName, rowNumber, message), innerException)
        {
            SourceName = sourceName;
            RowNumber = rowNumber;
        }

        public string SourceName { get; }

        public int RowNumber { get; }

        private static string FormatMessage(
            string sourceName,
            int rowNumber,
            string message)
        {
            var source = string.IsNullOrWhiteSpace(sourceName)
                ? "<table>"
                : sourceName;
            return rowNumber > 0
                ? $"{source} (row {rowNumber}): {message}"
                : $"{source}: {message}";
        }
    }

    public sealed class TableParseException : FormatException
    {
        public TableParseException(
            string sourceName,
            int rowNumber,
            string columnName,
            string message,
            Exception innerException = null)
            : base(
                $"{sourceName} (row {rowNumber}, column " +
                $"'{columnName}'): {message}",
                innerException)
        {
            SourceName = sourceName;
            RowNumber = rowNumber;
            ColumnName = columnName;
        }

        public string SourceName { get; }

        public int RowNumber { get; }

        public string ColumnName { get; }
    }
}
