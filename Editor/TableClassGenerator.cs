using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace ArkFramework.Editor
{
    public static class TableClassGenerator
    {
        public const string DefaultOutputDirectory =
            "Assets/Generated/Tables";

        private static readonly IReadOnlyDictionary<string, string> TypeAliases =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["string"] = "string",
                ["bool"] = "bool",
                ["byte"] = "byte",
                ["sbyte"] = "sbyte",
                ["short"] = "short",
                ["ushort"] = "ushort",
                ["int"] = "int",
                ["uint"] = "uint",
                ["long"] = "long",
                ["ulong"] = "ulong",
                ["float"] = "float",
                ["double"] = "double",
                ["decimal"] = "decimal",
                ["char"] = "char",
                ["guid"] = "Guid",
                ["datetime"] = "DateTime"
            };

        private static readonly HashSet<string> Keywords =
            new HashSet<string>(
                new[]
                {
                    "abstract", "as", "base", "bool", "break", "byte",
                    "case", "catch", "char", "checked", "class", "const",
                    "continue", "decimal", "default", "delegate", "do",
                    "double", "else", "enum", "event", "explicit", "extern",
                    "false", "finally", "fixed", "float", "for", "foreach",
                    "goto", "if", "implicit", "in", "int", "interface",
                    "internal", "is", "lock", "long", "namespace", "new",
                    "null", "object", "operator", "out", "override", "params",
                    "private", "protected", "public", "readonly", "ref",
                    "return", "sbyte", "sealed", "short", "sizeof",
                    "stackalloc", "static", "string", "struct", "switch",
                    "this", "throw", "true", "try", "typeof", "uint", "ulong",
                    "unchecked", "unsafe", "ushort", "using", "virtual",
                    "void", "volatile", "while"
                },
                StringComparer.Ordinal);

        [MenuItem("ArkFramework/Tables/Generate Selected Classes")]
        public static void GenerateSelectedClasses()
        {
            var paths = Selection.objects
                .Select(AssetDatabase.GetAssetPath)
                .Where(IsStreamingAssetsCsv)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (paths.Length == 0)
            {
                throw new InvalidOperationException(
                    "Select one or more CSV files under Assets/StreamingAssets.");
            }

            GenerateAssets(paths);
        }

        [MenuItem(
            "ArkFramework/Tables/Generate Selected Classes",
            true)]
        private static bool CanGenerateSelectedClasses()
        {
            return Selection.objects
                .Select(AssetDatabase.GetAssetPath)
                .Any(IsStreamingAssetsCsv);
        }

        [MenuItem("ArkFramework/Tables/Generate All Classes")]
        public static void GenerateAllClasses()
        {
            const string streamingAssetsRoot = "Assets/StreamingAssets";
            if (!AssetDatabase.IsValidFolder(streamingAssetsRoot))
            {
                throw new DirectoryNotFoundException(
                    "Assets/StreamingAssets does not exist.");
            }

            var paths = AssetDatabase.FindAssets(
                    "t:TextAsset",
                    new[] { streamingAssetsRoot })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(IsStreamingAssetsCsv)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
            if (paths.Length == 0)
            {
                throw new InvalidOperationException(
                    "No CSV table was found under Assets/StreamingAssets.");
            }

            GenerateAssets(paths);
        }

        public static string GenerateAsset(
            string csvAssetPath,
            string outputDirectoryOverride = null)
        {
            if (!IsStreamingAssetsCsv(csvAssetPath))
            {
                throw new ArgumentException(
                    "CSV table must be under Assets/StreamingAssets.",
                    nameof(csvAssetPath));
            }

            var text = File.ReadAllText(
                Path.GetFullPath(csvAssetPath),
                Encoding.UTF8);
            var document = CsvTableDocument.Parse(text, csvAssetPath);
            var outputDirectory = string.IsNullOrWhiteSpace(
                outputDirectoryOverride)
                ? document.Schema.OutputDirectory
                : outputDirectoryOverride;
            if (string.IsNullOrWhiteSpace(outputDirectory))
            {
                outputDirectory = DefaultOutputDirectory;
            }

            outputDirectory = NormalizeOutputDirectory(outputDirectory);
            EnsureAssetFolder(outputDirectory);
            var className = GetTypeParts(
                document.Schema.TargetTypeName).ClassName;
            var outputPath = outputDirectory + "/" +
                             className + ".generated.cs";
            File.WriteAllText(
                Path.GetFullPath(outputPath),
                GenerateSource(document),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            AssetDatabase.ImportAsset(
                outputPath,
                ImportAssetOptions.ForceSynchronousImport |
                ImportAssetOptions.ForceUpdate);
            return outputPath;
        }

        public static string GenerateSource(
            string csvText,
            string sourceName = null)
        {
            return GenerateSource(
                CsvTableDocument.Parse(csvText, sourceName));
        }

        private static string GenerateSource(CsvTableDocument document)
        {
            var typeParts = GetTypeParts(document.Schema.TargetTypeName);
            var builder = new StringBuilder();
            builder.AppendLine("// <auto-generated>");
            builder.AppendLine("// 由 ArkFramework 配表生成器创建，请勿手工修改。");
            builder.AppendLine("// </auto-generated>");
            builder.AppendLine("using System;");
            builder.AppendLine();
            if (!string.IsNullOrEmpty(typeParts.Namespace))
            {
                builder.Append("namespace ")
                    .AppendLine(typeParts.Namespace);
                builder.AppendLine("{");
            }

            var indent = string.IsNullOrEmpty(typeParts.Namespace)
                ? string.Empty
                : "    ";
            builder.Append(indent).AppendLine("[Serializable]");
            builder.Append(indent)
                .Append("public sealed class ")
                .AppendLine(typeParts.ClassName);
            builder.Append(indent).AppendLine("{");
            foreach (var column in document.Schema.Columns)
            {
                ValidateIdentifier(column.Name, "field");
                var typeName = NormalizeTypeName(column.TypeName);
                if (!string.IsNullOrWhiteSpace(column.Comment))
                {
                    builder.Append(indent).AppendLine("    /// <summary>");
                    builder.Append(indent)
                        .Append("    /// ")
                        .AppendLine(EscapeXml(column.Comment));
                    builder.Append(indent).AppendLine("    /// </summary>");
                }

                builder.Append(indent)
                    .Append("    public ")
                    .Append(typeName)
                    .Append(' ')
                    .Append(column.Name)
                    .AppendLine(" { get; set; }");
                builder.AppendLine();
            }

            builder.Append(indent).AppendLine("}");
            if (!string.IsNullOrEmpty(typeParts.Namespace))
            {
                builder.AppendLine("}");
            }

            return builder.ToString();
        }

        private static void GenerateAssets(IReadOnlyList<string> paths)
        {
            var outputs = paths.Select(path => GenerateAsset(path)).ToArray();
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            Debug.Log(
                $"Generated {outputs.Length} table class(es): " +
                string.Join(", ", outputs));
        }

        private static bool IsStreamingAssetsCsv(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            var normalized = path.Replace('\\', '/');
            return normalized.StartsWith(
                       "Assets/StreamingAssets/",
                       StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(
                       Path.GetExtension(normalized),
                       ".csv",
                       StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeOutputDirectory(string path)
        {
            var normalized = path.Replace('\\', '/').TrimEnd('/');
            var segments = normalized.Split('/');
            if (!normalized.StartsWith("Assets/", StringComparison.Ordinal) ||
                segments.Any(
                    segment =>
                        string.IsNullOrEmpty(segment) ||
                        string.Equals(segment, ".", StringComparison.Ordinal) ||
                        string.Equals(segment, "..", StringComparison.Ordinal)))
            {
                throw new ArgumentException(
                    "#output must be a project-relative directory under Assets.",
                    nameof(path));
            }

            return normalized;
        }

        private static void EnsureAssetFolder(string assetPath)
        {
            var segments = assetPath.Split('/');
            var current = segments[0];
            for (var index = 1; index < segments.Length; index++)
            {
                var next = current + "/" + segments[index];
                if (!AssetDatabase.IsValidFolder(next))
                {
                    var guid = AssetDatabase.CreateFolder(
                        current,
                        segments[index]);
                    if (string.IsNullOrEmpty(guid))
                    {
                        throw new InvalidOperationException(
                            $"Failed to create generated code folder '{next}'.");
                    }
                }

                current = next;
            }
        }

        private static TypeParts GetTypeParts(string targetTypeName)
        {
            var separator = targetTypeName.LastIndexOf('.');
            var namespaceName = separator < 0
                ? string.Empty
                : targetTypeName.Substring(0, separator);
            var className = separator < 0
                ? targetTypeName
                : targetTypeName.Substring(separator + 1);
            ValidateIdentifier(className, "class");
            if (!string.IsNullOrEmpty(namespaceName))
            {
                foreach (var segment in namespaceName.Split('.'))
                {
                    ValidateIdentifier(segment, "namespace");
                }
            }

            return new TypeParts(namespaceName, className);
        }

        private static string NormalizeTypeName(string typeName)
        {
            var normalized = typeName.Trim();
            if (normalized.EndsWith("?", StringComparison.Ordinal))
            {
                return NormalizeTypeName(
                           normalized.Substring(0, normalized.Length - 1)) +
                       "?";
            }

            if (normalized.EndsWith("[]", StringComparison.Ordinal))
            {
                return NormalizeTypeName(
                           normalized.Substring(0, normalized.Length - 2)) +
                       "[]";
            }

            if (TypeAliases.TryGetValue(normalized, out var alias))
            {
                return alias;
            }

            foreach (var segment in normalized.Split('.'))
            {
                ValidateIdentifier(segment, "type");
            }

            return normalized;
        }

        private static void ValidateIdentifier(string value, string role)
        {
            if (string.IsNullOrWhiteSpace(value) ||
                Keywords.Contains(value) ||
                !(char.IsLetter(value[0]) || value[0] == '_') ||
                value.Skip(1).Any(
                    character =>
                        !(char.IsLetterOrDigit(character) || character == '_')))
            {
                throw new TableFormatException(
                    "<schema>",
                    0,
                    $"'{value}' is not a valid C# {role} identifier.");
            }
        }

        private static string EscapeXml(string value)
        {
            return value.Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;");
        }

        private readonly struct TypeParts
        {
            public TypeParts(string namespaceName, string className)
            {
                Namespace = namespaceName;
                ClassName = className;
            }

            public string Namespace { get; }

            public string ClassName { get; }
        }
    }
}
