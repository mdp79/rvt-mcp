using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using NamingUtils = Autodesk.Revit.DB.NamingUtils;

namespace RvtMcp.Plugin.Views.DrawingReview
{
    internal enum DrawingRole { Plan, Elevation, Section }

    internal enum MarkKind { ExteriorWall, InteriorWall, Level, Height, Door, Window, Construction, FloorFootprint }

    internal sealed record DrawingMark(MarkKind Kind, Point Start, Point? End = null, string Label = null,
        double? ElevationMm = null, string SavedAnnotation = null, bool Suggested = false,
        ConstructionSpec Construction = null, ConstructionChoice AssignedType = null, double? PageWallThickness = null,
        bool AutomaticTypeMatch = false, Point[] Boundary = null, string AgentEvidence = null)
    {
        public bool IsWall => Kind is MarkKind.ExteriorWall or MarkKind.InteriorWall;
        public string Annotation => Construction?.Caption ?? SavedAnnotation ?? (ElevationMm.HasValue
            ? $"{Label} | {ElevationMm.Value.ToString("G", CultureInfo.CurrentCulture)} mm (project elevation)"
            : string.IsNullOrEmpty(Label) ? Kind == MarkKind.FloorFootprint ? "Floor footprint (wall centerlines)" : Kind.ToString() : $"{Kind}: {Label}");
        public string ListLabel => (Suggested ? AgentEvidence == null ? "Suggested · " : "Agent · " : "") + Annotation
            + (AssignedType == null ? "" : $" · {AssignedType.Spec.Caption}");
    }

    internal abstract class DrawingReviewWindow : Window
    {
        protected DrawingReviewWindow()
        {
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
        }

        public void SetOwner(IntPtr handle)
        {
            if (handle != IntPtr.Zero)
                new WindowInteropHelper(this).Owner = handle;
        }
    }

    internal abstract class DrawingInputWindow : DrawingReviewWindow
    {
        protected DrawingInputWindow(string title)
        {
            Title = title;
            Width = 450;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            Form = new StackPanel { Margin = new Thickness(16) };
            Content = Form;
        }

        protected StackPanel Form { get; }

        protected void AddField(string title, Control control)
        {
            Form.Children.Add(new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap });
            control.Margin = new Thickness(0, 3, 0, 12);
            Form.Children.Add(control);
        }

