using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace ArkFramework.Tests
{
    public sealed class TableServiceTests
    {
        private const string TableText =
            "#class,ArkFramework.Tests.ItemTableRow\n" +
            "#fields,Id,Name,Price,Enabled,Tags,Quality\n" +
            "#types,int,string,float,bool,string[],ItemQuality\n" +
            "#key,Id\n" +
            "#comments,编号,名称,价格,启用,标签,品质\n" +
            "1,\"Sword, Basic\",12.5,1,weapon|starter,Rare\n" +
            "2,\"Shield \"\"Mk II\"\"\",8.25,false,,Common\n";

        [Test]
        public void LoadAsync_ParsesRowsTypesAndPrimaryKey()
        {
            var source = new MemorySource(TableText);
            using (var service = new TableService(source))
            {
                var table = service.LoadAsync<ItemTableRow>(
                        "Tables/Items.csv")
                    .AsTask()
                    .GetAwaiter()
                    .GetResult();

                Assert.That(table.Count, Is.EqualTo(2));
                Assert.That(table.HasKey, Is.True);
                Assert.That(table.Get(1).Name, Is.EqualTo("Sword, Basic"));
                Assert.That(table.Get(1).Price, Is.EqualTo(12.5f));
                Assert.That(table.Get(1).Enabled, Is.True);
                Assert.That(
                    table.Get(1).Tags,
                    Is.EqualTo(new[] { "weapon", "starter" }));
                Assert.That(table.Get(1).Quality, Is.EqualTo(ItemQuality.Rare));
                Assert.That(table.Get(2).Name, Is.EqualTo("Shield \"Mk II\""));
                Assert.That(table.Get(2).Tags, Is.Empty);
                Assert.That(source.ReadCount, Is.EqualTo(1));

                var cached = service.LoadAsync<ItemTableRow>(
                        "Tables/Items.csv")
                    .AsTask()
                    .GetAwaiter()
                    .GetResult();
                Assert.That(cached, Is.SameAs(table));
                Assert.That(source.ReadCount, Is.EqualTo(1));
            }
        }

        [Test]
        public void Parse_SupportsQuotedMultilineCells()
        {
            var document = CsvTableDocument.Parse(
                "#class,Rows.MessageRow\n" +
                "#fields,Id,Message\n" +
                "#types,int,string\n" +
                "1,\"first line\nsecond line\"\n",
                "Messages.csv");

            Assert.That(document.Rows, Has.Count.EqualTo(1));
            Assert.That(
                document.Rows[0].Cells[1],
                Is.EqualTo("first line\nsecond line"));
        }

        [Test]
        public void Parse_AcceptsUtf8Bom()
        {
            var document = CsvTableDocument.Parse(
                "\uFEFF#class,Rows.ItemRow\n" +
                "#fields,Id\n" +
                "#types,int\n" +
                "1\n",
                "Items.csv");

            Assert.That(document.Schema.TargetTypeName, Is.EqualTo("Rows.ItemRow"));
            Assert.That(document.Rows, Has.Count.EqualTo(1));
        }

        [Test]
        public void Parse_SupportsSpreadsheetAlignedDataRows()
        {
            var document = CsvTableDocument.Parse(
                "#class,Rows.ItemRow\n" +
                "#fields,Id,Name\n" +
                "#types,int,string\n" +
                "#comments,编号,名称\n" +
                ",1,Sword\n",
                "Items.csv");

            Assert.That(document.Rows, Has.Count.EqualTo(1));
            Assert.That(
                document.Rows[0].Cells,
                Is.EqualTo(new[] { "1", "Sword" }));
        }

        [Test]
        public void Parse_IgnoresCommentRowsAtAnyPosition()
        {
            var document = CsvTableDocument.Parse(
                "// 文件说明\n" +
                "#class,Rows.ItemRow\n" +
                "//,类声明与字段之间的注释\n" +
                "#fields,Id,Name\n" +
                "#types,int,string\n" +
                "//,1,Disabled Before Data\n" +
                ",2,Sword\n" +
                "//,3,Disabled Between Data\n" +
                ",4,Shield\n" +
                "// 表尾说明\n",
                "Items.csv");

            Assert.That(document.Rows, Has.Count.EqualTo(2));
            Assert.That(
                document.Rows.Select(row => row.Cells[0]),
                Is.EqualTo(new[] { "2", "4" }));
        }

        [Test]
        public void Parse_AlignedRowPreservesDoubleSlashFirstValue()
        {
            var document = CsvTableDocument.Parse(
                "#class,Rows.PathRow\n" +
                "#fields,Path,Name\n" +
                "#types,string,string\n" +
                ",//server/share,Network\n",
                "Paths.csv");

            Assert.That(document.Rows, Has.Count.EqualTo(1));
            Assert.That(
                document.Rows[0].Cells[0],
                Is.EqualTo("//server/share"));
        }

        [Test]
        public void LoadAsync_ReportsDuplicateKeyWithRowAndColumn()
        {
            var duplicate = TableText +
                            "1,Duplicate,1,true,,Common\n";
            using (var service = new TableService(new MemorySource(duplicate)))
            {
                var exception = Assert.Throws<TableParseException>(
                    () => service.LoadAsync<ItemTableRow>(
                            "Tables/Items.csv")
                        .AsTask()
                        .GetAwaiter()
                        .GetResult());

                Assert.That(exception.RowNumber, Is.EqualTo(8));
                Assert.That(exception.ColumnName, Is.EqualTo("Id"));
                Assert.That(exception.Message, Does.Contain("Duplicate key"));
            }
        }

        [Test]
        public void LoadAsync_MapsPublicStructMembers()
        {
            const string text =
                "#class,Rows.StructRow\n" +
                "#fields,Id,Name\n" +
                "#types,int,string\n" +
                "1,Sword\n";
            using (var service = new TableService(new MemorySource(text)))
            {
                var table = service.LoadAsync<StructRow>("Tables/Struct.csv")
                    .AsTask()
                    .GetAwaiter()
                    .GetResult();

                Assert.That(table.Rows[0].Id, Is.EqualTo(1));
                Assert.That(table.Rows[0].Name, Is.EqualTo("Sword"));
            }
        }

        [TestCase("../Items.csv")]
        [TestCase("/Items.csv")]
        [TestCase("Tables//Items.csv")]
        public void LoadAsync_RejectsPathOutsideStreamingAssets(string path)
        {
            using (var service = new TableService(new MemorySource(TableText)))
            {
                Assert.Throws<ArgumentException>(
                    () => service.LoadAsync<ItemTableRow>(path)
                        .AsTask()
                        .GetAwaiter()
                        .GetResult());
            }
        }

        [Test]
        public void Installer_DeclaresTableModuleAndService()
        {
            var installer = ScriptableObject.CreateInstance<
                TableModuleInstaller>();
            try
            {
                Assert.That(installer.ModuleId, Is.EqualTo("Table"));
                Assert.That(installer.Dependencies, Is.Empty);
                Assert.That(
                    installer.ServiceTypes,
                    Is.EqualTo(new[] { typeof(ITableService) }));
                Assert.That(installer.CreateModule(), Is.TypeOf<TableModule>());
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(installer);
            }
        }

        [UnityTest]
        public IEnumerator StreamingAssetsSource_ReadsUtf8File()
        {
            var streamingAssetsRoot = Application.streamingAssetsPath;
            var streamingAssetsRootExisted =
                Directory.Exists(streamingAssetsRoot);
            var relativeDirectory =
                "ArkFrameworkTableTests/" + Guid.NewGuid().ToString("N");
            var directory = Path.Combine(
                streamingAssetsRoot,
                relativeDirectory.Replace('/', Path.DirectorySeparatorChar));
            var file = Path.Combine(directory, "Utf8.csv");
            Directory.CreateDirectory(directory);
            File.WriteAllText(file, "名称,长剑", new UTF8Encoding(false));

            var task = new StreamingAssetsTableSource()
                .ReadAsync(relativeDirectory + "/Utf8.csv")
                .AsTask();
            try
            {
                while (!task.IsCompleted)
                {
                    yield return null;
                }

                Assert.That(task.GetAwaiter().GetResult(), Is.EqualTo("名称,长剑"));
            }
            finally
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }

                var testRoot = Path.GetDirectoryName(directory);
                if (Directory.Exists(testRoot) &&
                    Directory.GetFileSystemEntries(testRoot).Length == 0)
                {
                    Directory.Delete(testRoot);
                }

                if (!streamingAssetsRootExisted &&
                    Directory.Exists(streamingAssetsRoot) &&
                    Directory.GetFileSystemEntries(streamingAssetsRoot).Length == 0)
                {
                    Directory.Delete(streamingAssetsRoot);
                    File.Delete(streamingAssetsRoot + ".meta");
                }
            }
        }

        public sealed class ItemTableRow
        {
            public int Id { get; set; }

            public string Name { get; set; }

            public float Price { get; set; }

            public bool Enabled { get; set; }

            public string[] Tags { get; set; }

            public ItemQuality Quality { get; set; }
        }

        public enum ItemQuality
        {
            Common,
            Rare
        }

        public struct StructRow
        {
            public int Id;

            public string Name;
        }

        private sealed class MemorySource : ITableTextSource
        {
            private readonly string _text;

            public MemorySource(string text)
            {
                _text = text;
            }

            public int ReadCount { get; private set; }

            public ValueTask<string> ReadAsync(
                string relativePath,
                CancellationToken token = default)
            {
                token.ThrowIfCancellationRequested();
                ReadCount++;
                return new ValueTask<string>(_text);
            }
        }
    }
}
