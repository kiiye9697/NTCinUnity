using System.Collections.Generic;
using Ntc.Unity;
using NUnit.Framework;
using UnityEngine;

namespace Ntc.Tests
{
    public class NtcMemoryAccountingTests
    {
        [Test]
        public void NonSquareMipChain_UsesExactTexelCount()
        {
            var texture = new Texture2D(4,2,TextureFormat.RGBA32,true,true);
            try { Assert.That(NtcMemoryAccounting.EstimateTextureBytes(texture), Is.EqualTo(44)); }
            finally { Object.DestroyImmediate(texture); }
        }
        [Test]
        public void CompressedMips_CountAtLeastOneBlockPerLevel()
        {
            if (!SystemInfo.SupportsTextureFormat(TextureFormat.DXT1)) Assert.Ignore("DXT1 unsupported.");
            var texture = new Texture2D(8,8,TextureFormat.DXT1,true,true);
            try { Assert.That(NtcMemoryAccounting.EstimateTextureBytes(texture), Is.EqualTo(56)); }
            finally { Object.DestroyImmediate(texture); }
        }
        [Test]
        public void SharedOriginalTexture_IsCountedOnce()
        {
            var texture = new Texture2D(4,2,TextureFormat.RGBA32,true,true);
            var first = new Material(Shader.Find("Hidden/NTC/MaterialContractTest"));
            var second = new Material(first);
            first.SetTexture("_BaseMap",texture); second.SetTexture("_BaseMap",texture);
            try
            {
                var groups = new[] { new NtcSceneController.MaterialGroup { originalMaterials = new[] { first,second } } };
                var report = NtcMemoryAccounting.Capture(groups, new HashSet<int>());
                Assert.That(report.uniqueOriginalTextures, Is.EqualTo(1));
                Assert.That(report.originalTextureBytes, Is.EqualTo(44));
            }
            finally { Object.DestroyImmediate(first); Object.DestroyImmediate(second); Object.DestroyImmediate(texture); }
        }
    }
}
