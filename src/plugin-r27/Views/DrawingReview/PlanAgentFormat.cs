using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Views.DrawingReview
{
    internal static class PlanAgentFormat
    {
        internal sealed record Segment(string Kind, double X1, double Y1, double X2, double Y2, string Evidence);
        private static readonly HashSet<string> Kinds = new(StringComparer.Ordinal)
            { "ExteriorWall", "InteriorWall", "Door", "Window" };

        internal sealed record ReferenceNote(string Kind, double X, double Y, string Label, string Evidence, ConstructionSpec Construction);

        private static InvalidDataException Error(string message) => new(message);

        private static double Number(JToken token, double minimum, double maximum, string message)
        {
            if (token?.Type is not (JTokenType.Float or JTokenType.Integer))
                throw Error(message);
            double value = token.Value<double>();
            if (!double.IsFinite(value) || value < minimum || value > maximum)
                throw Error(message);
            return value;
        }

        private static double Coordinate(JToken token) => Number(token, 0, 1, "Each coordinate must be a number from 0 to 1.");

        private static string Text(JToken token) => token?.Type == JTokenType.String ? token.Value<string>() : null;

        private static string RequiredText(JToken token, int limit, string message)
        {
            var value = Text(token);
            if (string.IsNullOrWhiteSpace(value) || value.Length > limit || value.Any(char.IsControl))
                throw Error(message);
            return value;
        }

        private static (JObject Root, double[] Bounds) ReadPage(string json, string viewKey, string imageSha256, int version)
        {
            var root = JObject.Parse(json);
            if (root["formatVersion"]?.Type != JTokenType.Integer || root.Value<int>("formatVersion") != version)
                throw Error("Unsupported drawing agent proposal format.");
            if (!string.Equals(Text(root["viewKey"]), viewKey, StringComparison.Ordinal)
                || !string.Equals(Text(root["imageSha256"]), imageSha256, StringComparison.OrdinalIgnoreCase))
                throw Error("This proposal belongs to a different drawing or image. Open the original review page.");
            if (root["region"] is not JArray region || region.Count != 4)
                throw Error("The proposal needs a valid drawing region.");
            var bounds = region.Select(Coordinate).ToArray();
            if (bounds[2] <= bounds[0] || bounds[3] <= bounds[1])
                throw Error("The drawing region has no area.");
            return (root, bounds);
        }

        private static bool Inside(double x, double y, double[] bounds) => x >= bounds[0] - 1e-6 && x <= bounds[2] + 1e-6
            && y >= bounds[1] - 1e-6 && y <= bounds[3] + 1e-6;

        internal static IReadOnlyList<Segment> Read(string json, string viewKey, string imageSha256)
        {
            var (root, bounds) = ReadPage(json, viewKey, imageSha256, 1);
            if (root["segments"] is not JArray segments || segments.Count is < 1 or > 300)
                throw Error("The proposal needs between 1 and 300 segments.");

            var result = new List<Segment>();
            foreach (var token in segments)
            {
                if (token is not JObject mark || !Kinds.Contains(Text(mark["kind"])))
                    throw Error("Only wall, door and window segments are supported.");
                if (mark["start"] is not JArray start || start.Count != 2 || mark["end"] is not JArray end || end.Count != 2)
                    throw Error("Every proposal needs two endpoints.");
                var segment = new Segment(Text(mark["kind"]), Coordinate(start[0]), Coordinate(start[1]),
                    Coordinate(end[0]), Coordinate(end[1]), Text(mark["evidence"]));
                if (segment.Evidence is not { Length: > 0 and <= 180 } || string.IsNullOrWhiteSpace(segment.Evidence)
                    || segment.Evidence.Any(char.IsControl))
                    throw Error("Every proposal needs a short visual evidence note.");
                if (!Inside(segment.X1, segment.Y1, bounds) || !Inside(segment.X2, segment.Y2, bounds))
                    throw Error("A proposal lies outside the plan region.");
                var dx = segment.X2 - segment.X1;
                var dy = segment.Y2 - segment.Y1;
                if (dx * dx + dy * dy < 0.000004)
                    throw Error("A proposal is too short to review.");
                result.Add(segment);
            }
            return result;
        }

        internal static IReadOnlyList<ReferenceNote> ReadReference(string json, string viewKey, string imageSha256, string role)
        {
            var (root, bounds) = ReadPage(json, viewKey, imageSha256, 2);
            if (role is not ("Elevation" or "Section") || Text(root["drawingRole"]) != role)
                throw Error("The proposal was generated for a different drawing type.");
            if (root["marks"] is not JArray marks || marks.Count is < 1 or > 200)
                throw Error("The proposal needs between 1 and 200 reference marks.");
            var result = new List<ReferenceNote>();
            foreach (var token in marks)
            {
                if (token is not JObject mark || Text(mark["kind"]) is not ("Height" or "Door" or "Window" or "Construction"))
                    throw Error("Unrecognized reference mark.");
                if (mark["anchor"] is not JArray anchor || anchor.Count != 2)
                    throw Error("Every reference mark needs a drawing location.");
                double x = Coordinate(anchor[0]), y = Coordinate(anchor[1]);
                if (!Inside(x, y, bounds))
                    throw Error("A note lies outside the drawing region.");
                string kind = Text(mark["kind"]);
                string label = RequiredText(mark["label"], 220, "Every reference mark needs a short label.");
                string evidence = RequiredText(mark["evidence"], 180, "Every reference mark needs a short visual evidence note.");
                ConstructionSpec construction = kind == "Construction" ? ReadConstruction(mark["construction"], evidence) : null;
                result.Add(new ReferenceNote(kind, x, y, label, evidence, construction));
            }
            return result;
        }

        private static ConstructionSpec ReadConstruction(JToken token, string evidence)
        {
            const string invalidThickness = "A documented thickness must be between 0.1 and 10,000 mm.";
            if (token is not JObject data || !Enum.TryParse<ConstructionKind>(Text(data["category"]), out var category)
                || category.ToString() != Text(data["category"]))
                throw Error("A construction proposal needs a supported category.");
            string title = RequiredText(data["title"], 180, "A construction proposal needs a title.");
            string code = Text(data["code"]);
            if (code?.Length > 40 || code?.Any(char.IsControl) == true)
                throw Error("The construction code is invalid.");
            double? total = data["totalThicknessMm"] is { Type: not JTokenType.Null } stated
                ? Number(stated, 0.1, 10000, invalidThickness) : null;
            if (data["layers"] is not JArray layers || layers.Count > 15)
                throw Error("A construction proposal supports at most 15 layers.");
            var parsedLayers = layers.Select(layer =>
            {
                if (layer is not JObject row) throw Error("A construction layer is invalid.");
                return new ConstructionLayer(RequiredText(row["material"], 100, "A layer needs a material."),
                    Number(row["thicknessMm"], 0.1, 10000, invalidThickness));
            }).ToArray();
            return new ConstructionSpec(category, string.IsNullOrWhiteSpace(code) ? null : code,
                title, total, parsedLayers, new[] { evidence });
        }
    }
}
