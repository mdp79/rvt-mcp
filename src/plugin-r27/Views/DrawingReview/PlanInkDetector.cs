using System;
using System.Collections.Generic;
using System.Linq;

namespace RvtMcp.Plugin.Views.DrawingReview
{
    // Reads strong horizontal and vertical plan linework from a page or a user-selected area.
    internal static class PlanInkDetector
    {
        internal enum Kind { ExteriorWall, InteriorWall, Door, Window }
        internal sealed record Candidate(Kind Type, double X1, double Y1, double X2, double Y2, double Width);
        internal sealed record Vertex(double X, double Y);
        private sealed record Run(int FirstRow, int LastRow, int Start, int End)
        {
            public int Center => (FirstRow + LastRow) / 2;
            public int Thickness => LastRow - FirstRow + 1;
            public int Length => End - Start + 1;
        }
        private sealed record Wall(bool Horizontal, int Center, int Start, int End, int Thickness)
        {
            public int Length => End - Start + 1;
        }

        public static IReadOnlyList<Candidate> Detect(byte[] gray, int width, int height, bool wholePage = false, int inkThreshold = 135)
        {
            bool Ink(int x, int y) => x >= 0 && x < width && y >= 0 && y < height && gray[y * width + x] < inkThreshold;
            var size = Math.Min(width, height);
            int minimumLength = Math.Max(20, size / 28);
            int maxThickness = Math.Max(8, size / 28);
            var walls = new List<Wall>();
            void AddWall(Wall wall, int rows, int length)
            {
                if (wall.Length < wall.Thickness * 4) return;
                // Ignore long bands close to the sheet edge when searching the whole page.
                if (wholePage && wall.Length > length * 0.80
                    && (wall.Center < rows * 0.08 || wall.Center > rows * 0.92)) return;
                if (!walls.Any(w => w.Horizontal == wall.Horizontal && Math.Abs(w.Center - wall.Center) <= 2
                    && Math.Min(w.End, wall.End) - Math.Max(w.Start, wall.Start) > wall.Length * 0.65))
                    walls.Add(wall);
            }
            foreach (bool horizontal in new[] { true, false })
            {
                int length = horizontal ? width : height;
                int rows = horizontal ? height : width;
                var strokes = new List<Run>();
                var previousRuns = new List<int>();
                for (int across = 2; across < rows - 2; across++)
                {
                    var currentRuns = new List<int>();
                    int start = -1;
                    int last = -1;
                    for (int along = 2; along < length - 1; along++)
                    {
                        if (along < length - 2 && (horizontal ? Ink(along, across) : Ink(across, along)))
                        {
                            if (start < 0) start = along;
                            last = along;
                        }
                        else if (start >= 0 && (along - last > 1 || along == length - 2))
                        {
                            if (last - start + 1 >= minimumLength)
                                currentRuns.Add(AddStroke(strokes, previousRuns, new Run(across, across, start, last)));
                            start = -1;
                        }
                    }
                    previousRuns = currentRuns;
                }

                // Solid walls produce a wide ink band instead of two thin edges.
                foreach (var band in strokes.Where(stroke => stroke.Thickness >= 3 && stroke.Thickness <= maxThickness))
                    AddWall(new Wall(horizontal, band.Center, band.Start, band.End, band.Thickness), rows, length);
                var edges = strokes.Where(stroke => stroke.Thickness <= 2).OrderBy(stroke => stroke.Center).ToArray();
                for (int index = 0; index < edges.Length; index++)
                {
                    var first = edges[index];
                    for (int nextIndex = index + 1; nextIndex < edges.Length; nextIndex++)
                    {
                        var second = edges[nextIndex];
                        int gap = second.Center - first.Center;
                        if (gap < 3) continue;
                        if (gap > maxThickness) break;
                        int overlap = Math.Min(first.End, second.End) - Math.Max(first.Start, second.Start) + 1;
                        if (overlap < minimumLength || overlap < Math.Max(first.Length, second.Length) * 0.6) continue;
                        AddWall(new Wall(horizontal, (first.Center + second.Center) / 2,
                            Math.Max(first.Start, second.Start), Math.Min(first.End, second.End), gap), rows, length);
                        break;
                    }
                }
            }

            var results = new List<Candidate>();
            Candidate Convert(Kind kind, Wall wall, int start, int end, int thickness) => wall.Horizontal
                ? new Candidate(kind, (double)start / width, (double)wall.Center / height, (double)end / width,
                    (double)wall.Center / height, (double)thickness / height)
                : new Candidate(kind, (double)wall.Center / width, (double)start / height, (double)wall.Center / width,
                    (double)end / height, (double)thickness / width);

            foreach (bool horizontal in new[] { true, false })
            {
                var parallel = walls.Where(w => w.Horizontal == horizontal).OrderBy(w => w.Center).ToArray();
                if (parallel.Length == 0) continue;
                foreach (var wall in parallel.OrderByDescending(w => w.Length).Take(150))
                {
                    bool exterior = parallel.Length >= 2 && (wall.Center - parallel[0].Center <= wall.Thickness
                        || parallel[^1].Center - wall.Center <= wall.Thickness);
                    results.Add(Convert(exterior ? Kind.ExteriorWall : Kind.InteriorWall, wall, wall.Start, wall.End, wall.Thickness));

                    var next = parallel.Where(w => w.Start > wall.End && Math.Abs(w.Center - wall.Center) <= 2
                        && Math.Abs(w.Thickness - wall.Thickness) <= 3).OrderBy(w => w.Start).FirstOrDefault();
                    if (next == null) continue;
                    int gap = next.Start - wall.End;
                    if (gap < Math.Max(9, wall.Thickness + 2) || gap > size / 6) continue;
                    bool WallInk(int along, int across) => horizontal ? Ink(along, across) : Ink(across, along);
                    var opening = ClassifyOpening(wall, gap, WallInk);
                    if (opening.HasValue)
                        results.Add(Convert(opening.Value, wall, wall.End, next.Start, 0));
                }
            }
            return results;
        }

