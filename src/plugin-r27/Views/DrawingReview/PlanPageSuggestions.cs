using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RvtMcp.Plugin.Views.DrawingReview
{
    internal static class PlanPageSuggestions
    {
        public static Rect WholePage => new(0, 0, 1, 1);

        public static IReadOnlyList<DrawingMark> Read(BitmapSource bitmap, Rect area, bool wholePage = false)
        {
            int x = (int)(area.Left * bitmap.PixelWidth);
            int y = (int)(area.Top * bitmap.PixelHeight);
            int w = Math.Min(bitmap.PixelWidth - x, (int)(area.Width * bitmap.PixelWidth));
            int h = Math.Min(bitmap.PixelHeight - y, (int)(area.Height * bitmap.PixelHeight));
            if (Math.Min(w, h) < 60) return Array.Empty<DrawingMark>();

            BitmapSource image = wholePage ? bitmap : new CroppedBitmap(bitmap, new Int32Rect(x, y, w, h));
            double scale = Math.Min(1, 1800.0 / Math.Max(w, h));
            if (scale < 1) image = new TransformedBitmap(image, new ScaleTransform(scale, scale));
            var gray = new FormatConvertedBitmap(image, PixelFormats.Gray8, null, 0);
            var pixels = new byte[gray.PixelWidth * gray.PixelHeight];
            gray.CopyPixels(pixels, gray.PixelWidth, 0);
            var candidates = PlanInkDetector.Detect(pixels, gray.PixelWidth, gray.PixelHeight, wholePage);
            if (candidates.Count == 0)
                candidates = PlanInkDetector.Detect(pixels, gray.PixelWidth, gray.PixelHeight, wholePage, inkThreshold: 185);

            Point PagePoint(double px, double py) => new((x + px * w) / bitmap.PixelWidth,
                (y + py * h) / bitmap.PixelHeight);
            var marks = new List<DrawingMark>();
            foreach (var candidate in candidates)
            {
                var kind = Enum.Parse<MarkKind>(candidate.Type.ToString());
                bool horizontal = candidate.X2 != candidate.X1;
                double? thickness = candidate.Width > 0
                    ? candidate.Width * (horizontal ? (double)h / bitmap.PixelHeight : (double)w / bitmap.PixelWidth)
                    : null;
                marks.Add(new DrawingMark(kind, PagePoint(candidate.X1, candidate.Y1), PagePoint(candidate.X2, candidate.Y2),
                    Suggested: true, PageWallThickness: thickness));
            }
            var boundary = PlanInkDetector.FindFloorFootprint(candidates).Select(p => PagePoint(p.X, p.Y)).ToArray();
            if (boundary.Length > 0)
                marks.Add(new DrawingMark(MarkKind.FloorFootprint, boundary[0], Suggested: true, Boundary: boundary));
            return marks;
        }

        public static bool SameGeometry(DrawingMark first, DrawingMark second)
        {
            const double tolerance = 0.006;
            if (first.Boundary != null && second.Boundary != null)
                return first.Boundary.Length == second.Boundary.Length
                    && first.Boundary.Zip(second.Boundary).All(pair => (pair.First - pair.Second).Length < tolerance);
            if (!(first.IsWall && second.IsWall) && first.Kind != second.Kind) return false;
            if (!first.End.HasValue || !second.End.HasValue) return false;
            return ((first.Start - second.Start).Length < tolerance && (first.End.Value - second.End.Value).Length < tolerance)
                || ((first.Start - second.End.Value).Length < tolerance && (first.End.Value - second.Start).Length < tolerance);
        }
    }
}
