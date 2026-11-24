using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace RvtMcp.Plugin.Views.DrawingReview
{
    internal sealed record ConstructionSource(string ViewKey, string ViewName, int Page);
    internal sealed record ConstructionChoice(ConstructionSpec Spec, ConstructionSource Source)
    {
        public string Caption => $"{Spec.Caption} ({Source.ViewName})";
    }

    internal static class ConstructionTypes
    {
        public static bool MatchesWall(ConstructionKind category, bool exterior) =>
            category == ConstructionKind.Wall || (exterior ? category == ConstructionKind.ExteriorWall
                : category == ConstructionKind.InteriorWall);

        public static ConstructionChoice Match(IEnumerable<ConstructionChoice> choices, bool exterior, string code, double? measuredMm)
        {
            var compatible = Distinct(choices.Where(choice => MatchesWall(choice.Spec.Category, exterior))).ToArray();
            bool hasCode = !string.IsNullOrWhiteSpace(code);
            if (hasCode)
            {
                var tagged = compatible.Where(choice => string.Equals(CodeKey(choice.Spec.Code), CodeKey(code),
                    StringComparison.OrdinalIgnoreCase)).ToArray();
                var specified = tagged.Where(choice => choice.Spec.TotalThicknessMm.HasValue || choice.Spec.Layers.Length > 0).ToArray();
                if (specified.Length > 0) tagged = specified;
                return tagged.Length == 1 ? tagged[0] : null;
            }

            if (measuredMm is not > 0 || !double.IsFinite(measuredMm.Value)) return null;
            var tolerance = Math.Clamp(measuredMm.Value * 0.07, 12, 30);
            var fitting = compatible.Where(c => c.Spec.TotalThicknessMm.HasValue
                && Math.Abs(c.Spec.TotalThicknessMm.Value - measuredMm.Value) <= tolerance).Take(2).ToArray();
            return fitting.Length == 1 ? fitting[0] : null;
        }

        public static bool SameSpecification(ConstructionSpec left, ConstructionSpec right) =>
            left != null && right != null && StringComparer.OrdinalIgnoreCase.Equals(Identity(left), Identity(right));

        public static IEnumerable<ConstructionChoice> Distinct(IEnumerable<ConstructionChoice> choices) =>
            choices.GroupBy(c => Identity(c.Spec), StringComparer.OrdinalIgnoreCase).Select(g => g.First());

        private static string CodeKey(string code) => code?.Trim().Replace("-", "").Replace(" ", "");

        private static string Identity(ConstructionSpec spec)
        {
            static string Number(double? value) => value?.ToString("G", CultureInfo.InvariantCulture) ?? "";
            return $"{spec.Category}|{CodeKey(spec.Code) ?? spec.Title}|{Number(spec.TotalThicknessMm)}|"
                + string.Join(";", spec.Layers.Select(l => $"{l.Material.Trim()}:{Number(l.ThicknessMm)}"));
        }
    }
}
