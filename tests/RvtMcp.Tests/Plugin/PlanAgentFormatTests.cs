using System.IO;
using RvtMcp.Plugin.Views.DrawingReview;
using Xunit;

namespace RvtMcp.Tests
{
    public sealed class PlanAgentFormatTests
    {
        private const string Proposal = """
            {"formatVersion":1,"viewKey":"plan-25","imageSha256":"example",
             "region":[0.1,0.1,0.9,0.8],
             "segments":[{"kind":"Window","start":[0.2,0.3],"end":[0.3,0.3],"evidence":"Visible frame between jambs"}]}
            """;

        private const string Section = """
            {"formatVersion":2,"viewKey":"section-28","imageSha256":"example","drawingRole":"Section","region":[0.1,0.1,0.9,0.8],
             "marks":[{"kind":"Height","anchor":[0.2,0.3],"label":"0.00 is the printed drawing datum","evidence":"Datum next to the floor"},
                      {"kind":"Construction","anchor":[0.7,0.6],"label":"Outer wall","evidence":"Layers listed beside the section",
                       "construction":{"category":"ExteriorWall","code":"EW01","title":"Brick cavity wall","totalThicknessMm":null,
                        "layers":[{"material":"Brick","thicknessMm":110},{"material":"Mineral wool","thicknessMm":150}]}}]}
            """;

        [Fact]
        public void Reads_window_span_for_the_matching_reference()
        {
            var segment = Assert.Single(PlanAgentFormat.Read(Proposal, "plan-25", "example"));
            Assert.Equal("Window", segment.Kind);
            Assert.Equal(0.2, segment.X1);
            Assert.Equal(0.3, segment.X2);
            Assert.Equal("Visible frame between jambs", segment.Evidence);
        }

        [Fact]
        public void Rejects_other_views_and_changed_images()
        {
            Assert.Throws<InvalidDataException>(() => PlanAgentFormat.Read(Proposal, "plan-26", "example"));
            Assert.Throws<InvalidDataException>(() => PlanAgentFormat.Read(Proposal, "plan-25", "changed"));
        }

        [Theory]
        [InlineData("[0.3,0.3]", "[0.3,0.92]")]
        [InlineData("[0.3,0.3]", "[0.2,0.3]")]
        [InlineData("[0.3,0.3]", "[1.3,0.3]")]
        [InlineData("\"Window\"", "\"Column\"")]
        public void Rejects_outside_empty_or_unsupported_geometry(string original, string invalid)
        {
            Assert.Throws<InvalidDataException>(() => PlanAgentFormat.Read(Proposal.Replace(original, invalid), "plan-25", "example"));
        }

        [Fact]
        public void Reads_drawing_datums_and_ordered_assembly_layers_as_reference_notes()
        {
            var notes = PlanAgentFormat.ReadReference(Section, "section-28", "example", "Section");
            Assert.Collection(notes,
                note =>
                {
                    Assert.Equal("Height", note.Kind);
                    Assert.Contains("printed drawing datum", note.Label);
                    Assert.Null(note.Construction);
                },
                note =>
                {
                    Assert.Equal(ConstructionKind.ExteriorWall, note.Construction.Category);
                    Assert.Null(note.Construction.TotalThicknessMm);
                    Assert.Collection(note.Construction.Layers,
                        layer => Assert.Equal(new ConstructionLayer("Brick", 110), layer),
                        layer => Assert.Equal(new ConstructionLayer("Mineral wool", 150), layer));
                });
        }

        [Fact]
        public void Rejects_wrong_role_unconfirmed_project_levels_and_invalid_thickness()
        {
            Assert.Throws<InvalidDataException>(() => PlanAgentFormat.ReadReference(Section, "section-28", "example", "Elevation"));
            var invalidLevel = Section.Replace("\"Height\"", "\"Level\"");
            Assert.Throws<InvalidDataException>(() => PlanAgentFormat.ReadReference(invalidLevel, "section-28", "example", "Section"));
            var invalidLayer = Section.Replace("\"thicknessMm\":110", "\"thicknessMm\":-110");
            Assert.Throws<InvalidDataException>(() => PlanAgentFormat.ReadReference(invalidLayer, "section-28", "example", "Section"));
        }
    }
}
