using System;
using System.IO;
using NUnit.Framework;
using Ntc.Packing;
using Ntc.Unity;

namespace Ntc.Tests
{
    public class NtcInferenceValidationTests
    {
        static NtcContainer MakeModel()
        {
            var container = new NtcContainer { Document = new NtcTextureSetDocument(), DataChunk = new byte[16384] };
            var doc = container.Document;
            doc.Width = doc.Height = doc.NumColorMips = doc.NumChannels = 1;
            var mlp = new NtcMlp { WeightLayout = "RowMajor" };
            int[] channels = { 48, 64, 48, 32, 16 };
            int offset = 0;
            for (int i = 0; i < 4; i++)
            {
                int first = doc.Views.Count;
                foreach (int size in new[] { channels[i] * channels[i + 1], channels[i + 1] * 4, channels[i + 1] * 4 })
                {
                    doc.Views.Add(new NtcBufferView { Offset = offset, StoredSize = size });
                    offset += size;
                }
                mlp.Layers.Add(new NtcMlpLayer
                {
                    InputChannels = channels[i], OutputChannels = channels[i + 1],
                    WeightView = first, ScaleView = first + 1, BiasView = first + 2, BiasType = "Int32"
                });
            }
            doc.MlpVersions.Add(mlp);
            doc.Views.Add(new NtcBufferView { Offset = offset, StoredSize = 2 });
            var latent = new NtcLatentImage { Width = 1, Height = 1 };
            latent.LayerViews.Add(doc.Views.Count - 1);
            doc.Latents.Add(latent);
            doc.ColorMips.Add(new NtcColorMip { Width = 1, Height = 1, LatentMip = 0, PositionScale = 1 });
            return container;
        }

        [Test]
        public void ValidModel_AcceptsFixedShaderContract() => Assert.DoesNotThrow(() => NtcAssetBuilder.ValidateInferenceModel(MakeModel()));

        [TestCase("layout")]
        [TestCase("shape")]
        [TestCase("precision")]
        public void IncompatibleModel_IsRejectedBeforeGpuAllocation(string mutation)
        {
            var container = MakeModel();
            var mlp = container.Document.MlpVersions[0];
            if (mutation == "layout") mlp.WeightLayout = "ColumnMajor";
            if (mutation == "shape") mlp.Layers[0].OutputChannels = 32;
            if (mutation == "precision") mlp.Layers[0].WeightType = "FloatE4M3";
            Assert.Throws<NotSupportedException>(() => NtcAssetBuilder.Build(container));
        }

        [Test]
        public void TruncatedWeights_AreRejectedBeforeGpuAllocation()
        {
            var container = MakeModel();
            container.Document.Views[0].StoredSize--;
            Assert.Throws<InvalidDataException>(() => NtcAssetBuilder.Build(container));
        }

        [Test]
        public void InvalidColorChannel_IsRejectedBeforeGpuAllocation()
        {
            var container = MakeModel();
            container.Document.Textures.Add(new NtcTexture { Name = "Color", FirstChannel = 15, NumChannels = 3 });
            Assert.Throws<InvalidDataException>(() => NtcAssetBuilder.Build(container));
        }

        [Test]
        public void OutOfRangeLod_IsRejectedBeforeGpuAllocation()
        {
            var container = MakeModel();
            container.Document.ColorMips[0].PositionLod = 2f;
            Assert.Throws<InvalidDataException>(() => NtcAssetBuilder.Build(container));
        }

        [Test]
        public void MissingSecondNeuralGrid_IsRejectedBeforeGpuAllocation()
        {
            var container = MakeModel();
            container.Document.Latents[0].Width = container.Document.Latents[0].Height = 2;
            container.Document.Views[12].StoredSize = 8;
            Assert.Throws<InvalidDataException>(() => NtcAssetBuilder.Build(container));
        }

        [Test]
        public void ShowcaseCorpus_MatchesInferenceContract()
        {
            string directory = Environment.GetEnvironmentVariable("NTC_TEST_DATA_DIR");
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) Assert.Ignore("Set NTC_TEST_DATA_DIR for real-model validation.");
            string[] paths = Directory.GetFiles(directory, "*.ntc");
            Assert.That(paths, Is.Not.Empty);
            foreach (string path in paths)
                Assert.DoesNotThrow(() => NtcAssetBuilder.ValidateInferenceModel(NtcContainerReader.Load(path)), path);
        }
    }
}
