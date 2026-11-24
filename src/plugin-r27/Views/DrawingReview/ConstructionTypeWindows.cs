using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace RvtMcp.Plugin.Views.DrawingReview
{
    internal sealed class ConstructionTypeWindow : DrawingInputWindow
    {
        public ConstructionTypeWindow(DrawingMark mark) : base("Review construction type")
        {
            var existing = mark.Construction;
            var category = new ComboBox { ItemsSource = Enum.GetValues<ConstructionKind>(), SelectedItem = existing?.Category ?? ConstructionKind.Other };
            var code = new TextBox { Text = existing?.Code ?? "", MaxLength = 40 };
            var name = new TextBox { Text = existing?.Title ?? mark.Label ?? mark.SavedAnnotation ?? "", MaxLength = 180 };
            var total = new TextBox { Text = existing?.TotalThicknessMm?.ToString(CultureInfo.CurrentCulture) ?? "", MaxLength = 20 };
            var layers = new TextBox
            {
                Text = string.Join(Environment.NewLine, (existing?.Layers ?? Array.Empty<ConstructionLayer>())
                    .Select(l => $"{l.ThicknessMm.ToString(CultureInfo.CurrentCulture)} | {l.Material}")),
                AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 96,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxLength = 1800
            };
            AddField("Construction category", category);
            AddField("Type code from the drawing (optional)", code);
            AddField("Type name", name);
            AddField("Total thickness in mm, if known", total);
            AddField("Layers in drawing order. Enter one per line as thickness in mm | material.", layers);
            if (existing?.Evidence.Length > 0)
            {
                AddField("Source text from this PDF page", new TextBox
                {
                    Text = string.Join(Environment.NewLine, existing.Evidence), IsReadOnly = true,
                    Height = 72, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto
                });
            }
            AddButtons("Confirm", () =>
            {
                if (string.IsNullOrWhiteSpace(name.Text)) return "Enter a type name.";
                double? thickness = null;
                if (!string.IsNullOrWhiteSpace(total.Text))
                {
                    if (!TryThickness(total.Text, out var mm)) return "Enter a valid total thickness in mm.";
                    thickness = mm;
                }
                var specified = new List<ConstructionLayer>();
                foreach (var line in layers.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    string[] cells = line.Split('|', 2, StringSplitOptions.TrimEntries);
                    if (cells.Length != 2 || string.IsNullOrWhiteSpace(cells[1]) || cells[1].Length > 100 || !TryThickness(cells[0], out var mm))
                        return "Use one layer per line as thickness in mm | material.";
                    specified.Add(new ConstructionLayer(cells[1], mm));
                }
                if (specified.Count > 15) return "Enter at most 15 layers.";
                Mark = mark with
                {
                    Label = null, SavedAnnotation = null, Suggested = false,
                    Construction = new ConstructionSpec((ConstructionKind)category.SelectedItem, NullIfEmpty(code.Text), name.Text.Trim(),
                        thickness, specified.ToArray(), existing?.Evidence ?? Array.Empty<string>())
                };
                return null;
            });
        }

        public DrawingMark Mark { get; private set; }

        private static string NullIfEmpty(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        private static bool TryThickness(string text, out double mm) =>
            DrawingNoteWindow.TryNumber(text, out mm) && mm is >= 0.1 and <= 10_000;
    }

    internal sealed class AssignConstructionTypeWindow : DrawingInputWindow
    {
        private sealed record Option(string Caption, ConstructionChoice Choice);

        public AssignConstructionTypeWindow(IEnumerable<ConstructionChoice> choices) : base("Assign a construction type")
        {
            var options = choices.Select(choice => new Option(choice.Caption, choice))
                .Prepend(new Option("Clear type assignment", null)).ToArray();
            var selection = new ComboBox { ItemsSource = options, DisplayMemberPath = nameof(Option.Caption), SelectedIndex = -1 };
            AddField("Choose a reviewed construction", selection);
            AddButtons("Assign", () =>
            {
                if (selection.SelectedItem is not Option option) return "Choose a construction type.";
                Selected = option.Choice;
                return null;
            });
        }

        public ConstructionChoice Selected { get; private set; }
    }
}
