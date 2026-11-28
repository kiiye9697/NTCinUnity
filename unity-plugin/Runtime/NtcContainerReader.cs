// NtcContainerReader.cs
// Pure .NET (no UnityEngine dependency) reader for the NTC Texture Set container format
// ("NTEX", see nvidia-backend/docs/TextureSetFile.md). It turns a raw ".ntc" file into
// a plain C# object graph
// (NtcTextureSetDocument) plus the raw data chunk bytes, without linking against LibNTC/CUDA.
//
// This file is designed to be usable in three contexts unchanged:
//   1. Compiled standalone with csc.exe for quick validation against real .ntc files.
//   2. Dropped into a Unity Editor assembly to back a ScriptedImporter.
//   3. Dropped into a Unity Runtime assembly for runtime/streaming scenarios.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Ntc.Packing
{
    public class NtcBufferView
    {
        public long Offset;
        public long StoredSize;
        public string Compression = "None"; // "None" or "GDeflate"
        public long UncompressedSize;
        public uint Crc32;

        public bool IsCompressed
        {
            get { return Compression != null && Compression != "None"; }
        }
    }

    public class NtcMlpLayer
    {
        public int InputChannels;
        public int OutputChannels;
        public int WeightView = -1;
        public int ScaleView = -1; // -1 => no view, all scales assumed 1.0
        public int BiasView = -1;
        public string WeightType = "Int8";
        public string ScaleType = "Float32";
        public string BiasType = "Float32";
    }

    public class NtcMlp
    {
        public List<NtcMlpLayer> Layers = new List<NtcMlpLayer>();
        public string Activation = "HGELUClamp";
        public string WeightLayout = "ColumnMajor";
    }

    public class NtcBcModeBuffer
    {
        public int MipLevel;
        public int View;
    }

    public class NtcTexture
    {
        public string Name;
        public int FirstChannel;
        public int NumChannels;
        public string ChannelFormat = "UNORM8";
        public string RgbColorSpace = "Linear";
        public string AlphaColorSpace = "Linear";
        public string BcFormat = "None";
        public List<NtcBcModeBuffer> BcModeBuffers = new List<NtcBcModeBuffer>();
    }

    public class NtcChannel
    {
        public string ColorSpace = "Linear";
    }

    public class NtcLatentImage
    {
        public int Width;
        public int Height;
        public List<int> LayerViews = new List<int>();
    }

    public class NtcColorMip
    {
        public int Width;
        public int Height;
        public int LatentMip = -1; // -1 => not driven by NTC decompression
        public float PositionLod;
        public float PositionScale;
    }

    public class NtcLatentShape
    {
        public int NumFeatures = 16;
    }

    public class NtcTextureSetDocument
    {
        public int SchemaVersion;
        public int Width;
        public int Height;
        public int NumColorMips = 1;
        public int NumChannels;
        public NtcLatentShape LatentShape;
        public List<NtcMlp> MlpVersions = new List<NtcMlp>();
        public List<NtcTexture> Textures = new List<NtcTexture>();
        public List<NtcChannel> Channels = new List<NtcChannel>();
        public List<NtcLatentImage> Latents = new List<NtcLatentImage>();
        public List<NtcColorMip> ColorMips = new List<NtcColorMip>();
        public List<NtcBufferView> Views = new List<NtcBufferView>();
    }

    /// <summary>
    /// Parsed NTC metadata with either a resident binary chunk or a file-backed view source.
    /// </summary>
    public class NtcContainer
    {
        public uint ContainerVersion;
        public NtcTextureSetDocument Document;
        public byte[] DataChunk; // Null for LoadMetadata; GetViewBytes reads only the requested range.
        internal string SourcePath;
        internal long SourceDataOffset, SourceDataLength;
        public long DataLength { get { return DataChunk != null ? DataChunk.LongLength : SourceDataLength; } }
        public bool HasDataSource { get { return DataChunk != null || SourcePath != null; } }

        /// <summary>
        /// Returns the raw (possibly still GDeflate-compressed) bytes for a BufferView by index.
        /// Decompression is intentionally NOT performed here (see architecture doc, GDeflate risk item);
        /// callers must check view.IsCompressed and handle decompression separately if needed.
        /// </summary>
        public byte[] GetViewBytes(int viewIndex)
        {
            if (viewIndex < 0 || viewIndex >= Document.Views.Count)
                throw new IndexOutOfRangeException("Invalid NTC buffer view index: " + viewIndex);

            NtcBufferView view = Document.Views[viewIndex];
            if (view.IsCompressed)
            {
                throw new NotSupportedException(
                    "NTC buffer view " + viewIndex + " uses " + view.Compression +
                    " compression, but the Unity POC reader only supports uncompressed views. " +
                    "Rebake with ntc-cli option: --gdeflate off");
            }
            long chunkLength = DataLength;
            if (view.Offset < 0 || view.StoredSize < 0 || view.StoredSize > int.MaxValue
                || view.Offset > chunkLength || view.StoredSize > chunkLength - view.Offset)
                throw new InvalidDataException("NTC buffer view " + viewIndex + " points outside the data chunk.");

            byte[] result = new byte[(int)view.StoredSize];
            if (DataChunk != null)
                Buffer.BlockCopy(DataChunk, (int)view.Offset, result, 0, result.Length);
            else
            {
                if (SourcePath == null) throw new InvalidOperationException("NTC container has no binary data source.");
                using (var stream = File.OpenRead(SourcePath))
                {
                    stream.Seek(SourceDataOffset + view.Offset, SeekOrigin.Begin);
                    int read = 0;
                    while (read < result.Length)
                    {
                        int count = stream.Read(result, read, result.Length - read);
                        if (count == 0) throw new EndOfStreamException("NTC buffer view was truncated: " + viewIndex);
                        read += count;
                    }
                }
            }
            return result;
        }
    }

    public static class NtcContainerReader
    {
        private static readonly byte[] ExpectedSignature = Encoding.ASCII.GetBytes("NTEX");

        public static NtcContainer Load(string path)
        {
            return Load(path, false);
        }

        // GPU upload consumes only selected views. Avoid retaining the entire container's
        // binary chunk (including unused weight variants/BC metadata) for the scene lifetime.
        public static NtcContainer LoadMetadata(string path)
        {
            return Load(path, true);
        }

        static NtcContainer Load(string path, bool deferBinaryData)
        {
            using (FileStream fs = File.OpenRead(path))
            using (BinaryReader br = new BinaryReader(fs))
            {
                byte[] signature = br.ReadBytes(4);
                if (signature.Length != 4) throw new EndOfStreamException("Truncated NTC signature.");
                for (int i = 0; i < 4; i++)
                {
                    if (signature[i] != ExpectedSignature[i])
                        throw new InvalidDataException("Not an NTC container (bad signature) in file: " + path);
                }

                uint containerVersion = br.ReadUInt32();
                ulong jsonOffset = br.ReadUInt64();
                ulong jsonSize = br.ReadUInt64();
                ulong dataOffset = br.ReadUInt64();
                ulong dataSize = br.ReadUInt64();

                ulong length = (ulong)fs.Length;
                if (jsonOffset > length || jsonSize > length - jsonOffset || jsonSize > int.MaxValue
                    || dataOffset > length || dataSize > length - dataOffset
                    || (!deferBinaryData && dataSize > int.MaxValue))
                    throw new InvalidDataException("NTC JSON or binary chunk lies outside the file or exceeds the reader limit.");

                fs.Seek((long)jsonOffset, SeekOrigin.Begin);
                byte[] jsonBytes = br.ReadBytes((int)jsonSize);
                string jsonText = Encoding.UTF8.GetString(jsonBytes);

                object root = NtcJson.Parse(jsonText);
                NtcTextureSetDocument document = ParseDocument(NtcJson.AsObject(root));

                fs.Seek((long)dataOffset, SeekOrigin.Begin);
                byte[] dataChunk = deferBinaryData ? null : br.ReadBytes((int)dataSize);

                NtcContainer container = new NtcContainer();
                container.ContainerVersion = containerVersion;
                container.Document = document;
                container.DataChunk = dataChunk;
                container.SourcePath = Path.GetFullPath(path);
                container.SourceDataOffset = (long)dataOffset;
                container.SourceDataLength = (long)dataSize;
                return container;
            }
        }

        private static NtcTextureSetDocument ParseDocument(Dictionary<string, object> root)
        {
            var doc = new NtcTextureSetDocument();
            doc.SchemaVersion = NtcJson.GetInt(root, "schemaVersion", 1);
            doc.Width = NtcJson.GetInt(root, "width", 0);
            doc.Height = NtcJson.GetInt(root, "height", 0);
            doc.NumColorMips = NtcJson.GetInt(root, "numColorMips", 1);
            doc.NumChannels = NtcJson.GetInt(root, "numChannels", 0);

            Dictionary<string, object> latentShapeObj = NtcJson.GetObject(root, "latentShape");
            if (latentShapeObj != null)
            {
                doc.LatentShape = new NtcLatentShape();
                doc.LatentShape.NumFeatures = NtcJson.GetInt(latentShapeObj, "numFeatures", 16);
            }

            List<object> mlpArray = NtcJson.GetArray(root, "mlpVersions");
            if (mlpArray != null)
            {
                foreach (object mlpObj in mlpArray)
                    doc.MlpVersions.Add(ParseMlp(NtcJson.AsObject(mlpObj)));
            }

            List<object> texArray = NtcJson.GetArray(root, "textures");
            if (texArray != null)
            {
                foreach (object texObj in texArray)
                    doc.Textures.Add(ParseTexture(NtcJson.AsObject(texObj)));
            }

            List<object> chArray = NtcJson.GetArray(root, "channels");
            if (chArray != null)
            {
                foreach (object chObj in chArray)
                {
                    Dictionary<string, object> chDict = NtcJson.AsObject(chObj);
                    var ch = new NtcChannel();
                    ch.ColorSpace = NtcJson.GetString(chDict, "colorSpace", "Linear");
                    doc.Channels.Add(ch);
                }
            }

            List<object> latentArray = NtcJson.GetArray(root, "latents");
            if (latentArray != null)
            {
                foreach (object latentObj in latentArray)
                {
                    Dictionary<string, object> latentDict = NtcJson.AsObject(latentObj);
                    var latent = new NtcLatentImage();
                    latent.Width = NtcJson.GetInt(latentDict, "width", 0);
                    latent.Height = NtcJson.GetInt(latentDict, "height", 0);
                    List<object> layerViews = NtcJson.GetArray(latentDict, "layerViews");
                    if (layerViews != null)
                    {
                        foreach (object lv in layerViews)
                            latent.LayerViews.Add((int)(double)lv);
                    }
                    doc.Latents.Add(latent);
                }
            }

            List<object> colorMipArray = NtcJson.GetArray(root, "colorMips");
            if (colorMipArray != null)
            {
                foreach (object cmObj in colorMipArray)
                {
                    Dictionary<string, object> cmDict = NtcJson.AsObject(cmObj);
                    var cm = new NtcColorMip();
                    cm.Width = NtcJson.GetInt(cmDict, "width", 0);
                    cm.Height = NtcJson.GetInt(cmDict, "height", 0);
                    cm.LatentMip = NtcJson.GetInt(cmDict, "latentMip", -1);
                    cm.PositionLod = NtcJson.GetFloat(cmDict, "positionLod", 0f);
                    cm.PositionScale = NtcJson.GetFloat(cmDict, "positionScale", 1f);
                    doc.ColorMips.Add(cm);
                }
            }

            List<object> viewArray = NtcJson.GetArray(root, "views");
            if (viewArray != null)
            {
                foreach (object viewObj in viewArray)
                {
                    Dictionary<string, object> viewDict = NtcJson.AsObject(viewObj);
                    var view = new NtcBufferView();
                    view.Offset = NtcJson.GetLong(viewDict, "offset", 0);
                    view.StoredSize = NtcJson.GetLong(viewDict, "storedSize", 0);
                    view.Compression = NtcJson.GetString(viewDict, "compression", "None");
                    view.UncompressedSize = NtcJson.GetLong(viewDict, "uncompressedSize", view.StoredSize);
                    view.Crc32 = (uint)NtcJson.GetLong(viewDict, "crc32", 0);
                    doc.Views.Add(view);
                }
            }

            return doc;
        }

        private static NtcMlp ParseMlp(Dictionary<string, object> mlpDict)
        {
            var mlp = new NtcMlp();
            mlp.Activation = NtcJson.GetString(mlpDict, "activation", "HGELUClamp");
            mlp.WeightLayout = NtcJson.GetString(mlpDict, "weightLayout", "ColumnMajor");

            List<object> layerArray = NtcJson.GetArray(mlpDict, "layers");
            if (layerArray != null)
            {
                foreach (object layerObj in layerArray)
                {
                    Dictionary<string, object> layerDict = NtcJson.AsObject(layerObj);
                    var layer = new NtcMlpLayer();
                    layer.InputChannels = NtcJson.GetInt(layerDict, "inputChannels", 0);
                    layer.OutputChannels = NtcJson.GetInt(layerDict, "outputChannels", 0);
                    layer.WeightView = NtcJson.GetInt(layerDict, "weightView", -1);
                    layer.ScaleView = NtcJson.GetInt(layerDict, "scaleView", -1);
                    layer.BiasView = NtcJson.GetInt(layerDict, "biasView", -1);
                    layer.WeightType = NtcJson.GetString(layerDict, "weightType", "Int8");
                    layer.ScaleType = NtcJson.GetString(layerDict, "scaleType", "Float32");
                    layer.BiasType = NtcJson.GetString(layerDict, "biasType", "Float32");
                    mlp.Layers.Add(layer);
                }
            }
            return mlp;
        }

        private static NtcTexture ParseTexture(Dictionary<string, object> texDict)
        {
            var tex = new NtcTexture();
            tex.Name = NtcJson.GetString(texDict, "name", "");
            tex.FirstChannel = NtcJson.GetInt(texDict, "firstChannel", 0);
            tex.NumChannels = NtcJson.GetInt(texDict, "numChannels", 0);
            tex.ChannelFormat = NtcJson.GetString(texDict, "channelFormat", "UNORM8");
            tex.RgbColorSpace = NtcJson.GetString(texDict, "rgbColorSpace", "Linear");
            tex.AlphaColorSpace = NtcJson.GetString(texDict, "alphaColorSpace", "Linear");
            tex.BcFormat = NtcJson.GetString(texDict, "bcFormat", "None");

            List<object> bcArray = NtcJson.GetArray(texDict, "bcModeBuffers");
            if (bcArray != null)
            {
                foreach (object bcObj in bcArray)
                {
                    Dictionary<string, object> bcDict = NtcJson.AsObject(bcObj);
                    var bc = new NtcBcModeBuffer();
                    bc.MipLevel = NtcJson.GetInt(bcDict, "mipLevel", 0);
                    bc.View = NtcJson.GetInt(bcDict, "view", -1);
                    tex.BcModeBuffers.Add(bc);
                }
            }
            return tex;
        }
    }
}
