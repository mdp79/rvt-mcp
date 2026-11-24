using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Media.Imaging;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Autodesk.Revit.UI;
using Microsoft.Win32;
using RvtMcp.Plugin.Views.DrawingReview;
using Point = System.Windows.Point;

namespace RvtMcp.Plugin.Commands
{
    [Transaction(TransactionMode.Manual)]
    public sealed class DrawingReviewCommand : IExternalCommand
    {
        private static readonly Guid ReferenceSchemaId = new Guid("dd88e28f-c058-4270-a2ae-0dd763217ed4");
        private const string ReviewKindField = "ReviewKind";

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var app = commandData.Application;
            var uiDoc = app.ActiveUIDocument;
            if (uiDoc == null || uiDoc.Document.IsFamilyDocument || uiDoc.Document.IsReadOnly)
            {
                TaskDialog.Show("Drawing Review", "Open an editable Revit project.");
                return Result.Cancelled;
            }

            try
            {
                var view = uiDoc.ActiveView as ViewDrafting;
                var schema = Schema.Lookup(ReferenceSchemaId);
                var reference = view == null || schema == null ? null : FindReference(view, schema);
                if (reference != null)
                {
                    var dialog = new TaskDialog("Drawing Review")
                    {
                        MainInstruction = view.Name,
                        CommonButtons = TaskDialogCommonButtons.Cancel
                    };
                    dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Review this PDF page");
                    dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Open another PDF");
                    switch (dialog.Show())
                    {
                        case TaskDialogResult.CommandLink1:
                            return Review(app, view, reference, schema);
                        case TaskDialogResult.CommandLink2:
                            break;
                        default:
                            return Result.Cancelled;
                    }
                }
                return Import(app, uiDoc);
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }

        private static ImageInstance FindReference(ViewDrafting view, Schema schema)
        {
            var references = new FilteredElementCollector(view.Document)
                .OwnedByView(view.Id)
                .OfClass(typeof(ImageInstance))
                .Cast<ImageInstance>()
                .Where(image => image.GetEntity(schema).IsValid()).Take(2).ToList();
            if (references.Count > 1)
                throw new InvalidOperationException("This drafting view contains multiple Drawing Review pages. Keep one page per view.");
            return references.FirstOrDefault();
        }

        private static Result Import(UIApplication app, UIDocument uiDoc)
        {
            var picker = new OpenFileDialog { Filter = "PDF drawings (*.pdf)|*.pdf", CheckFileExists = true };
            if (picker.ShowDialog() != true)
                return Result.Cancelled;
            int pageCount;
            using (var pdf = UglyToad.PdfPig.PdfDocument.Open(picker.FileName))
                pageCount = pdf.NumberOfPages;
            if (pageCount == 0)
                throw new InvalidOperationException("The selected PDF contains no pages.");

            var doc = uiDoc.Document;
            var settings = new PdfPagePicker(Path.GetFileName(picker.FileName), pageCount,
                page => PreviewPage(doc, picker.FileName, page));
            settings.SetOwner(app.MainWindowHandle);
            if (settings.ShowDialog() != true)
                return Result.Cancelled;

            var textSuggestions = ReadTextSuggestions(picker.FileName, settings.SelectedPages);
            var viewType = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType))
                .Cast<ViewFamilyType>().FirstOrDefault(type => type.ViewFamily == ViewFamily.Drafting);
            if (viewType == null)
                throw new InvalidOperationException("This project has no drafting view type.");

            var schema = Schema.Lookup(ReferenceSchemaId) ?? CreateSchema();
            var imported = new List<(PdfPageSelection Page, ViewDrafting View, ImageInstance Image)>();
            using (var tx = new Transaction(doc, "Drawing Review: Import selected PDF pages"))
            {
                Start(tx);
                foreach (var page in settings.SelectedPages)
                {
                    using var options = new ImageTypeOptions(picker.FileName, false, ImageTypeSource.Import)
                    {
                        PageNumber = page.Number,
                        Resolution = 150
                    };
                    var imageType = ImageType.Create(doc, options);
                    var view = ViewDrafting.Create(doc, viewType.Id);
                    view.Name = AvailableViewName(doc, $"{page.Role} reference - {page.Name} - p{page.Number}");
                    view.Scale = 1;
                    using var placement = new ImagePlacementOptions(XYZ.Zero, BoxPlacement.Center);
                    var image = ImageInstance.Create(doc, view, imageType.Id, placement);
                    image.DrawLayer = DrawLayer.Background;
                    image.Pinned = true;
                    Tag(image, schema, page.Role.ToString());
                    var marks = textSuggestions.TryGetValue(page.Number, out var textMarks)
                        ? textMarks.ToList() : new List<DrawingMark>();
                    bool plan = page.Role == DrawingRole.Plan;
                    if (plan && !settings.AnalyzeWithAgent)
                        marks.AddRange(PlanPageSuggestions.Read(ReadBitmap(imageType, 1800), PlanPageSuggestions.WholePage, wholePage: true));
                    if (marks.Count > 0 || plan)
                        DrawingReviewStore.Save(view, page.Role, page.Number, new DrawingReviewState(marks.ToArray(), null, plan), 1, approve: false);
                    imported.Add((page, view, image));
                }
                Commit(tx);
            }

