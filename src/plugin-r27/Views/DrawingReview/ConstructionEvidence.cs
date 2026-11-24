using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace RvtMcp.Plugin.Views.DrawingReview
{
    internal enum ConstructionKind { ExteriorWall, InteriorWall, Wall, Floor, SlabOnGrade, Roof, Foundation, Beam, Column, Other }

    internal sealed record ConstructionLayer(string Material, double ThicknessMm);
    internal sealed record ConstructionSpec(ConstructionKind Category, string Code, string Title,
        double? TotalThicknessMm, ConstructionLayer[] Layers, string[] Evidence)
    {
        public string Caption => string.IsNullOrWhiteSpace(Code)
            || Title.Replace("-", "").Replace(" ", "").Equals(Code, StringComparison.OrdinalIgnoreCase)
            ? Title : $"{Code} · {Title}";
    }

    internal static class ConstructionEvidence
    {
        internal sealed record Word(string Text, double Left, double Top, double Right, double Bottom);
        private sealed record Line(string Text, double Left, double Top, double Right, double Bottom);
        internal sealed record Candidate(ConstructionSpec Spec, double X, double Y);

        private const RegexOptions TextOptions = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        private static readonly (ConstructionKind Kind, Regex Heading)[] Headings =
        {
            (ConstructionKind.ExteriorWall, Heading(@"ydervæg(?:ge)?|facadevæg(?:ge)?|(?:exterior|external) walls?")),
            (ConstructionKind.InteriorWall, Heading(@"indervæg(?:ge)?|skillevæg(?:ge)?|(?:interior|internal|partition) walls?")),
            (ConstructionKind.Wall, Heading(@"væg(?:opbygning)?|wall (?:type|assembly|build[- ]?up)")),
            (ConstructionKind.SlabOnGrade, Heading(@"terrændæk(?:ket)?|slab[- ]on[- ]grade|ground[- ]bearing slab")),
            (ConstructionKind.Floor, Heading(@"etagedæk(?:ket)?|gulv(?:opbygning)?|(?:floor|concrete) slab|floor (?:assembly|build[- ]?up)")),
            (ConstructionKind.Roof, Heading(@"tag(?:opbygning|konstruktion)?|roof(?: (?:assembly|build[- ]?up))?")),
            (ConstructionKind.Foundation, Heading(@"fundament(?:er)?|sokkel|foundation(?:s)?|footing(?:s)?")),
            (ConstructionKind.Beam, Heading(@"bjælke(?:r)?|beam(?:s)?")),
            (ConstructionKind.Column, Heading(@"søjle(?:r)?|column(?:s)?"))
        };
        private static readonly Regex Code = new(@"(?<![\p{L}\d])(?<prefix>YV|IV|EW|IW|TD|ED|RF|FD)[- ]?(?<number>\d{1,3}[A-Z]?)(?![\p{L}\d])", TextOptions);
        private static readonly Regex Total = new(@"(?:samlet\s+tykkelse|total\s+tykkelse|total\s+thickness|overall\s+thickness)\s*[:=]?\s*(?<value>\d+(?:[.,]\d+)?)\s*(?<unit>mm|cm|m)\b", TextOptions);
        private static readonly Regex Layer = new(@"^\s*[-•]?\s*(?<value>\d+(?:[.,]\d+)?)\s*(?<unit>mm|cm|m)\s+(?<material>[^+;]{2,100})$|^\s*(?<material>[^+;]{2,100}?)[\s:]+(?<value>\d+(?:[.,]\d+)?)\s*(?<unit>mm|cm|m)\s*$", TextOptions);
        private static readonly Regex Material = new(@"\b(?:beton\w*|concrete|tegl\w*|mursten\w*|brick\w*|porebeton\w*|letbeton\w*|aircrete|gips\w*|gypsum|plasterboard|isolering\w*|insulation|mineraluld\w*|mineral wool|træ\w*|timber|wood|stål\w*|steel|krydsfiner\w*|plywood|membran\w*|membrane|tagpap|roofing felt|afretning\w*|screed|EPS|XPS|OSB|singels|grus|gravel)\b", TextOptions);
        private static readonly Regex MultipleDimensions = new(@"\d\s*[x×]\s*\d", TextOptions);
        private static readonly Regex OtherAnnotation = new(@"\b(?:detalje|detail|snit|section|rev|revision|henvisning|reference)\b", TextOptions);

        private static Regex Heading(string words) => new($@"^\s*(?:type\s+)?(?:{words})(?=$|[\s:–—()/-])", TextOptions);

        public static IEnumerable<Candidate> Find(IEnumerable<Word> words)
        {
            var lines = ReadLines(words);
            var used = new HashSet<int>();
            for (int i = 0; i < lines.Count; i++)
            {
                if (used.Contains(i)) continue;
                var header = lines[i];
                var (kind, code) = Classify(header.Text);
                if (!kind.HasValue) continue;

                var evidence = new List<string> { header.Text };
                var layers = ReadLayers(header.Text.Contains(':') ? header.Text[(header.Text.IndexOf(':') + 1)..] : "").ToList();
                double? total = ReadTotal(header.Text);
                var previous = header;
                // Keep competing columns separate and stop at the next aligned note that is not a layer.
                foreach (int index in Enumerable.Range(i + 1, lines.Count - i - 1).Take(30))
                {
                    if (evidence.Count >= 10) break;
                    var line = lines[index];
                    double distance = line.Top - previous.Top;
                    if (distance > Math.Max(20, (previous.Bottom - previous.Top) * 2.6)) break;
                    if (distance < 2 || used.Contains(index) || Math.Abs(line.Left - header.Left) > 17) continue;
                    if (Classify(line.Text).Kind.HasValue) break;
                    var rowLayers = ReadLayers(line.Text).ToArray();
                    var rowTotal = ReadTotal(line.Text);
                    if (rowLayers.Length == 0 && !rowTotal.HasValue) break;
                    evidence.Add(line.Text);
                    layers.AddRange(rowLayers);
                    total ??= rowTotal;
                    previous = line;
                    used.Add(index);
                }

                yield return new Candidate(new ConstructionSpec(kind.Value, code, header.Text, total, layers.ToArray(), evidence.ToArray()),
                    (header.Left + header.Right) / 2, (header.Top + header.Bottom) / 2);
            }
        }

        private static (ConstructionKind? Kind, string Code) Classify(string text)
        {
            if (text.Length > 180 || OtherAnnotation.IsMatch(text)) return (null, null);
            var code = Code.Match(text);
            var content = code.Success && Regex.IsMatch(text[..code.Index], @"^\s*(?:type\s*)?$", TextOptions)
                ? text[(code.Index + code.Length)..].TrimStart(' ', ':', '-', '–', '—') : text;
            string tag = code.Success ? code.Groups["prefix"].Value.ToUpperInvariant() + code.Groups["number"].Value.ToUpperInvariant() : null;
            foreach (var (category, heading) in Headings)
                if (heading.IsMatch(content)) return (category, tag);
            // A compact tag is evidence even when the plan carries no assembly note.
            if (!code.Success || content.Length > 0) return (null, null);
            return (code.Groups["prefix"].Value.ToUpperInvariant() switch
            {
                "YV" or "EW" => ConstructionKind.ExteriorWall,
                "IV" or "IW" => ConstructionKind.InteriorWall,
                "TD" => ConstructionKind.SlabOnGrade,
                "ED" => ConstructionKind.Floor,
                "RF" => ConstructionKind.Roof,
                "FD" => ConstructionKind.Foundation,
                _ => ConstructionKind.Other
            }, tag);
        }

        private static IEnumerable<ConstructionLayer> ReadLayers(string line)
        {
            foreach (var part in line.Split('+', ';'))
            {
                if (MultipleDimensions.IsMatch(part)) continue;
                var layer = Layer.Match(part);
                if (!layer.Success || !Material.IsMatch(layer.Groups["material"].Value)) continue;
                var thickness = ReadThickness(layer);
                if (thickness.HasValue)
                    yield return new ConstructionLayer(layer.Groups["material"].Value.Trim(), thickness.Value);
            }
        }

        private static double? ReadTotal(string line) => ReadThickness(Total.Match(line));

        private static double? ReadThickness(Match match)
        {
            if (!match.Success || !double.TryParse(match.Groups["value"].Value.Replace(',', '.'),
                NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double value)) return null;
            value *= match.Groups["unit"].Value.ToLowerInvariant() switch { "m" => 1000, "cm" => 10, _ => 1 };
            return value is >= 0.1 and <= 10_000 ? value : null;
        }

        private static IReadOnlyList<Line> ReadLines(IEnumerable<Word> words)
        {
            var rows = new List<List<Word>>();
            foreach (var word in words.OrderBy(w => (w.Top + w.Bottom) / 2))
            {
                var row = rows.LastOrDefault();
                var anchor = row?.FirstOrDefault();
                if (anchor == null || Math.Abs((anchor.Top + anchor.Bottom - word.Top - word.Bottom) / 2)
                    > Math.Max(1.5, (anchor.Bottom - anchor.Top) * 0.35))
                {
                    row = new List<Word>();
                    rows.Add(row);
                }
                row.Add(word);
            }

            var result = new List<Line>();
            foreach (var row in rows)
            {
                var segment = new List<Word>();
                foreach (var word in row.OrderBy(w => w.Left))
                {
                    if (segment.Count > 0 && word.Left - segment[^1].Right > Math.Max(15, (word.Bottom - word.Top) * 2.6))
                    {
                        result.Add(Join(segment));
                        segment.Clear();
                    }
                    segment.Add(word);
                }
                if (segment.Count > 0) result.Add(Join(segment));
            }
            return result.OrderBy(line => line.Top).ThenBy(line => line.Left).ToArray();
        }

        private static Line Join(List<Word> words) => new(string.Join(" ", words.Select(w => w.Text)),
            words[0].Left, words.Min(w => w.Top), words.Max(w => w.Right), words.Max(w => w.Bottom));
    }
}
