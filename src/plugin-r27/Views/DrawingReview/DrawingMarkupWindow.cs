using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace RvtMcp.Plugin.Views.DrawingReview
{
    internal sealed class DrawingMarkupWindow : DrawingReviewWindow
    {
        private enum PickMode { Add, Detect, Align, Move }
        private readonly ObservableCollection<DrawingMark> _marks;
        private readonly ListBox _list = new() { SelectionMode = SelectionMode.Extended, DisplayMemberPath = nameof(DrawingMark.ListLabel) };
        private readonly ComboBox _kind = new() { MinWidth = 150 };
        private readonly ScrollViewer _scroll = new()
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        private readonly Canvas _page = new();
        private readonly Canvas _overlay = new() { IsHitTestVisible = false };
        private readonly Slider _zoom = new() { Minimum = 1, Maximum = 12, Value = 1, Width = 105 };
        private readonly TextBlock _status = new() { Margin = new Thickness(6), TextWrapping = TextWrapping.Wrap };
        private readonly Func<IReadOnlyList<DrawingMark>, string> _validate;
        private readonly BitmapSource _bitmap;
        private readonly DrawingRole _role;
        private readonly Dictionary<MarkKind, string> _kinds;
        private readonly ConstructionSource _source;
        private readonly IReadOnlyList<ConstructionChoice> _reviewedTypes;
        private readonly PlanAgentExchange _agentExchange;
        private readonly string[] _existingLevels;
        private readonly Action<PlanAlignment> _saveAlignment;
        private PickMode _pickMode;
        private Point? _first;
        private DrawingMark _moving;
        private bool _closingWithSave;

        public DrawingMarkupWindow(string name, DrawingRole role, BitmapSource bitmap, IEnumerable<DrawingMark> saved,
            PlanAlignment alignment, bool wasApproved, ConstructionSource source,
            IReadOnlyList<ConstructionChoice> reviewedTypes, Func<IReadOnlyList<DrawingMark>, string> validate,
            string[] existingLevels, Action<PlanAlignment> saveAlignment)
        {
            Title = $"Drawing Review | {name}";
            Width = 1140;
            Height = 780;
            MinWidth = 730;
            MinHeight = 470;
            _bitmap = bitmap;
            _role = role;
            _source = source;
            _reviewedTypes = reviewedTypes;
            _agentExchange = new PlanAgentExchange(source, bitmap, role);
            _validate = validate;
            _existingLevels = existingLevels;
            _saveAlignment = saveAlignment;
            Alignment = alignment;
            _marks = new ObservableCollection<DrawingMark>(saved);
            _kinds = Modes(role);

            var layout = new DockPanel();
            var toolbar = new WrapPanel { Margin = new Thickness(8) };
            _kind.ItemsSource = _kinds;
            _kind.DisplayMemberPath = "Value";
            _kind.SelectedValuePath = "Key";
            _kind.SelectedIndex = 0;
            _kind.SelectionChanged += (_, __) => Begin(PickMode.Add);
            toolbar.Children.Add(_kind);
            AddButton(toolbar, "Add mark", () => Begin(PickMode.Add));
            if (role == DrawingRole.Plan)
            {
                AddButton(toolbar, "Refine in area", () => Begin(PickMode.Detect));
                AddButton(toolbar, "Scan whole page", () => Suggest(PlanPageSuggestions.WholePage, wholePage: true));
                AddButton(toolbar, "Align plan", () => Begin(PickMode.Align));
                AddButton(toolbar, "Edit saved alignment", () =>
                {
                    if (Alignment == null) Begin(PickMode.Align);
                    else EditAlignment(Alignment.First, Alignment.Second);
                });
                AddButton(toolbar, "Find wall types", () => SuggestWallTypes(interactive: true));
            }
            toolbar.Children.Add(new TextBlock { Text = "Zoom", Margin = new Thickness(12, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
            toolbar.Children.Add(_zoom);
            _zoom.ValueChanged += (_, __) => ResizePage();
            DockPanel.SetDock(toolbar, Dock.Top);
            layout.Children.Add(toolbar);

            var footer = new DockPanel { Margin = new Thickness(8) };
            AddFooterButton(footer, "Cancel", () => Close());
            AddFooterButton(footer, "Approve for MCP", () => Save(approve: true));
            AddFooterButton(footer, "Save draft", () => Save(approve: false));
            footer.Children.Add(_status);
            DockPanel.SetDock(footer, Dock.Bottom);
            layout.Children.Add(footer);

            var sidebar = new DockPanel { Width = 270, Margin = new Thickness(8) };
            var review = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            AddButton(review, "Accept selected", () => EditSelected(accept: true));
            AddButton(review, "Remove selected", () => EditSelected(accept: false));
            AddButton(review, "Edit details", () =>
            {
                if (_list.SelectedItems.Count == 1 && _list.SelectedItem is DrawingMark mark)
                    Edit(mark, original: mark);
            });
            AddButton(review, "Move or redraw", () =>
            {
                if (_list.SelectedItems.Count == 1 && _list.SelectedItem is DrawingMark mark)
                    Begin(PickMode.Move, mark);
            });
            if (role == DrawingRole.Plan)
                AddButton(review, "Assign construction type", AssignType);
            var analyze = new Button
            {
                Content = "Analyze with Codex",
                ToolTip = "Sends this drawing image to the signed-in Codex model.",
                Margin = new Thickness(5, 2, 5, 2), Padding = new Thickness(6, 2, 6, 2)
            };
            analyze.Click += (_, _) => AnalyzeWithAgent();
            review.Children.Add(analyze);
            DockPanel.SetDock(review, Dock.Bottom);
            sidebar.Children.Add(review);
            sidebar.Children.Add(_list);
            _list.ItemsSource = _marks;
            _list.SelectionChanged += (_, __) => Redraw();
            DockPanel.SetDock(sidebar, Dock.Right);
            layout.Children.Add(sidebar);

            _scroll.Content = _page;
            var image = new Image { Source = bitmap, Stretch = Stretch.Fill };
            image.SetBinding(WidthProperty, new System.Windows.Data.Binding(nameof(Width)) { Source = _page });
            image.SetBinding(HeightProperty, new System.Windows.Data.Binding(nameof(Height)) { Source = _page });
            _page.Children.Add(image);
            _page.Children.Add(_overlay);
            _page.Cursor = Cursors.Cross;
            _page.MouseLeftButtonDown += OnPageClick;
            _scroll.SizeChanged += (_, __) => ResizePage();
            layout.Children.Add(new Border { Background = Brushes.Gainsboro, Child = _scroll });
            Content = layout;
            Loaded += (_, __) => ResizePage();
            PreviewKeyDown += (_, args) =>
            {
                if (args.Key == Key.Escape && (_first.HasValue || _pickMode != PickMode.Add))
                {
                    Begin(PickMode.Add);
                    args.Handled = true;
                }
            };
            if (wasApproved)
                Title += " · approved version on file";
            if (_list.Items.Count > 0)
                _list.SelectedIndex = 0;
        }

        public IReadOnlyList<DrawingMark> Marks => _marks;
        public PlanAlignment Alignment { get; private set; }
        public bool Approved { get; private set; }
        public double AspectRatio => (double)_bitmap.PixelWidth / _bitmap.PixelHeight;
        private MarkKind Kind => (MarkKind)_kind.SelectedValue;

        private static Dictionary<MarkKind, string> Modes(DrawingRole role)
        {
            var types = new Dictionary<MarkKind, string>();
            if (role == DrawingRole.Plan)
            {
                types.Add(MarkKind.ExteriorWall, "Exterior wall");
                types.Add(MarkKind.InteriorWall, "Interior wall");
            }
            else
            {
                types.Add(MarkKind.Level, "Project level");
                types.Add(MarkKind.Height, "Height");
            }
            types.Add(MarkKind.Door, "Door");
            types.Add(MarkKind.Window, "Window");
            types.Add(MarkKind.Construction, "Construction type");
            return types;
        }

        private static void AddButton(Panel panel, string caption, Action action)
        {
            var button = new Button { Content = caption, Margin = new Thickness(5, 2, 5, 2), Padding = new Thickness(6, 2, 6, 2) };
            button.Click += (_, __) => action();
            panel.Children.Add(button);
        }

        private static void AddFooterButton(DockPanel panel, string caption, Action action)
        {
            var button = new Button { Content = caption, Margin = new Thickness(8, 0, 0, 0), MinWidth = 84 };
            button.Click += (_, __) => action();
            DockPanel.SetDock(button, Dock.Right);
            panel.Children.Add(button);
        }

        private void Begin(PickMode mode, DrawingMark moving = null)
        {
            _pickMode = mode;
            _first = null;
            _moving = moving;
            Redraw();
        }

        private void EditSelected(bool accept)
        {
            var selected = _list.SelectedItems.Cast<DrawingMark>().ToArray();
            foreach (DrawingMark mark in selected)
            {
                if (!accept)
                    _marks.Remove(mark);
                else if (mark.Suggested)
                    _marks[_marks.IndexOf(mark)] = mark with { Suggested = false };
            }
            if (!accept)
                foreach (var mark in selected)
                    DetachLocalType(mark.Construction);
            Begin(PickMode.Add);
        }

        private void Edit(DrawingMark mark, DrawingMark original = null)
        {
            if (mark.Boundary != null)
            {
                AssignType();
                return;
            }
            DrawingMark edited;
            if (mark.Kind == MarkKind.Construction)
            {
                var dialog = new ConstructionTypeWindow(mark) { Owner = this };
                if (dialog.ShowDialog() != true) return;
                edited = dialog.Mark;
            }
            else
            {
                var dialog = new DrawingNoteWindow(_kinds.Keys.Where(kind => kind != MarkKind.Construction).ToArray(), mark) { Owner = this };
                if (dialog.ShowDialog() != true) return;
                edited = dialog.Mark;
            }
            if (original == null) _marks.Add(edited);
            else
            {
                _marks[_marks.IndexOf(original)] = edited;
                if (original.Construction != null && !ConstructionTypes.SameSpecification(original.Construction, edited.Construction))
                    DetachLocalType(original.Construction);
            }
            _list.SelectedItem = edited;
            _list.ScrollIntoView(edited);
            Begin(PickMode.Add);
        }

        private IEnumerable<ConstructionChoice> AvailableTypes() => ConstructionTypes.Distinct(
            _marks.Where(mark => !mark.Suggested && mark.Construction != null)
                .Select(mark => new ConstructionChoice(mark.Construction, _source)).Concat(_reviewedTypes));

        private void DetachLocalType(ConstructionSpec spec)
        {
            if (spec == null || _marks.Any(mark => !mark.Suggested && ConstructionTypes.SameSpecification(spec, mark.Construction)))
                return;
            for (int i = 0; i < _marks.Count; i++)
            {
                var mark = _marks[i];
                if (mark.AssignedType?.Source == _source && ConstructionTypes.SameSpecification(spec, mark.AssignedType.Spec))
                    _marks[i] = mark with { AssignedType = null, AutomaticTypeMatch = false, Suggested = true };
            }
        }

        private void AssignType()
        {
            var selected = _list.SelectedItems.Cast<DrawingMark>().ToArray();
            if (selected.Length == 0 || selected.Any(mark => !mark.IsWall && mark.Boundary == null))
            {
                MessageBox.Show(this, "Select wall traces or a floor outline first.", "Drawing Review");
                return;
            }
            var choices = AvailableTypes().Where(choice => selected.All(mark =>
                mark.Boundary != null ? choice.Spec.Category is ConstructionKind.Floor or ConstructionKind.SlabOnGrade
                    : ConstructionTypes.MatchesWall(choice.Spec.Category, mark.Kind == MarkKind.ExteriorWall))).ToArray();
            if (choices.Length == 0 && selected.All(mark => mark.AssignedType == null))
            {
                MessageBox.Show(this, "Review a matching construction type on this page or approve it on a reference page first.", "Drawing Review");
                return;
            }
            var dialog = new AssignConstructionTypeWindow(choices) { Owner = this };
            if (dialog.ShowDialog() != true) return;
            foreach (var mark in selected)
                _marks[_marks.IndexOf(mark)] = mark with { AssignedType = dialog.Selected, AutomaticTypeMatch = false };
            Begin(PickMode.Add);
        }

        private void SuggestWallTypes(bool interactive)
        {
            var choices = AvailableTypes().ToArray();
            int found = 0;
            for (int i = 0; i < _marks.Count; i++)
            {
                var mark = _marks[i];
                if (!mark.IsWall || mark.AssignedType != null || (!interactive && !mark.Suggested)) continue;
                var type = ConstructionTypes.Match(choices, mark.Kind == MarkKind.ExteriorWall, mark.Label, MeasuredWallMm(mark));
                if (type == null) continue;
                _marks[i] = mark with { AssignedType = type, AutomaticTypeMatch = true, Suggested = true };
                found++;
            }
            if (interactive && found == 0)
                MessageBox.Show(this, "No unique matches. Align the plan for thickness-based matching and check that candidate types have a printed total thickness. Use Assign wall type to select a reviewed type.", "Drawing Review");
            Redraw();
        }

        private double? MeasuredWallMm(DrawingMark mark)
        {
            if (Alignment == null || !mark.PageWallThickness.HasValue || !mark.End.HasValue) return null;
            var delta = mark.End.Value - mark.Start;
            double width = mark.PageWallThickness.Value;
            var offset = Math.Abs(delta.X) * AspectRatio >= Math.Abs(delta.Y) ? new Vector(0, width) : new Vector(width, 0);
            return (Alignment.ToProject(mark.Start + offset, AspectRatio) - Alignment.ToProject(mark.Start, AspectRatio)).Length;
        }

        private void OnPageClick(object sender, MouseButtonEventArgs e)
        {
            var position = e.GetPosition(_page);
            var point = new Point(position.X / _page.Width, position.Y / _page.Height);
            if (point.X < 0 || point.X > 1 || point.Y < 0 || point.Y > 1) return;

            if (_pickMode == PickMode.Move && _moving.Boundary != null)
            {
                MoveFloorCorner(point);
                return;
            }

            var needsTwo = _pickMode is PickMode.Detect or PickMode.Align
                || (_pickMode == PickMode.Move ? _moving.End.HasValue : Kind is MarkKind.ExteriorWall or MarkKind.InteriorWall);
            if (needsTwo)
            {
                if (!_first.HasValue)
                {
                    _first = point;
                    Redraw();
                    return;
                }
                if ((ToScreen(point) - ToScreen(_first.Value)).Length < 3) return;
            }

            var mode = _pickMode;
            var initial = _first;
            var moving = _moving;
            Begin(PickMode.Add);
            switch (mode)
            {
                case PickMode.Detect:
                    Suggest(new Rect(initial.Value, point), wholePage: false);
                    break;
                case PickMode.Align:
                    EditAlignment(initial.Value, point);
                    break;
                case PickMode.Move:
                    var moved = moving with
                    {
                        Start = initial ?? point, End = needsTwo ? point : null,
                        Suggested = moving.AutomaticTypeMatch, PageWallThickness = null,
                        AssignedType = moving.AutomaticTypeMatch ? null : moving.AssignedType, AutomaticTypeMatch = false
                    };
                    _marks[_marks.IndexOf(moving)] = moved;
                    _list.SelectedItem = moved;
                    break;
                default:
                    var added = new DrawingMark(Kind, initial ?? point, needsTwo ? point : null);
                    if (added.IsWall)
                    {
                        _marks.Add(added);
                        _list.SelectedItem = added;
                    }
                    else Edit(added);
                    break;
            }
            Redraw();
        }

        private void MoveFloorCorner(Point point)
        {
            var boundary = _moving.Boundary;
            if (!_first.HasValue)
            {
                var nearest = boundary.OrderBy(vertex => (ToScreen(vertex) - ToScreen(point)).Length).First();
                if ((ToScreen(nearest) - ToScreen(point)).Length > 25) return;
                _first = nearest;
                Redraw();
                return;
            }
            var fixedCorner = boundary[(Array.IndexOf(boundary, _first.Value) + 2) % 4];
            var rectangle = new Rect(fixedCorner, point);
            if (rectangle.Width < 0.005 || rectangle.Height < 0.005) return;
            var moved = _moving with
            {
                Start = rectangle.TopLeft,
                Boundary = new[] { rectangle.TopLeft, rectangle.TopRight, rectangle.BottomRight, rectangle.BottomLeft },
                Suggested = false
            };
            _marks[_marks.IndexOf(_moving)] = moved;
            _list.SelectedItem = moved;
            Begin(PickMode.Add);
        }

        private void Suggest(Rect area, bool wholePage)
        {
            var found = PlanPageSuggestions.Read(_bitmap, area, wholePage);
            if (found.Count == 0)
            {
                MessageBox.Show(this, "No plan geometry found. Try a clearer or tighter plan area.", "Drawing Review");
                return;
            }

            ReplacePendingGeometry(found);
        }

        private void EditAlignment(Point first, Point second)
        {
            var dialog = new PlanAlignmentWindow(first, second, Alignment, _existingLevels) { Owner = this };
            if (dialog.ShowDialog() != true || Equals(Alignment, dialog.Alignment)) return;
            try { _saveAlignment(dialog.Alignment); }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Alignment could not be saved.\n\n{ex.Message}", "Drawing Review");
                return;
            }
            Alignment = dialog.Alignment;
            Title = $"Drawing Review | {_source.ViewName}";
            for (int i = 0; i < _marks.Count; i++)
                if (_marks[i].AutomaticTypeMatch)
                    _marks[i] = ResetAutomaticType(_marks[i]);
            SuggestWallTypes(interactive: false);
        }

        internal static DrawingMark ResetAutomaticType(DrawingMark mark) => mark.AutomaticTypeMatch
            ? mark with { AssignedType = null, AutomaticTypeMatch = false, Suggested = true } : mark;

        private void AnalyzeWithAgent()
        {
            var job = new PlanAgentJob(_source.Page, _role, _agentExchange);
            var dialog = new PlanAgentProgressWindow(new[] { job }, Array.Empty<byte[]>()) { Owner = this };
            dialog.ShowDialog();
            var result = dialog.Outcomes.FirstOrDefault();
            if (result?.Marks != null)
            {
                if (_role == DrawingRole.Plan) ReplacePendingGeometry(result.Marks, replaceFloor: false);
                else ReplaceReferenceNotes(result.Marks);
            }
            else if (result?.Error != null)
                MessageBox.Show(this, result.Error, "Drawing Review");
        }

        private void ReplaceReferenceNotes(IEnumerable<DrawingMark> found)
        {
            foreach (var previous in _marks.Where(mark => mark.Suggested && mark.AgentEvidence != null).ToArray())
                _marks.Remove(previous);
            foreach (var mark in found)
                if (!_marks.Any(existing => existing.Kind == mark.Kind
                    && string.Equals(existing.Annotation, mark.Annotation, StringComparison.OrdinalIgnoreCase)
                    && (existing.Start - mark.Start).Length < 0.012))
                    _marks.Add(mark);
            Begin(PickMode.Add);
        }

        private void ReplacePendingGeometry(IEnumerable<DrawingMark> found, bool replaceFloor = true)
        {
            // Keep reviewed edits, floor proposals and text evidence separate from wall and opening runs.
            foreach (var previous in _marks.Where(mark => mark.Suggested
                && (mark.End.HasValue || replaceFloor && mark.Boundary != null)).ToArray())
                _marks.Remove(previous);
            foreach (var mark in found)
                if (!_marks.Any(existing => PlanPageSuggestions.SameGeometry(existing, mark)))
                    _marks.Add(mark);
            SuggestWallTypes(interactive: false);
            Begin(PickMode.Add);
        }

        private void ResizePage()
        {
            var width = Math.Min(_scroll.ActualWidth - 22, (_scroll.ActualHeight - 22) * AspectRatio) * _zoom.Value;
            if (width <= 0) return;
            _page.Width = width;
            _page.Height = width / AspectRatio;
            Redraw();
        }

        private Point ToScreen(Point point) => new(point.X * _page.Width, point.Y * _page.Height);

        private void Redraw()
        {
            if (!double.IsFinite(_page.Width)) return;
            _overlay.Children.Clear();
            foreach (var mark in _marks)
            {
                var point = ToScreen(mark.Start);
                var color = mark.Kind switch
                {
                    MarkKind.ExteriorWall => Brushes.Firebrick,
                    MarkKind.InteriorWall => Brushes.SteelBlue,
                    MarkKind.Door => Brushes.DarkOrange,
                    MarkKind.Window => Brushes.Teal,
                    MarkKind.FloorFootprint => Brushes.ForestGreen,
                    _ => Brushes.DarkViolet
                };
                var selected = _list.SelectedItems.Contains(mark);
                if (mark.Boundary != null)
                {
                    _overlay.Children.Add(new Polygon
                    {
                        Points = new PointCollection(mark.Boundary.Select(ToScreen)),
                        Stroke = color, StrokeThickness = selected ? 4 : 2,
                        Fill = new SolidColorBrush(Color.FromArgb(24, 39, 125, 74)),
                        StrokeDashArray = mark.Suggested ? new DoubleCollection { 3, 2 } : null
                    });
                    if (selected)
                        foreach (var vertex in mark.Boundary) Dot(ToScreen(vertex), color);
                }
                if (mark.End.HasValue)
                {
                    var end = ToScreen(mark.End.Value);
                    _overlay.Children.Add(new Line
                    {
                        X1 = point.X, Y1 = point.Y, X2 = end.X, Y2 = end.Y,
                        Stroke = color, StrokeThickness = selected ? 6 : 3,
                        StrokeDashArray = mark.Suggested ? new DoubleCollection { 3, 2 } : null
                    });
                }
                if ((mark.Boundary == null && !mark.End.HasValue) || selected && (!mark.IsWall || mark.AssignedType != null))
                {
                    var text = new TextBlock { Text = mark.ListLabel, Padding = new Thickness(3), Background = Brushes.WhiteSmoke };
                    Canvas.SetLeft(text, point.X + 5);
                    Canvas.SetTop(text, point.Y);
                    _overlay.Children.Add(text);
                    Dot(point, color);
                }
            }
            if (Alignment != null)
            {
                AlignmentAnchor(Alignment.First, "A");
                AlignmentAnchor(Alignment.Second, "B");
            }
            if (_first.HasValue) Dot(ToScreen(_first.Value), Brushes.DarkOrange);

            var prompt = _pickMode switch
            {
                PickMode.Detect => _first.HasValue ? "Click the opposite corner." : "Click a corner of the plan area. Exclude title blocks and schedules.",
                PickMode.Align => _first.HasValue ? "Click the second alignment point." : "Click the first point with a known Revit project coordinate.",
                PickMode.Move when _moving.Boundary != null => _first.HasValue ? "Click the new location of that corner." : "Click one corner of the floor outline.",
                PickMode.Move => _first.HasValue ? "Click the new end point." : "Click the new location or start point.",
                _ => _first.HasValue ? "Click the end of the wall segment."
                    : _role == DrawingRole.Plan && _marks.Any(mark => mark.Suggested)
                        ? "Select proposals to edit, accept or remove. Use Refine in area if the page contains multiple drawings."
                        : "Click the drawing to add a mark."
            };
            _status.Text = $"{prompt}  {_marks.Count} marks · {_marks.Count(m => m.Suggested)} to review"
                + (_role == DrawingRole.Plan ? Alignment == null ? " · Alignment needed" : $" · Alignment saved · Level: {Alignment.Level}" : "");
        }

        private void AlignmentAnchor(Point anchor, string name)
        {
            var point = ToScreen(anchor);
            Dot(point, Brushes.DarkViolet);
            var badge = new TextBlock { Text = name, Background = Brushes.White, Foreground = Brushes.DarkViolet, Padding = new Thickness(3) };
            Canvas.SetLeft(badge, point.X + 5);
            Canvas.SetTop(badge, point.Y - 12);
            _overlay.Children.Add(badge);
        }

        private void Dot(Point point, Brush color)
        {
            var dot = new Ellipse { Width = 8, Height = 8, Fill = color };
            Canvas.SetLeft(dot, point.X - 4);
            Canvas.SetTop(dot, point.Y - 4);
            _overlay.Children.Add(dot);
        }

        private void Save(bool approve)
        {
            if (_closingWithSave) return;
            string error = _first.HasValue ? "Finish the current mark or press Esc first." : _validate(Marks);
            if (error == null && approve)
            {
                if (_marks.Count == 0) error = "Add a mark before approving this page.";
                else if (_marks.Any(m => m.Suggested)) error = "Review every suggestion. Accept, edit or remove each candidate.";
                else if (_role == DrawingRole.Plan && Alignment == null)
                {
                    Begin(PickMode.Align);
                    error = "No saved alignment for this plan. Click two known points, enter their project coordinates and level, then choose Save alignment.";
                }
                else if (Alignment != null && _marks.Any(m => !InsideProject(m.Start) || (m.End.HasValue && !InsideProject(m.End.Value))
                    || m.Boundary?.Any(p => !InsideProject(p)) == true))
                    error = "Some marks map outside the Revit project range. Check the two alignment points and their units.";
            }
            if (error != null)
            {
                MessageBox.Show(this, error, "Drawing Review", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (approve && MessageBox.Show(this, $"Make these {_marks.Count} reviewed marks available to MCP?",
                "Approve drawing page", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
                return;
            Approved = approve;
            _closingWithSave = true;
            DialogResult = true;
        }

        private bool InsideProject(Point pagePoint)
        {
            var point = Alignment.ToProject(pagePoint, AspectRatio);
            return double.IsFinite(point.X) && double.IsFinite(point.Y)
                && Math.Abs(point.X) <= 15_000_000 && Math.Abs(point.Y) <= 15_000_000;
        }
    }
}
