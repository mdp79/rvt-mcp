using System;
using RvtMcp.Plugin.Views.DrawingReview;
using Xunit;

namespace RvtMcp.Tests
{
    public sealed class PlanInkDetectorTests
    {
        [Fact]
        public void Suggests_wall_pairs_window_and_swing_door_in_a_plan_crop()
        {
            const int width = 400, height = 300;
            var gray = new byte[width * height];
            Array.Fill(gray, (byte)255);
            void Put(int x, int y) => gray[y * width + x] = 0;
            void Horizontal(int y, int start, int end)
            {
                for (int x = start; x <= end; x++) Put(x, y);
            }
            void Pair(int y, int start, int end)
            {
                Horizontal(y, start, end);
                Horizontal(y + 6, start, end);
            }

            Pair(30, 20, 375);
            Pair(260, 20, 375);

            Pair(150, 20, 138);
            Pair(150, 169, 375);
            Horizontal(153, 140, 167);

            Pair(90, 20, 240);
            Pair(90, 274, 375);
            for (int y = 94; y <= 121; y++) Put(240, y);
            for (int x = 242; x <= 272; x++)
                Put(x, 94 + (int)Math.Sqrt(32 * 32 - (x - 240) * (x - 240)));

            var proposals = PlanInkDetector.Detect(gray, width, height);
            Assert.Contains(proposals, p => p.Type == PlanInkDetector.Kind.ExteriorWall && InRow(p, 33, height));
            Assert.Contains(proposals, p => p.Type == PlanInkDetector.Kind.InteriorWall && InRow(p, 153, height));
            Assert.Contains(proposals, p => p.Type == PlanInkDetector.Kind.Window && InRow(p, 153, height)
                && Math.Abs(p.X1 * width - 138) < 1 && Math.Abs(p.X2 * width - 169) < 1);
            Assert.Contains(proposals, p => p.Type == PlanInkDetector.Kind.Door && InRow(p, 93, height)
                && Math.Abs(p.X1 * width - 240) < 1 && Math.Abs(p.X2 * width - 274) < 1);
        }

        [Fact]
        public void Empty_gaps_and_single_dimension_strokes_do_not_create_openings()
        {
            const int width = 300, height = 250;
            var gray = new byte[width * height];
            Array.Fill(gray, (byte)255);
            for (int x = 20; x < 270; x++)
            {
                gray[50 * width + x] = 0;
                if (x is > 119 and < 145) continue;
                gray[110 * width + x] = 0;
                gray[116 * width + x] = 0;
            }

            var proposals = PlanInkDetector.Detect(gray, width, height);
            Assert.NotEmpty(proposals);
            Assert.All(proposals, p =>
            {
                Assert.True(p.Type is PlanInkDetector.Kind.InteriorWall or PlanInkDetector.Kind.ExteriorWall);
                Assert.True(InRow(p, 113, height));
            });
        }

        private static bool InRow(PlanInkDetector.Candidate candidate, int y, int height)
            => Math.Abs(candidate.Y1 * height - y) < 1 && Math.Abs(candidate.Y2 * height - y) < 1;

        [Fact]
        public void Whole_sheet_finds_filled_wall_centerlines_and_ignores_the_page_frame()
        {
            const int width = 400, height = 300;
            var gray = new byte[width * height];
            Array.Fill(gray, (byte)255);
            void Fill(int left, int top, int right, int bottom)
            {
                for (int y = top; y <= bottom; y++)
                    for (int x = left; x <= right; x++)
                        gray[y * width + x] = 0;
            }
            Fill(8, 8, 390, 12);
            Fill(8, 287, 390, 291);
            Fill(8, 8, 12, 291);
            Fill(386, 8, 390, 291);
            Fill(70, 60, 300, 69);
            Fill(70, 205, 300, 214);
            Fill(70, 60, 79, 214);
            Fill(291, 60, 300, 214);

            var proposals = PlanInkDetector.Detect(gray, width, height, wholePage: true);
            Assert.Equal(4, proposals.Count);
            Assert.All(proposals, c => Assert.Equal(PlanInkDetector.Kind.ExteriorWall, c.Type));
            Assert.Contains(proposals, c => InRow(c, 64, height) && Math.Abs(c.Width * height - 10) < 0.1);

            var outline = PlanInkDetector.FindFloorFootprint(proposals);
            Assert.Equal(4, outline.Count);
            Assert.Equal(74.0 / width, outline[0].X);
            Assert.Equal(64.0 / height, outline[0].Y);
            Assert.Equal(295.0 / width, outline[2].X);
            Assert.Equal(209.0 / height, outline[2].Y);
        }

        [Fact]
        public void Floor_outline_requires_actual_edge_coverage_or_an_identified_opening()
        {
            const PlanInkDetector.Kind wall = PlanInkDetector.Kind.ExteriorWall;
            var topLeft = new PlanInkDetector.Candidate(wall, 0.2, 0.2, 0.48, 0.2, 0.02);
            var topRight = new PlanInkDetector.Candidate(wall, 0.55, 0.2, 0.8, 0.2, 0.02);
            var left = new PlanInkDetector.Candidate(wall, 0.2, 0.2, 0.2, 0.75, 0.02);
            var right = new PlanInkDetector.Candidate(wall, 0.8, 0.2, 0.8, 0.75, 0.02);
            var bottom = new PlanInkDetector.Candidate(wall, 0.2, 0.75, 0.8, 0.75, 0.02);
            var door = new PlanInkDetector.Candidate(PlanInkDetector.Kind.Door, 0.48, 0.2, 0.55, 0.2, 0);

            Assert.Empty(PlanInkDetector.FindFloorFootprint(new[] { topLeft, topRight, left, right, bottom }));
            Assert.Equal(4, PlanInkDetector.FindFloorFootprint(new[] { topLeft, topRight, left, right, bottom, door }).Count);
            Assert.Empty(PlanInkDetector.FindFloorFootprint(new[] { topLeft, topRight, left, right with { Y2 = 0.51 }, bottom, door }));
        }
    }
}
