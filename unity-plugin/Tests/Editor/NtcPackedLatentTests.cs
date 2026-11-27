using System;
using System.IO;
using Ntc.Packing;
using Ntc.Unity;
using NUnit.Framework;
using UnityEngine;

namespace Ntc.Tests
{
    public class NtcPackedLatentTests
    {
        [TestCase("Bricks")]
        [TestCase("Flagpoles")]
        public void PackedUpload_MatchesExpandedDecoder(string name)
        {
            if (!SystemInfo.supportsComputeShaders) Assert.Ignore("GPU required.");
            string directory = Environment.GetEnvironmentVariable("NTC_TEST_DATA_DIR");
            if (string.IsNullOrEmpty(directory)) Assert.Ignore("Set NTC_TEST_DATA_DIR.");
            bool preference = NtcAssetBuilder.PreferPackedLatents;
            NtcBuiltAssets expanded = null, packed = null;
            NtcDecompressor decoder = null;
            NtcDecompressTargets first = null, second = null;
            try
            {
                var data = NtcContainerReader.Load(Path.Combine(directory,name+".ntc"));
                var layout = NtcAssetBuilder.BuildPbrLayout(data);
                NtcAssetBuilder.PreferPackedLatents = false; expanded = NtcAssetBuilder.Build(data);
                NtcAssetBuilder.PreferPackedLatents = true; packed = NtcAssetBuilder.Build(data);
                long firstBytes = NtcMemoryAccounting.EstimateTextureBytes(expanded.LatentTexture);
                long secondBytes = NtcMemoryAccounting.EstimateTextureBytes(packed.LatentTexture);
                Assert.That(secondBytes,Is.LessThanOrEqualTo(firstBytes));
                decoder = NtcDecompressor.Create(null);
                int mip = Math.Min(7,data.Document.NumColorMips-1), w = Math.Max(data.Document.Width >> mip,1), h = Math.Max(data.Document.Height >> mip,1);
                first = decoder.AllocateTargets(w,h,layout,false); second = decoder.AllocateTargets(w,h,layout,false);
                decoder.DecompressFull(expanded,layout,first,mip); decoder.DecompressFull(packed,layout,second,mip);
                var a = Read(first.BaseColor); var b = Read(second.BaseColor); int max = 0;
                for (int i = 0; i < a.Length; i++)
                { max = Math.Max(max,Math.Abs(a[i].r-b[i].r)); max = Math.Max(max,Math.Abs(a[i].g-b[i].g)); max = Math.Max(max,Math.Abs(a[i].b-b[i].b)); }
                TestContext.WriteLine(name+" expanded bytes="+firstBytes+" packed bytes="+secondBytes+" format="+packed.LatentTexture.graphicsFormat+" max RGB byte error="+max);
                Assert.That(max,Is.LessThanOrEqualTo(2));
            }
            finally
            {
                NtcAssetBuilder.PreferPackedLatents = preference;
                if (first != null) first.Release(); if (second != null) second.Release();
                if (decoder != null) decoder.Dispose(); if (expanded != null) expanded.Release(); if (packed != null) packed.Release();
            }
        }
        static Color32[] Read(RenderTexture target)
        {
            var previous = RenderTexture.active; var texture = new Texture2D(target.width,target.height,TextureFormat.RGBA32,false,true);
            try { RenderTexture.active=target; texture.ReadPixels(new Rect(0,0,target.width,target.height),0,0); texture.Apply(); return texture.GetPixels32(); }
            finally { RenderTexture.active=previous; UnityEngine.Object.DestroyImmediate(texture); }
        }
    }
}