        // Only suggest a footprint when wall runs and identified openings close all four
        // sides. A bounding box alone would invent floor area on L-shaped/open plans.
        public static IReadOnlyList<Vertex> FindFloorFootprint(IReadOnlyList<Candidate> detected)
        {
            var walls = detected.Where(c => c.Type is Kind.ExteriorWall or Kind.InteriorWall).ToArray();
            var horizontal = walls.Where(c => c.Y1 == c.Y2).ToArray();
            var vertical = walls.Where(c => c.X1 == c.X2).ToArray();
            if (horizontal.Length < 2 || vertical.Length < 2) return Array.Empty<Vertex>();

            double left = vertical.Min(c => c.X1), right = vertical.Max(c => c.X1);
            double top = horizontal.Min(c => c.Y1), bottom = horizontal.Max(c => c.Y1);
            if (right - left < 0.10 || bottom - top < 0.10) return Array.Empty<Vertex>();
            double xTolerance = Math.Clamp(vertical.Max(c => c.Width) / 2, 0.004, 0.012);
            double yTolerance = Math.Clamp(horizontal.Max(c => c.Width) / 2, 0.004, 0.012);
            bool Edge(bool isHorizontal, double center, double start, double end, double sideTolerance, double gapTolerance)
            {
                var spans = detected.Where(c => isHorizontal ? c.Y1 == c.Y2 && Math.Abs(c.Y1 - center) <= sideTolerance
                    : c.X1 == c.X2 && Math.Abs(c.X1 - center) <= sideTolerance)
                    .Select(c => isHorizontal ? (c.X1, c.X2) : (c.Y1, c.Y2))
                    .OrderBy(range => range.Item1);
                double covered = start;
                foreach (var (from, to) in spans)
                {
                    if (from > covered + gapTolerance) break;
                    covered = Math.Max(covered, to);
                    if (covered + gapTolerance >= end) return true;
                }
                return false;
            }
            if (!Edge(true, top, left, right, yTolerance, xTolerance) || !Edge(true, bottom, left, right, yTolerance, xTolerance)
                || !Edge(false, left, top, bottom, xTolerance, yTolerance) || !Edge(false, right, top, bottom, xTolerance, yTolerance))
                return Array.Empty<Vertex>();

            return new[] { new Vertex(left, top), new Vertex(right, top), new Vertex(right, bottom), new Vertex(left, bottom) };
        }

        private static int AddStroke(List<Run> strokes, List<int> previousRuns, Run run)
        {
            foreach (int i in previousRuns)
            {
                var prior = strokes[i];
                if (prior.LastRow != run.FirstRow - 1 || Math.Abs(prior.Start - run.Start) > 3 || Math.Abs(prior.End - run.End) > 3)
                    continue;
                strokes[i] = prior with { LastRow = run.LastRow };
                return i;
            }
            strokes.Add(run);
            return strokes.Count - 1;
        }

        private static Kind? ClassifyOpening(Wall wall, int gap, Func<int, int, bool> ink)
        {
            int first = wall.End + 2;
            int last = wall.End + gap - 2;
            for (int across = wall.Center - wall.Thickness / 2 + 1; across < wall.Center + wall.Thickness / 2; across++)
            {
                int present = 0;
                for (int along = first; along <= last; along++)
                    if (ink(along, across)) present++;
                if (present >= (last - first + 1) * 0.75)
                    return Kind.Window;
            }

            foreach (int side in new[] { -1, 1 })
            {
                // Require both a jamb/leaf near an end and ink in the swing area.
                int extent = Math.Min(gap, 70);
                bool leaf = new[] { wall.End, wall.End + gap }.Any(jamb =>
                    Enumerable.Range(-2, 5).Any(offset => Enumerable.Range(wall.Thickness, extent - wall.Thickness)
                        .Count(step => ink(jamb + offset, wall.Center + step * side)) >= (extent - wall.Thickness) * 0.65));
                if (!leaf) continue;
                int marks = 0;
                for (int along = first + gap / 5; along < last - gap / 5; along++)
                    for (int step = wall.Thickness + 1; step < extent; step++)
                        if (ink(along, wall.Center + step * side)) marks++;
                if (marks >= Math.Max(3, gap / 3))
                    return Kind.Door;
            }
            return null;
        }
    }
}
