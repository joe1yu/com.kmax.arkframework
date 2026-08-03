using System;
using System.IO;
using System.Text;
using NUnit.Framework;

namespace ArkFramework.Editor.Tests
{
    public sealed class TableClassGeneratorTests
    {
        [Test]
        public void GenerateSource_UsesSpecifiedTypeFieldsAndComments()
        {
            var source = TableClassGenerator.GenerateSource(
                "#class,Game.Tables.ItemRow\n" +
                "#fields,Id,Name,Tags,ExpiresAt\n" +
                "#types,int,string,string[],DateTime?\n" +
                "#key,Id\n" +
                "#comments,编号,名称 & 展示,标签,到期时间\n" +
                "1,Sword,weapon|starter,\n",
                "Items.csv");

            StringAssert.Contains("namespace Game.Tables", source);
            StringAssert.Contains("public sealed class ItemRow", source);
            StringAssert.Contains("public int Id { get; set; }", source);
            StringAssert.Contains(
                "public string[] Tags { get; set; }",
                source);
            StringAssert.Contains(
                "public DateTime? ExpiresAt { get; set; }",
                source);
            StringAssert.Contains("名称 &amp; 展示", source);
        }

        [Test]
        public void GenerateSource_RejectsInvalidClassIdentifier()
        {
            Assert.Throws<TableFormatException>(
                () => TableClassGenerator.GenerateSource(
                    "#class,Game.Tables.bad-class\n" +
                    "#fields,Id\n" +
                    "#types,int\n" +
                    "1\n",
                    "Invalid.csv"));
        }

        [TestCase("Assets/Generated//Tables")]
        [TestCase("Assets/Generated/./Tables")]
        [TestCase("Assets/Generated/../Tables")]
        public void GenerateAsset_RejectsInvalidOutputDirectory(string output)
        {
            var streamingAssetsRoot =
                Path.GetFullPath("Assets/StreamingAssets");
            var streamingAssetsRootExisted =
                Directory.Exists(streamingAssetsRoot);
            Directory.CreateDirectory(streamingAssetsRoot);
            var path = "Assets/StreamingAssets/TableGeneratorTest-" +
                       Guid.NewGuid().ToString("N") + ".csv";
            File.WriteAllText(
                path,
                "#class,Rows.ItemRow\n#fields,Id\n#types,int\n1\n",
                new UTF8Encoding(false));
            try
            {
                Assert.Throws<ArgumentException>(
                    () => TableClassGenerator.GenerateAsset(path, output));
            }
            finally
            {
                File.Delete(path);
                if (!streamingAssetsRootExisted &&
                    Directory.Exists(streamingAssetsRoot) &&
                    Directory.GetFileSystemEntries(streamingAssetsRoot).Length == 0)
                {
                    Directory.Delete(streamingAssetsRoot);
                    File.Delete(streamingAssetsRoot + ".meta");
                }
            }
        }
    }
}
