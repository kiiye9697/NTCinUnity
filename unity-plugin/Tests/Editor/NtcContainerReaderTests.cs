using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using Ntc.Packing;

namespace Ntc.Tests
{
    public class NtcContainerReaderTests
    {
        private readonly List<string> _temporaryFiles = new List<string>();

        [TearDown]
        public void TearDown()
        {
            foreach (string path in _temporaryFiles)
            {
                if (File.Exists(path)) File.Delete(path);
            }
            _temporaryFiles.Clear();
        }

        [Test]
        public void ShowcaseCorpus_ParsesAll25Containers_AndHasExpectedSchema()
        {
            string dataDirectory = FindNtcDataDirectory();
            if (dataDirectory == null)
            {
                Assert.Ignore(
                    "No real NTC corpus found. Set NTC_TEST_DATA_DIR to the directory containing the 25 showcase .ntc files.");
            }

            string[] paths = Directory.GetFiles(dataDirectory, "*.ntc", SearchOption.TopDirectoryOnly);
            Array.Sort(paths, StringComparer.OrdinalIgnoreCase);
            Assert.That(paths.Length, Is.EqualTo(25),
                "The showcase regression corpus must contain exactly 25 .ntc files: " + dataDirectory);

            foreach (string path in paths)
            {
                NtcContainer container = NtcContainerReader.Load(path);
                NtcTextureSetDocument doc = container.Document;

                Assert.That(container.ContainerVersion, Is.GreaterThan(0), path);
                Assert.That(doc.SchemaVersion, Is.GreaterThanOrEqualTo(1), path);
                Assert.That(doc.Width, Is.GreaterThan(0), path);
                Assert.That(doc.Height, Is.GreaterThan(0), path);
                Assert.That(doc.NumChannels, Is.InRange(1, 16), path);
                Assert.That(doc.LatentShape, Is.Not.Null, path);
                CollectionAssert.Contains(new[] { 4, 8, 12, 16 }, doc.LatentShape.NumFeatures, path);
                Assert.That(doc.Latents, Is.Not.Empty, path);
                Assert.That(doc.Views, Is.Not.Empty, path);
                Assert.That(doc.ColorMips, Is.Not.Empty, path);
                Assert.That(doc.MlpVersions, Is.Not.Empty, path);

                for (int mlpIndex = 0; mlpIndex < doc.MlpVersions.Count; mlpIndex++)
                {
                    NtcMlp mlp = doc.MlpVersions[mlpIndex];
                    Assert.That(mlp.Activation, Is.EqualTo("HGELUClamp"), path);
                    // RTXNTC v0.9.2 files used by this showcase serialize the compact MLP
                    // weights in RowMajor order. This is a regression contract for the real
                    // corpus, not a parser-wide restriction (the synthetic schema test below
                    // deliberately exercises a different string value).
                    Assert.That(mlp.WeightLayout, Is.EqualTo("RowMajor"), path);
                    Assert.That(mlp.Layers, Is.Not.Empty, path);
                    Assert.That(mlp.Layers[0].InputChannels, Is.EqualTo(48), path);
                    Assert.That(mlp.Layers.Select(layer => layer.OutputChannels).ToArray(),
                        Is.EqualTo(new[] { 64, 48, 32, 16 }), path);
                    if (mlpIndex == 0)
                    {
                        // Runtime Build(..., 0) selects this portable DP4A/software-int8 model.
                        Assert.That(mlp.Layers.All(layer => layer.WeightType == "Int8"), Is.True, path);
                    }
                    else
                    {
                        // The optional CoopVec model mixes FloatE4M3 hidden weights with an
                        // Int8 output layer in the v0.9.2 corpus.
                        foreach (NtcMlpLayer layer in mlp.Layers)
                            CollectionAssert.Contains(new[] { "Int8", "FloatE4M3" }, layer.WeightType, path);
                    }
                }
            }
        }

        [Test]
        public void Load_RejectsBadSignature()
        {
            string path = WriteTemporaryFile(Encoding.ASCII.GetBytes("NOPE"));
            Assert.Throws<InvalidDataException>(() => NtcContainerReader.Load(path));
        }

        [Test]
        public void Load_RejectsTruncatedHeader()
        {
            string path = WriteTemporaryFile(Encoding.ASCII.GetBytes("NTEX"));
            Assert.Throws<EndOfStreamException>(() => NtcContainerReader.Load(path));
        }

