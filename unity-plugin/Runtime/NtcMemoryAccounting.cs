using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Profiling;
using UnityEngine.Rendering;

namespace Ntc.Unity
{
    [Serializable]
    public sealed class NtcMemorySnapshot
    {
        public string measurement = "Estimated loaded color-texture storage plus NTC weight buffers; excludes depth attachments, MSAA copies, alignment, CPU copies and other GPU buffers.";
        public long originalTextureBytes, latentTextureBytes, decodedCacheBytes, otherLoadedTextureBytes, weightBufferBytes;
        public long estimatedTextureAndNtcBufferBytes;
        public long graphicsDriverAllocatedBytes;
        public bool graphicsDriverAllocationAvailable;
        public int uniqueOriginalTextures, uniqueLatentTextures, uniqueCacheTextures, otherLoadedTextures;
    }

    public static class NtcMemoryAccounting
    {
        public static void RememberTextures(Material[] materials, HashSet<int> ids)
        {
            if (materials == null) return;
            foreach (var material in materials)
                if (material != null)
                    foreach (string property in material.GetTexturePropertyNames())
                    {
                        Texture t = material.GetTexture(property);
                        if (t != null) ids.Add(t.GetInstanceID());
                    }
        }

        public static long EstimateTextureBytes(Texture texture)
        {
            if (texture == null) return 0;
            GraphicsFormat format = texture.graphicsFormat;
            if (format == GraphicsFormat.None) return 0;
            long blockBytes = GraphicsFormatUtility.GetBlockSize(format);
            int blockW = (int)GraphicsFormatUtility.GetBlockWidth(format), blockH = (int)GraphicsFormatUtility.GetBlockHeight(format);
            int slices = 1, depth = 1, mips = texture.mipmapCount;
            if (texture is Texture2DArray array) slices = array.depth;
            else if (texture is Cubemap) slices = 6;
            else if (texture is CubemapArray cubes) slices = cubes.cubemapCount * 6;
            else if (texture is Texture3D volume) depth = volume.depth;
            else if (texture is RenderTexture rt)
            {
                if (rt.dimension == TextureDimension.Tex3D) depth = rt.volumeDepth;
                else if (rt.dimension == TextureDimension.Cube) slices = 6;
                else if (rt.dimension == TextureDimension.CubeArray) slices = rt.volumeDepth;
                else slices = Mathf.Max(rt.volumeDepth, 1);
            }
            long bytes = 0;
            for (int mip = 0; mip < Mathf.Max(mips, 1); mip++)
            {
                int w = Mathf.Max(texture.width >> mip, 1), h = Mathf.Max(texture.height >> mip, 1);
                bytes += ((w + blockW - 1) / blockW) * (long)((h + blockH - 1) / blockH) * blockBytes * slices * Mathf.Max(depth >> mip, 1);
            }
            return bytes;
        }

        public static NtcMemorySnapshot Capture(NtcSceneController.MaterialGroup[] groups, HashSet<int> originals)
        {
            var snapshot = new NtcMemorySnapshot();
            var latentIds = new HashSet<int>(); var cacheIds = new HashSet<int>();
            var weights = new HashSet<ComputeBuffer>();
            if (groups != null)
                foreach (var g in groups)
                {
                    if (g == null) continue;
                    RememberTextures(g.originalMaterials, originals);
                    if (g.assets != null)
                    {
                        if (g.assets.LatentTexture != null) latentIds.Add(g.assets.LatentTexture.GetInstanceID());
                        if (g.assets.WeightBuffer != null && weights.Add(g.assets.WeightBuffer))
                            snapshot.weightBufferBytes += (long)g.assets.WeightBuffer.count * g.assets.WeightBuffer.stride;
                    }
                    AddTargets(g.onLoadTargets, cacheIds);
                    if (g.streamMaterial != null)
                        foreach (string property in g.streamMaterial.GetTexturePropertyNames())
                            if (g.streamMaterial.GetTexture(property) is RenderTexture rt) cacheIds.Add(rt.GetInstanceID());
                    foreach (Texture t in new Texture[] { g.ntcColor, g.ntcNormal, g.ntcMetalness, g.ntcAO, g.ntcSmoothness, g.ntcAlpha, g.metallicGlossMap, g.colorWithAlphaMap })
                        if (t != null) cacheIds.Add(t.GetInstanceID());
                }
            var seen = new HashSet<int>();
            foreach (Texture texture in Resources.FindObjectsOfTypeAll<Texture>())
            {
                if (texture is RenderTexture uncreated && !uncreated.IsCreated()) continue;
                int id = texture.GetInstanceID();
                if (!seen.Add(id)) continue;
                long bytes = EstimateTextureBytes(texture);
                if (latentIds.Contains(id)) { snapshot.latentTextureBytes += bytes; snapshot.uniqueLatentTextures++; }
                else if (cacheIds.Contains(id)) { snapshot.decodedCacheBytes += bytes; snapshot.uniqueCacheTextures++; }
                else if (originals.Contains(id)) { snapshot.originalTextureBytes += bytes; snapshot.uniqueOriginalTextures++; }
                else { snapshot.otherLoadedTextureBytes += bytes; snapshot.otherLoadedTextures++; }
            }
            snapshot.estimatedTextureAndNtcBufferBytes = snapshot.originalTextureBytes + snapshot.latentTextureBytes + snapshot.decodedCacheBytes
                + snapshot.otherLoadedTextureBytes + snapshot.weightBufferBytes;
            snapshot.graphicsDriverAllocatedBytes = Profiler.GetAllocatedMemoryForGraphicsDriver();
            snapshot.graphicsDriverAllocationAvailable = snapshot.graphicsDriverAllocatedBytes > 0;
            return snapshot;
        }

        static void AddTargets(NtcDecompressTargets targets, HashSet<int> ids)
        {
            if (targets == null) return;
            foreach (var t in new[] { targets.BaseColor, targets.Normal, targets.Mask, targets.AO }) if (t != null) ids.Add(t.GetInstanceID());
        }
    }
}
