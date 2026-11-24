using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Media.Imaging;
using Newtonsoft.Json;

namespace RvtMcp.Plugin.Views.DrawingReview
{
    internal sealed class PlanAgentExchange
    {
        private const int MaxProposalSize = 512 * 1024;
        private readonly Lazy<byte[]> _png;
        private readonly ConstructionSource _source;
        private readonly BitmapSource _bitmap;
        private readonly DrawingRole _role;

        public PlanAgentExchange(ConstructionSource source, BitmapSource bitmap, DrawingRole role)
        {
            _source = source;
            _bitmap = bitmap;
            _role = role;
            _png = new Lazy<byte[]>(() => EncodePng(bitmap));
        }

        public static byte[] EncodePng(BitmapSource bitmap)
        {
            using var buffer = new MemoryStream();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            encoder.Save(buffer);
            return buffer.ToArray();
        }

        private string ImageSha256 => Convert.ToHexString(SHA256.HashData(_png.Value)).ToLowerInvariant();

        public void Export(string filePath)
        {
            var manifest = JsonConvert.SerializeObject(new
            {
                formatVersion = 1,
                drawingRole = _role.ToString(),
                viewKey = _source.ViewKey,
                imageSha256 = ImageSha256,
                pixelWidth = _bitmap.PixelWidth,
                pixelHeight = _bitmap.PixelHeight
            });
            using var buffer = new MemoryStream();
            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                using (var plan = zip.CreateEntry("plan.png", CompressionLevel.NoCompression).Open())
                    plan.Write(_png.Value);
                using var writer = new StreamWriter(zip.CreateEntry("request.json").Open(), new UTF8Encoding(false));
                writer.Write(manifest);
            }
            File.WriteAllBytes(filePath, buffer.ToArray());
        }

        public DrawingMark[] Import(string path)
        {
            using var stream = File.OpenRead(path);
            if (stream.Length > MaxProposalSize)
                throw new InvalidDataException("The agent proposal is too large.");
            using var reader = new StreamReader(stream);
            string json = reader.ReadToEnd();
            if (_role != DrawingRole.Plan)
                return PlanAgentFormat.ReadReference(json, _source.ViewKey, ImageSha256, _role.ToString())
                    .Select(note => new DrawingMark(Enum.Parse<MarkKind>(note.Kind), new Point(note.X, note.Y),
                        Label: note.Label, Suggested: true, Construction: note.Construction, AgentEvidence: note.Evidence)).ToArray();
            var marks = PlanAgentFormat.Read(json, _source.ViewKey, ImageSha256);
            var result = new DrawingMark[marks.Count];
            for (int i = 0; i < marks.Count; i++)
            {
                var mark = marks[i];
                result[i] = new DrawingMark(Enum.Parse<MarkKind>(mark.Kind),
                    new Point(mark.X1, mark.Y1), new Point(mark.X2, mark.Y2), Suggested: true, AgentEvidence: mark.Evidence);
            }
            return result;
        }
    }
}
