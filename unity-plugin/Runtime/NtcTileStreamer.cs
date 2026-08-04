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

namespace Ntc.Unity
{
    public class NtcTileStreamer
    {
        public int TileSize = 256;
        public int CapResolution = 1024;
        public int BudgetTilesPerFrame = 8;
        public float EvictAfterSeconds = 5f;

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
        }

        readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>();

        public NtcTileStreamer(NtcDecompressor decomp) { _decomp = decomp; }

        public bool IsRegistered(string id) { return _entries.ContainsKey(id); }

        public Entry Register(string id, NtcBuiltAssets assets, NtcPbrChannelLayout layout, Renderer[] renderers)
        {
            Entry e;
            if (_entries.TryGetValue(id, out e))
                return e;

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
            };
            e.tilesX = Mathf.Max((e.cacheW + TileSize - 1) / TileSize, 1);
            e.tilesY = Mathf.Max((e.cacheH + TileSize - 1) / TileSize, 1);
            e.tileResident = new bool[e.tilesX * e.tilesY];
            _entries[id] = e;
            return e;
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

            // 1. Update visibility + distance, and evict entries hidden too long.
            var visible = new List<Entry>();
            foreach (var kv in _entries)
            {
                Entry e = kv.Value;
                e.visibleNow = false;
                e.sqrDistance = float.MaxValue;
                for (int i = 0; i < e.renderers.Length; i++)
                {
                    Renderer r = e.renderers[i];
                    if (r == null || !r.isVisible) continue;
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

            // 2. Nearest visible materials get the budget first.
            visible.Sort((a, b) => a.sqrDistance.CompareTo(b.sqrDistance));

            int budget = BudgetTilesPerFrame;
            for (int vi = 0; vi < visible.Count && budget > 0; vi++)
            {
                Entry e = visible[vi];

                if (e.targets == null)
                {
                    e.targets = _decomp.AllocateTargets(e.cacheW, e.cacheH, e.layout, true);
                    e.version++;
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

                        _decomp.DecompressRegion(e.assets, e.layout, e.targets, e.colorMip, rect, new Vector2Int(x0, y0));
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
                if (kv.Value.targets != null) b += kv.Value.targets.VramBytes;
            return b;
        }
    }
}