            int? firstAnalyzed = null;
            if (settings.AnalyzeWithAgent)
            {
                try { firstAnalyzed = AnalyzeImportedDrawings(app, doc, imported); }
                catch (Exception ex)
                {
                    TaskDialog.Show("Drawing Review", $"The PDF pages were imported. Analysis could not finish.\n\n{ex.Message}\n\nRetry with Analyze with Codex in the drawing editor.");
                }
            }
            var review = imported.FirstOrDefault(item => item.Page.Number == firstAnalyzed);
            if (review.View == null)
                review = imported.FirstOrDefault(item => item.Page.Role == DrawingRole.Plan);
            if (review.View == null)
                review = imported[0];
            try
            {
                Review(app, review.View, review.Image, schema);
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Drawing Review", $"The PDF pages were imported. Open Drawing Review from the reference view to retry review.\n\n{ex.Message}");
            }
            uiDoc.RequestViewChange(review.View);
            return Result.Succeeded;
        }

        private static int? AnalyzeImportedDrawings(UIApplication app, Document doc,
            IReadOnlyList<(PdfPageSelection Page, ViewDrafting View, ImageInstance Image)> imported)
        {
            var jobs = imported.OrderBy(item => item.Page.Role != DrawingRole.Plan)
                .Select(item => new PlanAgentJob(item.Page.Number, item.Page.Role,
                new PlanAgentExchange(new ConstructionSource(item.View.UniqueId, item.View.Name, item.Page.Number),
                    ReadBitmap((ImageType)doc.GetElement(item.Image.GetTypeId()), 1800), item.Page.Role))).ToArray();
            var references = imported.Any(item => item.Page.Role == DrawingRole.Plan)
                ? imported.Where(item => item.Page.Role != DrawingRole.Plan).Take(4)
                .Select(item => PlanAgentExchange.EncodePng(ReadBitmap((ImageType)doc.GetElement(item.Image.GetTypeId()), 1600)))
                .ToArray() : Array.Empty<byte[]>();
            var dialog = new PlanAgentProgressWindow(jobs, references);
            dialog.SetOwner(app.MainWindowHandle);
            dialog.ShowDialog();

            var completed = dialog.Outcomes.Where(outcome => outcome.Marks != null).ToArray();
            if (completed.Length > 0)
            {
                using var tx = new Transaction(doc, "Drawing Review: Save agent proposals");
                Start(tx);
                foreach (var outcome in completed)
                {
                    var page = imported.Single(item => item.Page.Number == outcome.Page);
                    var state = DrawingReviewStore.ReadDraft(page.View) ?? new DrawingReviewState(Array.Empty<DrawingMark>(), null);
                    DrawingReviewStore.Save(page.View, page.Page.Role, page.Page.Number,
                        state with { Marks = state.Marks.Concat(outcome.Marks).ToArray() }, 1, approve: false);
                }
                Commit(tx);
            }

            var failures = dialog.Outcomes.Where(outcome => outcome.Error != null)
                .GroupBy(outcome => outcome.Error)
                .Select(result => $"PDF {string.Join(", ", result.Select(item => item.Page))}: {result.Key}").ToArray();
            if (failures.Length > 0 || dialog.WasCancelled)
            {
                string message = $"Imported all selected pages. Agent proposals received for {completed.Length} of {jobs.Length} drawings.";
                if (dialog.WasCancelled) message += "\n\nAnalysis stopped.";
                if (failures.Length > 0) message += "\n\n" + string.Join("\n", failures.Take(5));
                message += "\n\nRetry any page with Analyze with Codex in its Drawing Review editor.";
                TaskDialog.Show("Drawing Review", message);
            }
            return completed.FirstOrDefault()?.Page;
        }

        private static Dictionary<int, DrawingMark[]> ReadTextSuggestions(string fileName, IReadOnlyList<PdfPageSelection> pages)
        {
            var suggestions = new Dictionary<int, DrawingMark[]>();
            using var pdf = UglyToad.PdfPig.PdfDocument.Open(fileName);
            foreach (var page in pages)
            {
                try
                {
                    suggestions[page.Number] = PdfTextSuggestions.Read(pdf.GetPage(page.Number), page.Role).Take(100).ToArray();
                }
                catch (Exception ex) when (ex is UglyToad.PdfPig.Core.PdfDocumentFormatException or NotSupportedException)
                {
                    // Revit may still be able to render a page whose text cannot be analyzed.
                    DiagnosticLog.Write("DrawingReview", $"Could not read text suggestions for PDF page {page.Number}.");
                }
            }
            return suggestions;
        }

        private static BitmapSource PreviewPage(Document doc, string fileName, int page)
        {
            using var tx = new Transaction(doc, "Drawing Review: Preview PDF page");
            Start(tx);
            using var options = new ImageTypeOptions(fileName, false, ImageTypeSource.Import)
            {
                PageNumber = page,
                Resolution = 60
            };
            try
            {
                return ReadBitmap(ImageType.Create(doc, options));
            }
            finally
            {
                tx.RollBack(tx.GetFailureHandlingOptions().SetClearAfterRollback(true));
            }
        }

        private static Result Review(UIApplication app, ViewDrafting view, ImageInstance reference, Schema schema)
        {
            var doc = view.Document;
            var type = (ImageType)doc.GetElement(reference.GetTypeId());
            var role = (DrawingRole)Enum.Parse(typeof(DrawingRole), reference.GetEntity(schema).Get<string>(schema.GetField(ReviewKindField)));
            var topLeft = reference.GetLocation(BoxPlacement.TopLeft);
            var across = reference.GetLocation(BoxPlacement.TopRight) - topLeft;
            var down = reference.GetLocation(BoxPlacement.BottomLeft) - topLeft;
            var width = across.GetLength();
            var height = down.GetLength();
            if (width <= 0 || height <= 0)
                throw new InvalidOperationException("The PDF reference has no usable page dimensions.");
            var viewOrigin = view.Origin;
            var viewNormal = view.ViewDirection;
            XYZ DrawingPoint(Point point)
            {
                var position = topLeft + across * point.X + down * point.Y;
                return position - viewNormal * (position - viewOrigin).DotProduct(viewNormal);
            }
            var minimumTrace = doc.Application.ShortCurveTolerance;
            var textType = doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType);
            var hasTextType = doc.GetElement(textType) is TextNoteType;
            var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                .Select(level => (level.Name, Mm: UnitUtils.ConvertFromInternalUnits(level.ProjectElevation, UnitTypeId.Millimeters)))
                .ToList();
            var tagged = new FilteredElementCollector(doc).OwnedByView(view.Id).WhereElementIsNotElementType()
                .Where(element => (element is DetailCurve || element is TextNote)
                    && element.GetEntity(schema).IsValid()).ToArray();
            var draft = DrawingReviewStore.ReadDraft(view);
            var saved = (draft?.Marks ?? tagged.Select(element => ReadMarkup(element, schema, topLeft, across, down))
                .OfType<DrawingMark>().ToArray()).ToList();
            var bitmap = ReadBitmap(type);
            bool wasApproved = DrawingReviewStore.IsApproved(view);
            if (role == DrawingRole.Plan && draft?.AutoPlanAnalyzed != true && !wasApproved)
                saved.AddRange(PlanPageSuggestions.Read(bitmap, PlanPageSuggestions.WholePage, wholePage: true)
                    .Where(mark => !saved.Any(existing => PlanPageSuggestions.SameGeometry(mark, existing))).ToArray());
            bool alignmentSaved = false;
            void SaveAlignment(PlanAlignment alignment)
            {
                var state = DrawingReviewStore.ReadDraft(view) ?? new DrawingReviewState(saved.ToArray(), null, true);
                if (Equals(state.Alignment, alignment)) return;
                var reset = state.Marks.Select(DrawingMarkupWindow.ResetAutomaticType).ToArray();
                using var tx = new Transaction(doc, "Drawing Review: Save plan alignment");
                Start(tx);
                DrawingReviewStore.Save(view, role, type.PageNumber, state with { Alignment = alignment, Marks = reset },
                    (double)bitmap.PixelWidth / bitmap.PixelHeight, approve: false);
                Commit(tx);
                alignmentSaved = true;
            }
            var editor = new DrawingMarkupWindow(view.Name, role, bitmap, saved, draft?.Alignment,
                wasApproved, new ConstructionSource(view.UniqueId, view.Name, type.PageNumber),
                role == DrawingRole.Plan ? DrawingReviewStore.ApprovedConstructionTypes(doc, view.Id).ToArray() : Array.Empty<ConstructionChoice>(),
                marks => ValidateMarkup(marks, levels, width, height, minimumTrace, hasTextType),
                levels.Select(level => level.Name).OrderBy(name => name).ToArray(), SaveAlignment);
            editor.SetOwner(app.MainWindowHandle);
            if (editor.ShowDialog() != true)
                return alignmentSaved ? Result.Succeeded : Result.Cancelled;

            using (var tx = new Transaction(doc, "Drawing Review: Save reviewed PDF markup"))
            {
                Start(tx);
                var oldIds = tagged.Select(element => element.Id).ToHashSet();
                if (oldIds.Count > 0 && doc.Delete(oldIds).Any(id => !oldIds.Contains(id)))
                    throw new InvalidOperationException("A review annotation has dependent Revit elements. Remove those dependencies before replacing the markup.");
                foreach (var mark in editor.Marks.Where(mark => !mark.Suggested))
                {
                    var point = DrawingPoint(mark.Start);
                    if (mark.Boundary != null)
                    {
                        for (int i = 0; i < mark.Boundary.Length; i++)
                        {
                            var a = DrawingPoint(mark.Boundary[i]);
                            var b = DrawingPoint(mark.Boundary[(i + 1) % mark.Boundary.Length]);
                            var line = doc.Create.NewDetailCurve(view, Line.CreateBound(a, b));
                            view.SetElementOverrides(line.Id, new OverrideGraphicSettings()
                                .SetProjectionLineColor(new Color(39, 125, 74)).SetProjectionLineWeight(4));
                            Tag(line, schema, mark.Kind.ToString());
                        }
                    }
                    else if (mark.IsWall && mark.End.HasValue)
                    {
                        var end = DrawingPoint(mark.End.Value);
                        var line = doc.Create.NewDetailCurve(view, Line.CreateBound(point, end));
                        var color = mark.Kind == MarkKind.ExteriorWall ? new Color(184, 48, 26) : new Color(13, 110, 166);
                        view.SetElementOverrides(line.Id, new OverrideGraphicSettings()
                            .SetProjectionLineColor(color).SetProjectionLineWeight(5));
                        Tag(line, schema, mark.Kind.ToString());
                    }
                    else
                    {
                        if (mark.End.HasValue)
                            point = (point + DrawingPoint(mark.End.Value)) / 2;
                        var note = TextNote.Create(doc, view.Id, point, mark.Annotation, textType);
                        Tag(note, schema, mark.Kind.ToString());
                    }
                }
                DrawingReviewStore.Save(view, role, type.PageNumber, new DrawingReviewState(editor.Marks.ToArray(), editor.Alignment, role == DrawingRole.Plan),
                    editor.AspectRatio, editor.Approved);
                Commit(tx);
            }
            if (editor.Approved)
                ShowHandoff(view);
            app.ActiveUIDocument.RefreshActiveView();
            return Result.Succeeded;
        }

        private static void ShowHandoff(ViewDrafting view)
        {
            var dialog = new TaskDialog("Drawing page approved")
            {
                MainInstruction = "The reviewed page is ready for MCP.",
                MainContent = DrawingReviewStore.Uri(view) + "\n\nGive this URI to your MCP client, or export the approved JSON.",
                CommonButtons = TaskDialogCommonButtons.Close
            };
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Export approved JSON");
            if (dialog.Show() != TaskDialogResult.CommandLink1)
                return;
            var save = new SaveFileDialog
            {
                Filter = "Approved drawing review (*.json)|*.json",
                DefaultExt = ".json",
                FileName = "approved-drawing-review.json"
            };
            if (save.ShowDialog() == true)
            {
                try
                {
                    File.WriteAllText(save.FileName, DrawingReviewStore.ExportApproved(view));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    TaskDialog.Show("Could not export review", ex.Message);
                }
            }
        }

        private static DrawingMark ReadMarkup(Element element, Schema schema, XYZ origin, XYZ across, XYZ down)
        {
            var entity = element.GetEntity(schema);
            if (!entity.IsValid() || !Enum.TryParse(entity.Get<string>(schema.GetField(ReviewKindField)), out MarkKind kind))
                return null;
            Point Relative(XYZ position)
            {
                var delta = position - origin;
                return new Point(delta.DotProduct(across) / across.DotProduct(across),
                    delta.DotProduct(down) / down.DotProduct(down));
            }
            if (element is DetailCurve curve && curve.GeometryCurve is Line line)
                return new DrawingMark(kind, Relative(line.GetEndPoint(0)), Relative(line.GetEndPoint(1)));
            if (element is TextNote note)
                return new DrawingMark(kind, Relative(note.Coord), SavedAnnotation: note.Text);
            return null;
        }

        private static BitmapSource ReadBitmap(ImageType type, int maxSide = 0)
        {
            using (var bitmap = type.GetImage())
            using (var stream = new MemoryStream())
            {
                if (bitmap == null)
                    throw new InvalidOperationException("Revit could not read the embedded PDF image.");
                bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
                stream.Position = 0;
                var source = new BitmapImage();
                source.BeginInit();
                source.CacheOption = BitmapCacheOption.OnLoad;
                if (maxSide > 0 && Math.Max(bitmap.Width, bitmap.Height) > maxSide)
                {
                    if (bitmap.Width >= bitmap.Height) source.DecodePixelWidth = maxSide;
                    else source.DecodePixelHeight = maxSide;
                }
                source.StreamSource = stream;
                source.EndInit();
                source.Freeze();
                return source;
            }
        }

        private static string ValidateMarkup(IReadOnlyList<DrawingMark> marks, List<(string Name, double Mm)> existing,
            double width, double height, double minimumTrace, bool hasTextType)
        {
            var levels = new List<(string Name, double Mm)>(existing);
            foreach (var mark in marks.Where(mark => !mark.Suggested))
            {
                if (mark.Boundary != null)
                {
                    if (mark.Boundary.Length != 4 || (mark.Boundary.Max(p => p.X) - mark.Boundary.Min(p => p.X)) * width <= minimumTrace
                        || (mark.Boundary.Max(p => p.Y) - mark.Boundary.Min(p => p.Y)) * height <= minimumTrace)
                        return "The floor outline is too small. Move its corner or remove the suggestion.";
                    continue;
                }
                if (mark.IsWall && mark.End.HasValue)
                {
                    var delta = mark.End.Value - mark.Start;
                    var dx = delta.X * width;
                    var dy = delta.Y * height;
                    if (Math.Sqrt(dx * dx + dy * dy) <= minimumTrace)
                        return "A wall trace is too short for Revit. Undo it and trace a longer segment.";
                    continue;
                }
                if (!hasTextType)
                    return "Set a default Revit text note type before saving drawing notes.";
                if (mark.Kind != MarkKind.Level || !mark.ElevationMm.HasValue)
                    continue;

                var mm = mark.ElevationMm.Value;
                var sameName = levels.FirstOrDefault(level => SameName(level.Name, mark.Label));
                if (sameName.Name != null)
                {
                    if (Math.Abs(sameName.Mm - mm) > 1)
                        return $"Level '{mark.Label}' is already listed at a different project elevation.";
                    continue;
                }
                var sameElevation = levels.FirstOrDefault(level => Math.Abs(level.Mm - mm) <= 1);
                if (sameElevation.Name != null)
                    return $"Level '{sameElevation.Name}' is already listed at this elevation. Use its name to reference it.";
                levels.Add((mark.Label, mm));
            }
            return null;
        }

        private static bool SameName(string first, string second) =>
            string.Equals(first, second, StringComparison.OrdinalIgnoreCase);

        private static string AvailableViewName(Document doc, string proposed)
        {
            var names = new HashSet<string>(new FilteredElementCollector(doc).OfClass(typeof(View))
                .Cast<View>().Select(view => view.Name), StringComparer.OrdinalIgnoreCase);
            var result = proposed;
            for (int suffix = 2; names.Contains(result); suffix++)
                result = proposed + " (" + suffix.ToString(CultureInfo.InvariantCulture) + ")";
            return result;
        }

        private static Schema CreateSchema()
        {
            var builder = new SchemaBuilder(ReferenceSchemaId);
            builder.SetSchemaName("RvtMcpDrawingReview");
            builder.SetReadAccessLevel(AccessLevel.Public);
            builder.SetWriteAccessLevel(AccessLevel.Public);
            builder.AddSimpleField(ReviewKindField, typeof(string));
            return builder.Finish();
        }

        private static void Tag(Element element, Schema schema, string kind)
        {
            var entity = new Entity(schema);
            entity.Set(schema.GetField(ReviewKindField), kind);
            element.SetEntity(entity);
        }

        private static void Start(Transaction tx)
        {
            if (tx.Start() != TransactionStatus.Started)
                throw new InvalidOperationException("Revit could not start the drawing review transaction.");
        }

        private static void Commit(Transaction tx)
        {
            if (tx.Commit() != TransactionStatus.Committed)
                throw new InvalidOperationException("Revit did not commit the drawing review transaction.");
        }
    }
}
