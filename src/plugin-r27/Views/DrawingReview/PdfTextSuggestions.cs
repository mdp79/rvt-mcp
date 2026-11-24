using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using UglyToad.PdfPig.Content;

namespace RvtMcp.Plugin.Views.DrawingReview
{
    internal static class PdfTextSuggestions
    {
        private static readonly Regex DoorLabel = new(@"^(?:D|DR|DOOR)[-_]?\d{1,4}[A-Z]?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex WindowLabel = new(@"^(?:W|WIN|WINDOW)[-_]?\d{1,4}[A-Z]?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex Elevation = new(@"^[+−±-]\d{1,5}(?:[.,]\d{1,3})?(?:mm|m)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static IEnumerable<DrawingMark> Read(Page page, DrawingRole role)
        {
            // Avoid an offset between text and the Revit preview on rotated or offset crops.
            var bounds = page.CropBox.Bounds;
            if (page.Rotation.Value != 0 || Math.Abs(bounds.Left) > 0.01 || Math.Abs(bounds.Bottom) > 0.01
                || page.Width <= 0 || page.Height <= 0)
                yield break;

            var words = page.GetWords().ToArray();
            foreach (var candidate in ConstructionEvidence.Find(words.Select(word =>
            {
                var box = word.BoundingBox;
                return new ConstructionEvidence.Word(word.Text.Trim(), box.Left, page.Height - box.Top,
                    box.Right, page.Height - box.Bottom);
            }).Where(word => word.Text.Length > 0 && word.Right > word.Left && word.Bottom > word.Top)).Take(60))
            {
                var point = new Point(candidate.X / page.Width, candidate.Y / page.Height);
                if (point.X is >= 0 and <= 1 && point.Y is >= 0 and <= 1)
                    yield return new DrawingMark(MarkKind.Construction, point, Suggested: true, Construction: candidate.Spec);
            }

            foreach (var word in words)
            {
                string text = word.Text.Trim();
                if (text.Length is 0 or > 20) continue;
                MarkKind? kind = DoorLabel.IsMatch(text) ? MarkKind.Door
                    : WindowLabel.IsMatch(text) ? MarkKind.Window
                    : role != DrawingRole.Plan && Elevation.IsMatch(text) ? MarkKind.Height : null;
                if (!kind.HasValue) continue;
                var center = word.BoundingBox.Centroid;
                var point = new Point(center.X / page.Width, 1 - center.Y / page.Height);
                if (point.X is >= 0 and <= 1 && point.Y is >= 0 and <= 1)
                    yield return new DrawingMark(kind.Value, point,
                        Label: kind == MarkKind.Height ? $"Printed datum {text}" : text, Suggested: true);
            }
        }
    }
}