        [Test]
        public void GetViewBytes_RejectsGDeflateView()
        {
            const string json =
                "{\"schemaVersion\":3,\"width\":4,\"height\":4,\"numChannels\":1," +
                "\"colorMips\":[{\"width\":4,\"height\":4}]," +
                "\"views\":[{\"offset\":0,\"storedSize\":4,\"uncompressedSize\":8,\"compression\":\"GDeflate\"}]}";
            string path = WriteContainer(json, new byte[] { 1, 2, 3, 4 });
            NtcContainer container = NtcContainerReader.Load(path);

            NotSupportedException error = Assert.Throws<NotSupportedException>(() => container.GetViewBytes(0));
            StringAssert.Contains("--gdeflate off", error.Message);
        }

        [Test]
        public void Load_ParsesMlpAndLatentSchema()
        {
            const string json =
                "{\"schemaVersion\":3,\"width\":64,\"height\":32,\"numChannels\":4," +
                "\"latentShape\":{\"numFeatures\":12}," +
                "\"mlpVersions\":[{\"activation\":\"HGELUClamp\",\"weightLayout\":\"ColumnMajor\"," +
                "\"layers\":[{\"inputChannels\":48,\"outputChannels\":64,\"weightView\":0,\"biasView\":1,\"weightType\":\"Int8\"}]}]," +
                "\"latents\":[{\"width\":8,\"height\":4,\"layerViews\":[2,3]}]," +
                "\"colorMips\":[{\"width\":64,\"height\":32,\"latentMip\":0,\"positionLod\":0.5,\"positionScale\":2.0}]," +
                "\"views\":[]}";
            NtcContainer container = NtcContainerReader.Load(WriteContainer(json, new byte[0]));
            NtcTextureSetDocument doc = container.Document;

            Assert.That(doc.LatentShape.NumFeatures, Is.EqualTo(12));
            Assert.That(doc.MlpVersions[0].Layers[0].InputChannels, Is.EqualTo(48));
            Assert.That(doc.MlpVersions[0].Layers[0].OutputChannels, Is.EqualTo(64));
            Assert.That(doc.MlpVersions[0].Layers[0].WeightType, Is.EqualTo("Int8"));
            Assert.That(doc.Latents[0].LayerViews, Is.EqualTo(new[] { 2, 3 }));
            Assert.That(doc.ColorMips[0].LatentMip, Is.EqualTo(0));
        }

        private string FindNtcDataDirectory()
        {
            string configured = Environment.GetEnvironmentVariable("NTC_TEST_DATA_DIR");
            if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured))
                return Path.GetFullPath(configured);

            string[] candidates =
            {
                Path.Combine(Application.streamingAssetsPath, "NtcData"),
                Path.Combine(Application.dataPath, "NtcPlugin", "NtcData")
            };
            return candidates.FirstOrDefault(path => Directory.Exists(path) && Directory.GetFiles(path, "*.ntc").Length > 0);
        }

        private string WriteTemporaryFile(byte[] bytes)
        {
            string path = Path.Combine(Path.GetTempPath(), "ntc-test-" + Guid.NewGuid().ToString("N") + ".ntc");
            File.WriteAllBytes(path, bytes);
            _temporaryFiles.Add(path);
            return path;
        }

        private string WriteContainer(string json, byte[] data)
        {
            byte[] jsonBytes = Encoding.UTF8.GetBytes(json);
            const long headerSize = 40;
            long jsonSize = Align4(jsonBytes.Length);
            long dataOffset = headerSize + jsonSize;
            long dataSize = Align4(data.Length);

            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(Encoding.ASCII.GetBytes("NTEX"));
                writer.Write((uint)0x100);
                writer.Write((ulong)headerSize);
                writer.Write((ulong)jsonSize);
                writer.Write((ulong)dataOffset);
                writer.Write((ulong)dataSize);
                writer.Write(jsonBytes);
                writer.Write(new byte[(int)(jsonSize - jsonBytes.Length)]);
                writer.Write(data);
                writer.Write(new byte[(int)(dataSize - data.Length)]);
                return WriteTemporaryFile(stream.ToArray());
            }
        }

        private static int Align4(int value)
        {
            return (value + 3) & ~3;
        }
    }
}
