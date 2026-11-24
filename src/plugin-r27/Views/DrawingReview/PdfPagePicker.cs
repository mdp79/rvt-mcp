using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NamingUtils = Autodesk.Revit.DB.NamingUtils;

namespace RvtMcp.Plugin.Views.DrawingReview
{
    internal sealed record PdfPageSelection(int Number, string Name, DrawingRole Role);

    internal sealed class PdfPagePicker : DrawingReviewWindow
    {
        private readonly SortedDictionary<int, PdfPageSelection> _selected = new();
        private readonly ListBox _pages = new();
        private readonly TextBox _name = new() { MaxLength = 80 };
        private readonly ComboBox _role = new() { ItemsSource = Enum.GetValues<DrawingRole>() };
        private readonly Image _preview = new() { Stretch = Stretch.Uniform, Margin = new Thickness(12) };
        private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8) };
        private readonly Button _apply = new() { MinWidth = 140, IsEnabled = false };
        private readonly CheckBox _analyze = new()
        {
            Content = "Analyze selected drawings with Codex", IsChecked = true, IsEnabled = false,
            Margin = new Thickness(8, 2, 8, 2)
        };
        private readonly Func<int, BitmapSource> _loadPreview;
        private readonly int _count;
        private bool _updatingList;

        public PdfPagePicker(string fileName, int count, Func<int, BitmapSource> loadPreview)
        {
            Title = $"Select drawing pages | {fileName}";
            Width = 1000;
            Height = 740;
            MinWidth = 640;
            MinHeight = 460;
            _count = count;
            _loadPreview = loadPreview;
            _pages.ItemsSource = Enumerable.Range(1, count).Select(i => $"Page {i}").ToArray();
            var layout = new DockPanel { Margin = new Thickness(10) };
            var footer = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
            var analysis = new StackPanel { Margin = new Thickness(0, 4, 0, 6) };
            analysis.Children.Add(_analyze);
            analysis.Children.Add(new TextBlock
            {
                Text = "Sends only the selected drawing images. Plans, elevations and sections get their own proposals.",
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 2, 8, 2)
            });
            DockPanel.SetDock(analysis, Dock.Top);
            footer.Children.Add(analysis);
            AddRightButton(footer, new Button { Content = "Cancel", IsCancel = true, MinWidth = 84 });
            AddRightButton(footer, _apply);
            _apply.Click += (_, __) => DialogResult = true;
            footer.Children.Add(_status);
            DockPanel.SetDock(footer, Dock.Bottom);
            layout.Children.Add(footer);

            var sidebar = new DockPanel { Width = 245, Margin = new Thickness(0, 0, 10, 0) };
            var controls = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            controls.Children.Add(new TextBlock { Text = "Drawing type" });
            controls.Children.Add(_role);
            controls.Children.Add(new TextBlock { Text = "Reference name", Margin = new Thickness(0, 8, 0, 0) });
            controls.Children.Add(_name);
            var keep = new Button { Content = "Keep or update page", Margin = new Thickness(0, 8, 0, 0) };
            keep.Click += (_, __) => KeepPage();
            controls.Children.Add(keep);
            var remove = new Button { Content = "Remove from selection", Margin = new Thickness(0, 4, 0, 0) };
            remove.Click += (_, __) => { _selected.Remove(PageNumber); RefreshList(); };
            controls.Children.Add(remove);
            DockPanel.SetDock(controls, Dock.Bottom);
            sidebar.Children.Add(controls);
            sidebar.Children.Add(_pages);
            DockPanel.SetDock(sidebar, Dock.Left);
            layout.Children.Add(sidebar);
            layout.Children.Add(new Border { Background = Brushes.Gainsboro, Child = _preview });
            Content = layout;

            _pages.SelectionChanged += (_, __) => ShowPage();
            _analyze.Checked += (_, __) => UpdateImportLabel();
            _analyze.Unchecked += (_, __) => UpdateImportLabel();
            Loaded += (_, __) => { RefreshList(); _pages.SelectedIndex = 0; };
        }

        private int PageNumber => _pages.SelectedIndex + 1;
        public IReadOnlyList<PdfPageSelection> SelectedPages => _selected.Values.ToArray();
        public bool AnalyzeWithAgent => _analyze.IsEnabled && _analyze.IsChecked == true;

        private static void AddRightButton(DockPanel panel, Button button)
        {
            button.Margin = new Thickness(8, 0, 0, 0);
            DockPanel.SetDock(button, Dock.Right);
            panel.Children.Add(button);
        }

        private void ShowPage()
        {
            if (_updatingList || PageNumber < 1) return;
            _name.Text = _selected.TryGetValue(PageNumber, out var item) ? item.Name : $"Drawing {PageNumber}";
            _role.SelectedItem = item?.Role ?? DrawingRole.Plan;
            try
            {
                _preview.Source = _loadPreview(PageNumber);
                _status.Text = $"Page {PageNumber} of {_count}.";
            }
            catch (Exception ex)
            {
                _preview.Source = null;
                _status.Text = $"Could not preview page {PageNumber}. {ex.Message}";
            }
        }

        private void KeepPage()
        {
            if (PageNumber < 1 || _preview.Source == null) return;
            var name = _name.Text.Trim();
            if (name.Length == 0 || !NamingUtils.IsValidName(name))
            {
                _status.Text = "Enter a reference name without Revit's reserved punctuation.";
                return;
            }

            _selected[PageNumber] = new PdfPageSelection(PageNumber, name, (DrawingRole)_role.SelectedItem);
            RefreshList();
        }

        private void RefreshList()
        {
            var index = _pages.SelectedIndex;
            _updatingList = true;
            _pages.ItemsSource = Enumerable.Range(1, _count)
                .Select(i => _selected.TryGetValue(i, out var page) ? $"{i}  ✓ {page.Role} · {page.Name}" : $"Page {i}")
                .ToArray();
            _pages.SelectedIndex = index;
            _updatingList = false;
            _apply.IsEnabled = _selected.Count > 0;
            _analyze.IsEnabled = _selected.Count > 0;
            UpdateImportLabel();
        }

        private void UpdateImportLabel() => _apply.Content = AnalyzeWithAgent
            ? $"Import and analyze ({_selected.Count})" : $"Import selected ({_selected.Count})";

    }
}
