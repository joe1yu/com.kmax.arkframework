using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Reflection;

namespace ArkFramework
{
    public sealed class TableData<T>
    {
        private readonly Dictionary<object, T> _rowsByKey;

        internal TableData(
            CsvTableDocument document,
            IReadOnlyList<T> rows)
        {
            SourceName = document.SourceName;
            Schema = document.Schema;
            Rows = new ReadOnlyCollection<T>(new List<T>(rows));
            _rowsByKey = BuildKeyIndex(document, rows);
        }

        public string SourceName { get; }

        public TableSchema Schema { get; }

        public IReadOnlyList<T> Rows { get; }

        public int Count => Rows.Count;

        public bool HasKey => _rowsByKey != null;

        public T Get<TKey>(TKey key)
        {
            EnsureKeyExists();
            if (ReferenceEquals(key, null) ||
                !_rowsByKey.TryGetValue(key, out var row))
            {
                throw new KeyNotFoundException(
                    $"Table '{SourceName}' does not contain key '{key}'.");
            }

            return row;
        }

        public bool TryGet<TKey>(TKey key, out T row)
        {
            EnsureKeyExists();
            if (ReferenceEquals(key, null))
            {
                row = default;
                return false;
            }

            return _rowsByKey.TryGetValue(key, out row);
        }

        private static Dictionary<object, T> BuildKeyIndex(
            CsvTableDocument document,
            IReadOnlyList<T> rows)
        {
            if (!document.Schema.HasKey)
            {
                return null;
            }

            var binding = TableMemberBinding.Find(
                typeof(T),
                document.Schema.KeyColumnName,
                requireSetter: false);
            if (binding == null)
            {
                throw new TableFormatException(
                    document.SourceName,
                    0,
                    $"Target type '{typeof(T).FullName}' has no public key " +
                    $"member '{document.Schema.KeyColumnName}'.");
            }

            var index = new Dictionary<object, T>();
            for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
            {
                var key = binding.Get(rows[rowIndex]);
                var sourceRow = document.Rows[rowIndex];
                if (key == null)
                {
                    throw new TableParseException(
                        document.SourceName,
                        sourceRow.RowNumber,
                        document.Schema.KeyColumnName,
                        "Key value cannot be null.");
                }

                if (index.ContainsKey(key))
                {
                    throw new TableParseException(
                        document.SourceName,
                        sourceRow.RowNumber,
                        document.Schema.KeyColumnName,
                        $"Duplicate key '{key}'.");
                }

                index.Add(key, rows[rowIndex]);
            }

            return index;
        }

        private void EnsureKeyExists()
        {
            if (_rowsByKey == null)
            {
                throw new InvalidOperationException(
                    $"Table '{SourceName}' does not declare #key.");
            }
        }
    }

    internal static class TableRowMapper
    {
        public static TableData<T> Map<T>(CsvTableDocument document)
        {
            if (document == null)
            {
                throw new ArgumentNullException(nameof(document));
            }

            // 字段绑定只计算一次，再逐行转换，避免为每个单元格重复反射查找。
            var bindings = document.Schema.Columns
                .Select(
                    column => CreateBinding<T>(document, column))
                .ToArray();
            var rows = new List<T>(document.Rows.Count);
            foreach (var sourceRow in document.Rows)
            {
                object target;
                try
                {
                    // 统一在装箱后的实例上赋值，同时正确支持 class 与 struct。
                    target = Activator.CreateInstance(typeof(T));
                }
                catch (Exception exception)
                {
                    throw new TableFormatException(
                        document.SourceName,
                        sourceRow.RowNumber,
                        $"Target type '{typeof(T).FullName}' must expose a " +
                        "parameterless constructor.",
                        exception);
                }

                for (var index = 0; index < bindings.Length; index++)
                {
                    var column = document.Schema.Columns[index];
                    try
                    {
                        var value = TableValueConverter.Convert(
                            sourceRow.Cells[index],
                            bindings[index].ValueType);
                        bindings[index].Set(target, value);
                    }
                    catch (Exception exception)
                    {
                        throw new TableParseException(
                            document.SourceName,
                            sourceRow.RowNumber,
                            column.Name,
                            $"Value '{sourceRow.Cells[index]}' cannot be " +
                            $"converted to {bindings[index].ValueType.Name}.",
                            exception);
                    }
                }

                rows.Add((T)target);
            }

            return new TableData<T>(document, rows);
        }

        private static TableMemberBinding CreateBinding<T>(
            CsvTableDocument document,
            TableColumnSchema column)
        {
            var binding = TableMemberBinding.Find(
                typeof(T),
                column.Name,
                requireSetter: true);
            if (binding == null)
            {
                throw new TableFormatException(
                    document.SourceName,
                    0,
                    $"Target type '{typeof(T).FullName}' has no writable " +
                    $"public member '{column.Name}'.");
            }

            if (!TableTypeNames.Matches(column.TypeName, binding.ValueType))
            {
                throw new TableFormatException(
                    document.SourceName,
                    0,
                    $"Schema type '{column.TypeName}' for '{column.Name}' " +
                    $"does not match target member type " +
                    $"'{binding.ValueType.FullName}'.");
            }

            return binding;
        }
    }

