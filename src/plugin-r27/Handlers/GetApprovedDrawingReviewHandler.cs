using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin.Views.DrawingReview;

namespace RvtMcp.Plugin.Handlers
{
    internal sealed class GetApprovedDrawingReviewHandler : IRevitCommand
    {
        public string Name => "get_approved_drawing_review";
        public string Description => "Read the approved PDF review for one drafting view in the active document.";
        public string ParametersSchema => "{\"type\":\"object\",\"required\":[\"viewKey\",\"offset\"],\"properties\":{\"viewKey\":{\"type\":\"string\"},\"offset\":{\"type\":\"integer\",\"minimum\":0}}}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var document = app.ActiveUIDocument?.Document;
            if (document == null) return CommandResult.Fail("No document is open.");

            var request = JObject.Parse(paramsJson);
            var key = request.Value<string>("viewKey");
            int? offset = request.Value<int?>("offset");
            if (string.IsNullOrWhiteSpace(key) || key.Length > 80 || offset is null or < 0)
                return CommandResult.Fail("A drafting view key and nonnegative offset are required.");
            if (document.GetElement(key) is not ViewDrafting view)
                return CommandResult.Fail("The drafting view was not found in the active document.");
            var approved = DrawingReviewStore.ReadApproved(view, offset.Value);
            return approved == null
                ? CommandResult.Fail("This view has no approved drawing review. Approve it in Revit before modeling.")
                : CommandResult.Ok(approved);
        }
    }
}
