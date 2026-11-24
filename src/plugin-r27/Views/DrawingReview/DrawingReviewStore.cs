using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Point = System.Windows.Point;

namespace RvtMcp.Plugin.Views.DrawingReview
{
    internal sealed record DrawingReviewState(DrawingMark[] Marks, PlanAlignment Alignment, bool AutoPlanAnalyzed = false);

    internal static class DrawingReviewStore
    {
        private static readonly Guid SchemaId = new("ed14ce2d-2781-424f-a9c7-becc303daef4");
        private const string Draft = "Draft";
        private const string Approved = "Approved";

        public static DrawingReviewState ReadDraft(View view)
        {
            var draft = Read(view, Draft);
            return string.IsNullOrEmpty(draft) ? null : JsonConvert.DeserializeObject<DrawingReviewState>(draft);
        }

        public static bool IsApproved(View view) => !string.IsNullOrEmpty(Read(view, Approved));

        public static string Uri(View view) => $"revit://drawing-review/{view.UniqueId}/0";

        public static string ExportApproved(View view) => Read(view, Approved);

        public static IEnumerable<ConstructionChoice> ApprovedConstructionTypes(Document project, ElementId currentView)
        {
            foreach (var view in new FilteredElementCollector(project).OfClass(typeof(View)).OfType<ViewDrafting>())
            {
                if (view.Id == currentView) continue;
                string approved = Read(view, Approved);
                if (string.IsNullOrEmpty(approved)) continue;
                var data = JObject.Parse(approved);
                var source = new ConstructionSource(view.UniqueId, view.Name, data.Value<int>("page"));
                foreach (var mark in (JArray)data["marks"])
                    if (mark["construction"] is JObject spec)
                        yield return new ConstructionChoice(spec.ToObject<ConstructionSpec>(), source);
            }
        }

        public static JObject ReadApproved(View view, int offset)
        {
            var json = Read(view, Approved);
            if (string.IsNullOrEmpty(json)) return null;
            var result = JObject.Parse(json);
            var marks = (JArray)result["marks"];
            const int pageSize = 25;
            result["marks"] = new JArray(marks.Skip(offset).Take(pageSize));
            result["markCount"] = marks.Count;
            result["nextOffset"] = offset < marks.Count - pageSize ? new JValue(offset + pageSize) : JValue.CreateNull();
            return result;
        }

        private static string Read(View view, string field)
        {
            var schema = Schema.Lookup(SchemaId);
            if (schema == null) return null;
            var entity = view.GetEntity(schema);
            return entity.IsValid() ? entity.Get<string>(schema.GetField(field)) : null;
        }

        public static void Save(View view, DrawingRole role, int page, DrawingReviewState state, double aspect, bool approve)
        {
            var schema = Schema.Lookup(SchemaId) ?? CreateSchema();
            var entity = new Entity(schema);
            entity.Set(schema.GetField(Draft), JsonConvert.SerializeObject(state));
            entity.Set(schema.GetField(Approved), approve ? ApprovedJson(view, role, page, state, aspect) : "");
            view.SetEntity(entity);
        }

        private static string ApprovedJson(View view, DrawingRole role, int page, DrawingReviewState state, double aspect)
        {
            static object PagePoint(Point p) => new { x = p.X, y = p.Y };
            object ProjectPoint(Point p)
            {
                if (state.Alignment == null) return null;
                var xy = state.Alignment.ToProject(p, aspect);
                return new { x = Math.Round(xy.X, 2), y = Math.Round(xy.Y, 2) };
            }
            static object ModelType(ConstructionSpec spec) => spec == null ? null : new
            {
                category = spec.Category.ToString(),
                code = spec.Code,
                title = spec.Title,
                totalThicknessMm = spec.TotalThicknessMm,
                layers = spec.Layers.Select(layer => new { material = layer.Material, thicknessMm = layer.ThicknessMm }),
                evidence = spec.Evidence
            };

            return JsonConvert.SerializeObject(new
            {
                formatVersion = 3,
                approvedAtUtc = DateTimeOffset.UtcNow,
                document = view.Document.Title,
                view = view.Name,
                viewKey = view.UniqueId,
                drawingRole = role.ToString(),
                page,
                planLevel = state.Alignment?.Level,
                projectUnits = "mm",
                instructions = "Use approved marks only. Construction definitions and assigned types record drawing evidence; use approved project coordinates for placement. FloorFootprint is a closed centerline polygon. Confirm slab offsets, openings and level before creating a floor. Verify compatible Revit types, materials, missing layers, dimensions and opening hosts. A door or window span may bridge two wall segments; establish a continuous host. Page coordinates start at the upper-left.",
                marks = state.Marks.Select((mark, index) => new
                {
                    id = index + 1,
                    kind = mark.Kind.ToString(),
                    label = mark.Construction?.Caption ?? mark.Label ?? mark.SavedAnnotation,
                    construction = ModelType(mark.Construction),
                    assignedType = mark.AssignedType == null ? null : new
                    {
                        spec = ModelType(mark.AssignedType.Spec),
                        source = new { viewKey = mark.AssignedType.Source.ViewKey, viewName = mark.AssignedType.Source.ViewName, page = mark.AssignedType.Source.Page }
                    },
                    projectElevationMm = mark.ElevationMm,
                    pageStart = PagePoint(mark.Start),
                    pageEnd = mark.End.HasValue ? PagePoint(mark.End.Value) : null,
                    projectStart = mark.Kind == MarkKind.Construction ? null : ProjectPoint(mark.Start),
                    projectEnd = mark.End.HasValue ? ProjectPoint(mark.End.Value) : null,
                    pageBoundary = mark.Boundary?.Select(PagePoint),
                    projectBoundary = mark.Boundary?.Select(ProjectPoint),
                    boundaryClosed = mark.Boundary != null ? true : (bool?)null,
                    spanMm = mark.End.HasValue && state.Alignment != null
                        ? Math.Round((state.Alignment.ToProject(mark.End.Value, aspect) - state.Alignment.ToProject(mark.Start, aspect)).Length, 2)
                        : (double?)null
                })
            });
        }

        private static Schema CreateSchema()
        {
            var builder = new SchemaBuilder(SchemaId);
            builder.SetSchemaName("RvtMcpDrawingReviewState");
            builder.SetReadAccessLevel(AccessLevel.Public);
            builder.SetWriteAccessLevel(AccessLevel.Public);
            builder.AddSimpleField(Draft, typeof(string));
            builder.AddSimpleField(Approved, typeof(string));
            return builder.Finish();
        }
    }
}
