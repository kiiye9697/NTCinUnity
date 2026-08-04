// NtcPackingReport.cs
// Builds a human-readable summary of a parsed NtcContainer. Pure .NET, no UnityEngine dependency,
// so it can be exercised both by the standalone validation harness and by the Unity Editor test window.
using System.Text;

namespace Ntc.Packing
{
    public static class NtcPackingReport
    {
        public static string Build(string fileName, NtcContainer container)
        {
            var doc = container.Document;
            var sb = new StringBuilder();

            sb.AppendLine("==================================================================");
            sb.AppendLine("NTC Packing Report: " + fileName);
            sb.AppendLine("==================================================================");
            sb.AppendLine(string.Format("Container version : 0x{0:X}", container.ContainerVersion));
            sb.AppendLine(string.Format("Schema version    : {0}", doc.SchemaVersion));
            sb.AppendLine(string.Format("Texture set size  : {0} x {1}, {2} color mips, {3} channels",
                doc.Width, doc.Height, doc.NumColorMips, doc.NumChannels));
            if (doc.LatentShape != null)
                sb.AppendLine(string.Format("Latent shape      : numFeatures = {0}", doc.LatentShape.NumFeatures));

            sb.AppendLine();
            sb.AppendLine(string.Format("MLP versions ({0}):", doc.MlpVersions.Count));
            for (int m = 0; m < doc.MlpVersions.Count; m++)
            {
                NtcMlp mlp = doc.MlpVersions[m];
                sb.AppendLine(string.Format("  [{0}] activation={1} weightLayout={2}", m, mlp.Activation, mlp.WeightLayout));
                for (int l = 0; l < mlp.Layers.Count; l++)
                {
                    NtcMlpLayer layer = mlp.Layers[l];
                    sb.AppendLine(string.Format(
                        "      layer {0}: {1} -> {2}  weightType={3} scaleType={4} biasType={5}  (weightView={6}, scaleView={7}, biasView={8})",
                        l, layer.InputChannels, layer.OutputChannels, layer.WeightType, layer.ScaleType, layer.BiasType,
                        layer.WeightView, layer.ScaleView, layer.BiasView));
                }
            }

            sb.AppendLine();
            sb.AppendLine(string.Format("Textures ({0}):", doc.Textures.Count));
            foreach (NtcTexture tex in doc.Textures)
            {
                sb.AppendLine(string.Format("  {0}: channels[{1}..{2}] format={3} rgbSpace={4} bcFormat={5} bcModeBuffers={6}",
                    tex.Name, tex.FirstChannel, tex.FirstChannel + tex.NumChannels - 1,
                    tex.ChannelFormat, tex.RgbColorSpace, tex.BcFormat, tex.BcModeBuffers.Count));
            }

            sb.AppendLine();
            sb.AppendLine(string.Format("Latent images / neural mips ({0}):", doc.Latents.Count));
            for (int i = 0; i < doc.Latents.Count; i++)
            {
                NtcLatentImage li = doc.Latents[i];
                sb.AppendLine(string.Format("  mip {0}: {1} x {2}, {3} layer views", i, li.Width, li.Height, li.LayerViews.Count));
            }

            sb.AppendLine();
            sb.AppendLine(string.Format("Color mips ({0}):", doc.ColorMips.Count));
            foreach (NtcColorMip cm in doc.ColorMips)
            {
                sb.AppendLine(string.Format("  {0}x{1}  latentMip={2} positionLod={3} positionScale={4}",
                    cm.Width, cm.Height, cm.LatentMip, cm.PositionLod, cm.PositionScale));
            }

            sb.AppendLine();
            long compressedBytes = 0, uncompressedBytes = 0;
            int compressedViewCount = 0;
            foreach (NtcBufferView view in doc.Views)
            {
                if (view.IsCompressed)
                {
                    compressedViewCount++;
                    compressedBytes += view.StoredSize;
                    uncompressedBytes += view.UncompressedSize;
                }
            }
            sb.AppendLine(string.Format("Buffer views      : {0} total, {1} GDeflate-compressed ({2} bytes -> {3} bytes)",
                doc.Views.Count, compressedViewCount, compressedBytes, uncompressedBytes));
            sb.AppendLine(string.Format("Data chunk size   : {0} bytes", container.DataChunk.Length));

            return sb.ToString();
        }
    }
}
