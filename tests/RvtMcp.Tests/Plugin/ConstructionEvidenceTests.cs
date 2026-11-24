using System;
using System.Collections.Generic;
using System.Linq;
using RvtMcp.Plugin.Views.DrawingReview;
using Xunit;

namespace RvtMcp.Tests
{
    public sealed class ConstructionEvidenceTests
    {
        [Fact]
        public void Reads_ordered_layers_and_totals_without_combining_neighboring_columns()
        {
            var words = new List<ConstructionEvidence.Word>();
            AddLine(words, 25, 12, "YV-01 Ydervæg");
            AddLine(words, 27, 24, "108 mm tegl");
            AddLine(words, 27, 36, "Mineraluld 19,5 cm");
            AddLine(words, 27, 48, "Samlet tykkelse: 350 mm");
            AddLine(words, 230, 12, "RF02 Roof assembly");
            AddLine(words, 230, 24, "1.5 mm membrane");
            AddLine(words, 230, 36, "250 mm insulation");
            AddLine(words, 25, 75, "Terrændæk: 120 mm beton + 300 mm EPS");
            AddLine(words, 25, 105, "Fundament");
            AddLine(words, 25, 116, "300 x 600 mm beton");

            var found = ConstructionEvidence.Find(words).Select(c => c.Spec).ToArray();

            Assert.Equal(4, found.Length);
            var wall = Assert.Single(found, s => s.Category == ConstructionKind.ExteriorWall);
            Assert.Equal("YV01", wall.Code);
            Assert.Equal(new[] { new ConstructionLayer("tegl", 108), new ConstructionLayer("Mineraluld", 195) }, wall.Layers);
            Assert.Equal(350, wall.TotalThicknessMm);
            Assert.Equal(4, wall.Evidence.Length);
            var roof = Assert.Single(found, s => s.Category == ConstructionKind.Roof);
            Assert.Equal(new[] { 1.5, 250 }, roof.Layers.Select(layer => layer.ThicknessMm));
            Assert.Null(roof.TotalThicknessMm);
            var ground = Assert.Single(found, s => s.Category == ConstructionKind.SlabOnGrade);
            Assert.Equal(new[] { 120.0, 300 }, ground.Layers.Select(layer => layer.ThicknessMm));
            Assert.Empty(Assert.Single(found, s => s.Category == ConstructionKind.Foundation).Layers);
        }

        [Fact]
        public void Compact_type_tags_are_evidence_but_detail_references_are_not_types()
        {
            var words = new List<ConstructionEvidence.Word>();
            AddLine(words, 100, 10, "IV-02");
            AddLine(words, 20, 28, "Se detalje YV01");
            AddLine(words, 20, 40, "Bjælke B1");
            AddLine(words, 20, 52, "Søjle S3");

            var found = ConstructionEvidence.Find(words).Select(c => c.Spec).ToArray();
            Assert.Equal(3, found.Length);
            var wall = Assert.Single(found, s => s.Category == ConstructionKind.InteriorWall);
            Assert.Equal("IV02", wall.Code);
            Assert.Null(wall.TotalThicknessMm);
            Assert.Contains(found, s => s.Category == ConstructionKind.Beam);
            Assert.Contains(found, s => s.Category == ConstructionKind.Column);
        }

        [Fact]
        public void Wall_matching_requires_one_compatible_type_and_rejects_conflicting_evidence()
        {
            static ConstructionChoice Type(ConstructionKind kind, string code, double? width, string drawing)
                => new(new ConstructionSpec(kind, code, code, width, Array.Empty<ConstructionLayer>(), Array.Empty<string>()),
                    new ConstructionSource(drawing, drawing, 1));
            var exterior = Type(ConstructionKind.ExteriorWall, "YV01", 350, "section-1");
            var duplicate = Type(ConstructionKind.ExteriorWall, "YV01", 350, "section-2");
            var inside = Type(ConstructionKind.InteriorWall, "IV01", 351, "section-3");
            var uncertain = Type(ConstructionKind.ExteriorWall, "YV02", null, "plan");
            var conflict = Type(ConstructionKind.ExteriorWall, "YV03", 360, "detail");
            var catalog = new[] { exterior, duplicate, inside, uncertain };

            Assert.Same(exterior, ConstructionTypes.Match(catalog, true, null, 353));
            Assert.Same(inside, ConstructionTypes.Match(catalog, false, null, 355));
            Assert.Same(uncertain, ConstructionTypes.Match(catalog, true, "yv02", null));
            Assert.Null(ConstructionTypes.Match(catalog, true, "YV99", 350));
            Assert.Null(ConstructionTypes.Match(catalog.Append(conflict), true, null, 353));
            Assert.Null(ConstructionTypes.Match(catalog, true, null, 280));
        }

        private static void AddLine(List<ConstructionEvidence.Word> words, int left, int top, string text)
        {
            foreach (string token in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                int right = left + token.Length * 5;
                words.Add(new ConstructionEvidence.Word(token, left, top, right, top + 8));
                left = right + 3;
            }
        }
    }
}
