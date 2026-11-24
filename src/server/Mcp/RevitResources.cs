using System.ComponentModel;
using System.Threading.Tasks;
using ModelContextProtocol.Server;
using Newtonsoft.Json;
using RvtMcp.Server.Memory;

namespace RvtMcp.Server
{
    [McpServerResourceType]
    public class RevitResources
    {
        internal static SessionContext Session { get; set; }

        [McpServerResource(UriTemplate = "revit://session/context", Name = "Session Context", MimeType = "application/json")]
        [Description("Current MCP session context: tool call history, success rates, usage patterns, and flags. Use this to understand what has been done in this session.")]
        public static string GetSessionContext()
        {
            return Session?.GetSummary() ?? "{\"status\": \"no session\"}";
        }

        [McpServerResource(UriTemplate = "revit://drawing-review/{viewKey}/{offset}", Name = "Approved Drawing Review", MimeType = "application/json")]
        [Description("Read an approved PDF review in the bound Revit 2027 document. Use the URI shown by Drawing Review or get_current_view_info; continue with nextOffset if present. Drafts are unavailable.")]
        public static async Task<string> GetApprovedDrawingReview(string viewKey, int offset)
        {
            if (string.IsNullOrWhiteSpace(viewKey) || viewKey.Length > 80 || offset < 0)
                return "{\"error\":\"Invalid drawing review view key or offset.\"}";
            try
            {
                var result = await ToolGateway.SendToRevit("get_approved_drawing_review", new { viewKey, offset });
                return result.ToString(Formatting.None);
            }
            catch (System.Exception ex)
            {
                return JsonConvert.SerializeObject(new { error = ex.Message });
            }
        }
    }
}