        protected void AddButtons(string caption, Func<string> validate)
        {
            var warning = new TextBlock { TextWrapping = TextWrapping.Wrap };
            Form.Children.Add(warning);
            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };
            var confirm = new Button { Content = caption, IsDefault = true, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0) };
            confirm.Click += (_, __) =>
            {
                var error = validate();
                if (error == null) DialogResult = true;
                else warning.Text = error;
            };
            buttons.Children.Add(confirm);
            buttons.Children.Add(new Button { Content = "Cancel", IsCancel = true, MinWidth = 90 });
            Form.Children.Add(buttons);
        }
    }

    internal sealed class DrawingNoteWindow : DrawingInputWindow
    {
        public DrawingNoteWindow(MarkKind[] allowed, DrawingMark mark) : base("Edit drawing mark")
        {
            var kind = new ComboBox { ItemsSource = allowed, SelectedItem = mark.Kind };
            var label = new TextBox { MaxLength = 250, Text = mark.Label ?? mark.SavedAnnotation ?? "" };
            var elevation = new TextBox { MaxLength = 24, Text = mark.ElevationMm?.ToString(CultureInfo.CurrentCulture) ?? "" };
            AddField("Element or reference type", kind);
            AddField("Identification, dimensions, materials or notes from the drawing", label);
            AddField("For project levels, enter elevation in mm relative to the Revit project origin.", elevation);
            if (mark.AgentEvidence != null)
                AddField("Visual evidence supplied by the agent", new TextBox
                {
                    Text = mark.AgentEvidence, IsReadOnly = true, TextWrapping = TextWrapping.Wrap
                });
            AddButtons("Confirm", () =>
            {
                var selected = (MarkKind)kind.SelectedItem;
                var text = label.Text.Trim();
                if (selected is MarkKind.ExteriorWall or MarkKind.InteriorWall && !mark.End.HasValue)
                    return "A wall needs two points. Add a wall trace for this location.";
                if (selected is MarkKind.Level or MarkKind.Height or MarkKind.Construction && text.Length == 0)
                    return "Enter an identifying label.";
                double? value = null;
                if (selected == MarkKind.Level)
                {
                    if (!NamingUtils.IsValidName(text))
                        return "Enter a level name without Revit's reserved punctuation.";
                    if (!TryNumber(elevation.Text, out var mm))
                        return "Enter an elevation as a signed number in millimetres.";
                    value = mm;
                }
                Mark = mark with
                {
                    Kind = selected, Label = text, ElevationMm = value, SavedAnnotation = null,
                    Suggested = false, AssignedType = selected == mark.Kind ? mark.AssignedType : null,
                    AutomaticTypeMatch = selected == mark.Kind && mark.AutomaticTypeMatch
                };
                return null;
            });
        }

        public DrawingMark Mark { get; private set; }
        internal static bool TryNumber(string text, out double value) =>
            double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) && double.IsFinite(value);
    }

    internal sealed record PlanAlignment(Point First, Point Second, Point ProjectFirst, Point ProjectSecond, string Level)
    {
        public Point ToProject(Point p, double aspect)
        {
            // Work in page-height units and turn page-down into project-up before rotating.
            var dx = (Second.X - First.X) * aspect;
            var dy = First.Y - Second.Y;
            var px = (p.X - First.X) * aspect;
            var py = First.Y - p.Y;
            var square = dx * dx + dy * dy;
            var target = ProjectSecond - ProjectFirst;
            var cosScale = (dx * target.X + dy * target.Y) / square;
            var sinScale = (dx * target.Y - dy * target.X) / square;
            return ProjectFirst + new Vector(cosScale * px - sinScale * py, sinScale * px + cosScale * py);
        }
    }

    internal sealed class PlanAlignmentWindow : DrawingInputWindow
    {
        public PlanAlignmentWindow(Point first, Point second, PlanAlignment previous, string[] existingLevels) : base("Align plan to Revit")
        {
            TextBox Field(string prompt, double? value)
            {
                var box = new TextBox { Text = value?.ToString(CultureInfo.CurrentCulture) ?? "" };
                AddField(prompt, box);
                return box;
            }

            var x1 = Field("First point · project X (mm)", previous?.ProjectFirst.X);
            var y1 = Field("First point · project Y (mm)", previous?.ProjectFirst.Y);
            var x2 = Field("Second point · project X (mm)", previous?.ProjectSecond.X);
            var y2 = Field("Second point · project Y (mm)", previous?.ProjectSecond.Y);
            var level = new ComboBox { ItemsSource = existingLevels, IsEditable = true, Text = previous?.Level ?? "" };
            AddField("Choose a Revit level or enter a proposed level name", level);
            AddButtons("Save alignment", () =>
            {
                if (!DrawingNoteWindow.TryNumber(x1.Text, out var ax) || !DrawingNoteWindow.TryNumber(y1.Text, out var ay)
                    || !DrawingNoteWindow.TryNumber(x2.Text, out var bx) || !DrawingNoteWindow.TryNumber(y2.Text, out var by)
                    || Math.Abs(ax) > 15_000_000 || Math.Abs(ay) > 15_000_000 || Math.Abs(bx) > 15_000_000 || Math.Abs(by) > 15_000_000)
                    return "Enter valid project coordinates in millimetres within 15 km of the origin.";
                if (new Vector(bx - ax, by - ay).Length < 1)
                    return "The two project points must be distinct.";
                var name = level.Text.Trim();
                if (name.Length is 0 or > 80 || !NamingUtils.IsValidName(name))
                    return "Enter a valid Revit level name.";
                Alignment = new PlanAlignment(first, second, new Point(ax, ay), new Point(bx, by), name);
                return null;
            });
        }

        public PlanAlignment Alignment { get; private set; }
    }
}