    internal sealed class TableMemberBinding
    {
        private readonly PropertyInfo _property;
        private readonly FieldInfo _field;

        private TableMemberBinding(PropertyInfo property)
        {
            _property = property;
            ValueType = property.PropertyType;
        }

        private TableMemberBinding(FieldInfo field)
        {
            _field = field;
            ValueType = field.FieldType;
        }

        public Type ValueType { get; }

        public static TableMemberBinding Find(
            Type type,
            string name,
            bool requireSetter)
        {
            var property = type.GetProperty(
                name,
                BindingFlags.Instance | BindingFlags.Public);
            if (property != null &&
                property.GetIndexParameters().Length == 0 &&
                property.GetMethod != null &&
                (!requireSetter || property.SetMethod != null))
            {
                return new TableMemberBinding(property);
            }

            var field = type.GetField(
                name,
                BindingFlags.Instance | BindingFlags.Public);
            if (field != null && (!requireSetter || !field.IsInitOnly))
            {
                return new TableMemberBinding(field);
            }

            return null;
        }

        public object Get(object target)
        {
            return _property != null
                ? _property.GetValue(target)
                : _field.GetValue(target);
        }

        public void Set(object target, object value)
        {
            if (_property != null)
            {
                _property.SetValue(target, value);
            }
            else
            {
                _field.SetValue(target, value);
            }
        }
    }

    internal static class TableTypeNames
    {
        private static readonly IReadOnlyDictionary<string, Type> Aliases =
            new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase)
            {
                ["string"] = typeof(string),
                ["bool"] = typeof(bool),
                ["byte"] = typeof(byte),
                ["sbyte"] = typeof(sbyte),
                ["short"] = typeof(short),
                ["ushort"] = typeof(ushort),
                ["int"] = typeof(int),
                ["uint"] = typeof(uint),
                ["long"] = typeof(long),
                ["ulong"] = typeof(ulong),
                ["float"] = typeof(float),
                ["double"] = typeof(double),
                ["decimal"] = typeof(decimal),
                ["char"] = typeof(char),
                ["guid"] = typeof(Guid),
                ["datetime"] = typeof(DateTime)
            };

        public static bool Matches(string schemaName, Type targetType)
        {
            var name = schemaName.Trim();
            if (name.EndsWith("?", StringComparison.Ordinal))
            {
                var underlying = Nullable.GetUnderlyingType(targetType);
                return underlying != null &&
                       Matches(name.Substring(0, name.Length - 1), underlying);
            }

            if (name.EndsWith("[]", StringComparison.Ordinal))
            {
                return targetType.IsArray &&
                       Matches(
                           name.Substring(0, name.Length - 2),
                           targetType.GetElementType());
            }

            if (Aliases.TryGetValue(name, out var aliasType))
            {
                return targetType == aliasType;
            }

            return string.Equals(
                       name,
                       targetType.Name,
                       StringComparison.Ordinal) ||
                   string.Equals(
                       name,
                       targetType.FullName,
                       StringComparison.Ordinal);
        }
    }

    internal static class TableValueConverter
    {
        public static object Convert(string value, Type targetType)
        {
            var nullableType = Nullable.GetUnderlyingType(targetType);
            if (nullableType != null)
            {
                return string.IsNullOrWhiteSpace(value)
                    ? null
                    : Convert(value, nullableType);
            }

            if (targetType == typeof(string))
            {
                return value;
            }

            if (targetType.IsArray)
            {
                if (string.IsNullOrEmpty(value))
                {
                    return Array.CreateInstance(targetType.GetElementType(), 0);
                }

                var values = value.Split('|');
                var array = Array.CreateInstance(
                    targetType.GetElementType(),
                    values.Length);
                for (var index = 0; index < values.Length; index++)
                {
                    array.SetValue(
                        Convert(values[index], targetType.GetElementType()),
                        index);
                }

                return array;
            }

            var normalized = value.Trim();
            if (targetType == typeof(bool))
            {
                if (string.Equals(normalized, "1", StringComparison.Ordinal))
                {
                    return true;
                }

                if (string.Equals(normalized, "0", StringComparison.Ordinal))
                {
                    return false;
                }

                return bool.Parse(normalized);
            }

            if (targetType == typeof(char))
            {
                if (normalized.Length != 1)
                {
                    throw new FormatException(
                        "A char cell must contain exactly one character.");
                }

                return normalized[0];
            }

            if (targetType == typeof(Guid))
            {
                return Guid.Parse(normalized);
            }

            if (targetType == typeof(DateTime))
            {
                return DateTime.Parse(
                    normalized,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind);
            }

            if (targetType.IsEnum)
            {
                return Enum.Parse(targetType, normalized, ignoreCase: true);
            }

            return System.Convert.ChangeType(
                normalized,
                targetType,
                CultureInfo.InvariantCulture);
        }
    }
}
