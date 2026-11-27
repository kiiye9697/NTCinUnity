// NtcTileStreamer.cs
// mode3 (on-Sample) "decompress-on-demand" scheduler.
//
// Instead of running the full MLP per pixel every frame (the old on-Sample fragment path),
// each visible material is decompressed ONCE into a capped-resolution cache made of tiles,
// then shaded as an ordinary texture. Work is:
//   * budgeted   -> at most N tile dispatches per frame, so entering the map never freezes;
//   * visibility -> only materials whose renderers are currently visible get decompressed,
//                   nearest-to-camera first;
//   * capped     -> the cache resolution is clamped (default 1024), so a 4K material never
//                   costs 4K of decompressed VRAM -- this is the "省 VRAM" win vs on-Load;
//   * evicted    -> materials hidden for a while release their cache to reclaim VRAM.
//
// The heavy lifting (the actual MLP) is the shared NtcDecompress.compute via NtcDecompressor.
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace Ntc.Unity
{
    public class NtcTileStreamer
    {
        public static int GlobalMaxCacheMB = 256;
        public int TileSize = 256;
        public int CapResolution = 1024;
        public int BudgetTilesPerFrame = 8;
        public float EvictAfterSeconds = 5f;
        public long MaxCacheBytes = 256L * 1024L * 1024L;

        readonly NtcDecompressor _decomp;

        public class Entry
        {
            public NtcBuiltAssets assets;
            public NtcPbrChannelLayout layout;
            public Renderer[] renderers;

            public int colorMip;
            public int cacheW, cacheH;
            public int tilesX, tilesY;
            public bool[] tileResident;
            public int residentCount;

            public NtcDecompressTargets targets;
            public int version;          // bumped whenever targets are (re)allocated
            public float lastVisibleTime;
            public bool visibleNow;
            public float sqrDistance;
            public long predictedCacheBytes;
            public int registrationOrder;
            public bool selectedForBudget;
            public bool budgetBlocked;

            // A streamed cache is displayable only after every texel has been initialized
            // and its mip chain generated. Until then the renderer keeps the original map.
            public bool IsComplete { get { return targets != null && residentCount == tileResident.Length; } }
        }

        readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>();
        readonly List<Entry> _visible = new List<Entry>();
        readonly Plane[] _frustumPlanes = new Plane[6];
        int _nextRegistrationOrder;

        public NtcTileStreamer(NtcDecompressor decomp) { _decomp = decomp; }

        public bool IsRegistered(string id) { return _entries.ContainsKey(id); }

        public Entry Register(string id, NtcBuiltAssets assets, NtcPbrChannelLayout layout, Renderer[] renderers)
        {
            Entry e;
            if (_entries.TryGetValue(id, out e))
            {
                // Multiple material groups may legitimately share one .ntc/cache id (for
                // example distinct same-name material objects). Visibility must account for
                // every consumer without registering duplicate Renderer checks.
                e.renderers = UnionRenderers(e.renderers, renderers);
                return e;
            }

            int fullW = Mathf.Max(assets.Constants.imageWidth, 1);
            int fullH = Mathf.Max(assets.Constants.imageHeight, 1);
            int mips = Mathf.Max(assets.Constants.imageMips, 1);

            // Pick the coarsest mip whose dimensions still fit under the resolution cap.
            int mip = 0;
            while (mip < mips - 1 && Mathf.Max(fullW >> mip, fullH >> mip) > CapResolution)
                mip++;

            e = new Entry
            {
                assets = assets,
                layout = layout,
                renderers = renderers ?? System.Array.Empty<Renderer>(),
                colorMip = mip,
                cacheW = Mathf.Max(fullW >> mip, 1),
                cacheH = Mathf.Max(fullH >> mip, 1),
                lastVisibleTime = -999f,
                registrationOrder = _nextRegistrationOrder++,
            };
            e.tilesX = Mathf.Max((e.cacheW + TileSize - 1) / TileSize, 1);
            e.tilesY = Mathf.Max((e.cacheH + TileSize - 1) / TileSize, 1);
            e.tileResident = new bool[e.tilesX * e.tilesY];
            e.predictedCacheBytes = PredictCacheBytes(e.cacheW, e.cacheH, e.layout, true);
            _entries[id] = e;
            return e;
        }

        static Renderer[] UnionRenderers(Renderer[] current, Renderer[] additions)
        {
            var result = new List<Renderer>();
            if (current != null)
                for (int i = 0; i < current.Length; i++)
                    if (current[i] != null && !result.Contains(current[i])) result.Add(current[i]);
            if (additions != null)
                for (int i = 0; i < additions.Length; i++)
                    if (additions[i] != null && !result.Contains(additions[i])) result.Add(additions[i]);
            return result.ToArray();
        }

        public Entry Get(string id)
        {
            Entry e;
            return _entries.TryGetValue(id, out e) ? e : null;
        }

        /// <summary>
        /// Per-frame update: refresh visibility/priority, allocate caches for visible entries,
        /// stream missing tiles up to the frame budget, and evict long-hidden entries.
        /// </summary>
        public void Tick(Camera cam)
        {
            if (_decomp == null || !_decomp.IsValid) return;

            float now = Time.realtimeSinceStartup;
            Vector3 camPos = cam != null ? cam.transform.position : Vector3.zero;
            // Renderer.isVisible depends on whether Unity considers a window/camera visible and
            // remains false in valid hidden/offscreen Players. Streaming needs deterministic
            // camera visibility, so use the same frustum/bounds test in foreground and headless-
            // window operation. Retain isVisible only as the no-camera fallback.
            Plane[] frustumPlanes = null;
            if (cam != null)
            {
                GeometryUtility.CalculateFrustumPlanes(cam, _frustumPlanes);
                frustumPlanes = _frustumPlanes;
            }

            // 1. Update visibility + distance, and evict entries hidden too long.
            var visible = _visible;
            visible.Clear();
            foreach (var kv in _entries)
            {
                Entry e = kv.Value;
                e.visibleNow = false;
                e.selectedForBudget = false;
                e.budgetBlocked = false;
                e.sqrDistance = float.MaxValue;
                for (int i = 0; i < e.renderers.Length; i++)
                {
                    Renderer r = e.renderers[i];
                    if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                    bool rendererVisible = frustumPlanes != null
                        ? GeometryUtility.TestPlanesAABB(frustumPlanes, r.bounds)
                        : r.isVisible;
                    if (!rendererVisible) continue;
                    e.visibleNow = true;
                    float d = (r.bounds.center - camPos).sqrMagnitude;
                    if (d < e.sqrDistance) e.sqrDistance = d;
                }

                if (e.visibleNow)
                {
                    e.lastVisibleTime = now;
                    visible.Add(e);
                }
                else if (e.targets != null && now - e.lastVisibleTime > EvictAfterSeconds)
                {
                    // Reclaim VRAM: drop the decompressed cache; it will re-stream on next view.
                    e.targets.Release();
                    e.targets = null;
                    e.residentCount = 0;
                    System.Array.Clear(e.tileResident, 0, e.tileResident.Length);
                    e.version++;
                }
            }

            // 2. Nearest visible materials get admission first. Registration order breaks
            // equal-distance ties so selection is deterministic across runs.
            visible.Sort(ComparePriority);

            // A runtime budget reduction must take effect immediately. Evict hidden caches
            // first, then the farthest visible caches. Normal camera movement never replaces a
            // still-visible allocation, which avoids oscillating allocate/release thrash.
            TrimToBudget();

            long committedBytes = TotalCacheVram();
            long maxBytes = System.Math.Max(MaxCacheBytes, 0L);
            for (int vi = 0; vi < visible.Count; vi++)
            {
                Entry e = visible[vi];
                if (e.targets != null)
                {
                    e.selectedForBudget = true;
                    continue;
                }

                long required = e.predictedCacheBytes;
                if (required <= maxBytes - committedBytes)
                {
                    e.selectedForBudget = true;
                    committedBytes += required; // reserve before any RenderTexture allocation
                }
                else
                {
                    // Strict-cap policy: an entry larger than the entire budget is never
                    // allocated. It keeps its original material and remains explicitly deferred.
                    e.budgetBlocked = true;
                }
            }

            int budget = BudgetTilesPerFrame;
            for (int vi = 0; vi < visible.Count && budget > 0; vi++)
            {
                Entry e = visible[vi];
                if (!e.selectedForBudget) continue;

                if (e.targets == null)
                {
                    e.targets = _decomp.AllocateTargets(e.cacheW, e.cacheH, e.layout, true);
                    // Prediction is conservative and should always cover the allocation. Keep a
                    // hard guard so platform-specific format changes can never violate the cap.
                    if (TotalCacheVram() > maxBytes)
                    {
                        e.targets.Release();
                        e.targets = null;
                        e.selectedForBudget = false;
                        e.budgetBlocked = true;
                        continue;
                    }
                }

                for (int ty = 0; ty < e.tilesY && budget > 0; ty++)
                {
                    for (int tx = 0; tx < e.tilesX && budget > 0; tx++)
                    {
                        int idx = ty * e.tilesX + tx;
                        if (e.tileResident[idx]) continue;

                        int x0 = tx * TileSize;
                        int y0 = ty * TileSize;
                        int w = Mathf.Min(TileSize, e.cacheW - x0);
                        int h = Mathf.Min(TileSize, e.cacheH - y0);
                        var rect = new RectInt(x0, y0, w, h);

                        try
                        {
                            _decomp.DecompressRegion(e.assets, e.layout, e.targets, e.colorMip, rect, new Vector2Int(x0, y0));
                        }
                        catch
                        {
                            // Never retain or later present a partially written cache after a
                            // dispatch/binding failure. The next successful Tick starts clean.
                            e.targets.Release();
                            e.targets = null;
                            e.residentCount = 0;
                            System.Array.Clear(e.tileResident, 0, e.tileResident.Length);
                            e.version++;
                            throw;
                        }
                        e.tileResident[idx] = true;
                        e.residentCount++;
                        budget--;

                        // Regenerate mips once the whole cache is filled (cheap box filter for minification).
                        if (e.residentCount == e.tileResident.Length)
                        {
                            NtcDecompressor.GenerateMips(e.targets);
                            e.version++;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Force every resident tile of every registered entry to re-decompress on the next Tick
        /// (keeps the allocated cache targets, just clears the "already decoded" bookkeeping).
        /// Used when a decode-time parameter that DecompressRegion consumes (e.g. the debug
        /// color-space override) changes at runtime, so the cache doesn't keep stale pixels.
        /// </summary>
        public void InvalidateAllTiles()
        {
            foreach (var kv in _entries)
            {
                Entry e = kv.Value;
                if (e.tileResident != null) System.Array.Clear(e.tileResident, 0, e.tileResident.Length);
                e.residentCount = 0;
                e.version++;
            }
        }

        public void Release(string id)
        {
            Entry e;
            if (_entries.TryGetValue(id, out e))
            {
                if (e.targets != null) e.targets.Release();
                _entries.Remove(id);
            }
        }

        public void ReleaseAll()
        {
            foreach (var kv in _entries)
                if (kv.Value.targets != null) kv.Value.targets.Release();
            _entries.Clear();
        }

        public long TotalCacheVram()
        {
            long b = 0;
            foreach (var kv in _entries)
                // Use the exact mip-chain prediction used for admission. RenderTexture's own
                // accounting helper historically rounded the mip multiplier to 1.3333.
                if (kv.Value.targets != null) b += kv.Value.predictedCacheBytes;
            return b;
        }

        public int VisibleEntryCount { get { return CountVisible(0); } }
        public int EligibleVisibleEntryCount { get { return CountVisible(1); } }
        public int ResidentVisibleEntryCount { get { return CountVisible(2); } }
        public int DeferredVisibleEntryCount { get { return CountVisible(3); } }

        int CountVisible(int kind)
        {
            int count = 0;
            foreach (var kv in _entries)
            {
                Entry e = kv.Value;
                if (!e.visibleNow) continue;
                if (kind == 0
                    || (kind == 1 && e.selectedForBudget)
                    || (kind == 2 && e.selectedForBudget && e.IsComplete)
                    || (kind == 3 && e.budgetBlocked)) count++;
            }
            return count;
        }

        static int ComparePriority(Entry a, Entry b)
        {
            int distance = a.sqrDistance.CompareTo(b.sqrDistance);
            return distance != 0 ? distance : a.registrationOrder.CompareTo(b.registrationOrder);
        }

        void TrimToBudget()
        {
            long maxBytes = System.Math.Max(MaxCacheBytes, 0L);
            if (TotalCacheVram() <= maxBytes) return;

            var candidates = new List<Entry>();
            foreach (var kv in _entries)
                if (kv.Value.targets != null) candidates.Add(kv.Value);
            candidates.Sort((a, b) =>
            {
                if (a.visibleNow != b.visibleNow) return a.visibleNow ? 1 : -1;
                if (!a.visibleNow)
                {
                    int age = a.lastVisibleTime.CompareTo(b.lastVisibleTime);
                    if (age != 0) return age;
                }
                else
                {
                    int distance = b.sqrDistance.CompareTo(a.sqrDistance);
                    if (distance != 0) return distance;
                }
                return b.registrationOrder.CompareTo(a.registrationOrder);
            });

            for (int i = 0; i < candidates.Count && TotalCacheVram() > maxBytes; i++)
                ReleaseTargets(candidates[i]);
        }

        static void ReleaseTargets(Entry e)
        {
            if (e.targets != null) e.targets.Release();
            e.targets = null;
            e.residentCount = 0;
            if (e.tileResident != null) System.Array.Clear(e.tileResident, 0, e.tileResident.Length);
            e.version++;
        }

        static long PredictCacheBytes(int width, int height, NtcPbrChannelLayout layout, bool mips)
        {
            int bytesPerPixel = 0;
            if (layout.HasColor) bytesPerPixel += 4;
            if (layout.HasNormal) bytesPerPixel += 4;
            if (layout.HasMask)
                bytesPerPixel += SystemInfo.IsFormatSupported(GraphicsFormat.R16G16B16A16_UNorm, FormatUsage.LoadStore) ? 8 : 4;
            if (layout.HasAO) bytesPerPixel += 4;

            long pixels = 0;
            int w = Mathf.Max(width, 1);
            int h = Mathf.Max(height, 1);
            do
            {
                pixels += (long)w * h;
                if (!mips || (w == 1 && h == 1)) break;
                w = Mathf.Max(w >> 1, 1);
                h = Mathf.Max(h >> 1, 1);
            } while (true);
            return pixels * bytesPerPixel;
        }
    }
}
