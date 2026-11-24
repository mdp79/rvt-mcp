# PDF Drawing Review in Revit 2027

Open an editable Revit project and choose **Add-Ins → RvtMcp → Drawing Review**. Open the PDF, browse its pages, and use **Keep or update page** on each relevant page. Give it a reference name and choose **Plan**, **Elevation**, or **Section**. **Import and analyze** embeds the selected pages and starts the visual analysis for the selected plans, elevations, and sections. The review editor opens automatically for the first plan or, when no plan is selected, the first reference page. Find the other views in the Project Browser.

## Review the drawings

Open **Drawing Review → Review this PDF page** from each reference view.

- Plans already have a first pass of editable geometry on opening. The add-in looks for outlined or filled walls, framed gaps, and door swing patterns. Exterior wall candidates are red, interior wall candidates are blue, doors are orange, and windows are teal. Choose **Refine in area** and click opposite corners around the plan to replace pending geometry when the sheet contains other views or schedules. **Scan whole page** is also available on reference pages imported before this update.
- A clear, closed rectangular wall perimeter produces a green **Floor footprint** suggestion along the wall centerlines. Click **Move or redraw**, then pick a corner and its new location to resize it. Use **Assign construction type** to link a reviewed floor or slab type. Check offsets and any slab openings against the drawings before using the footprint in Revit.
- The add-in also suggests recognizable door and window IDs and signed elevation labels where it can read PDF text. Elevation and section pages let you record project levels, heights, openings, and construction details. Edit printed level values to match the Revit project datum before you approve them.
- Select candidates in the list and **Accept selected**, **Remove selected**, **Edit details**, or **Move or redraw**. You can add manual marks for anything the detector missed. Dashed lines show unreviewed candidates. Use Ctrl or Shift to select several candidates in the list.
- Choose **Align plan** and click two points whose positions you know in the Revit project coordinate system. Enter both sets of X/Y coordinates in millimetres, pick or name the Revit level, then choose **Save alignment**. The drawing shows anchors A and B and the status reads **Alignment saved**. Saving alignment persists those choices on this plan even if you close the editor without saving the other edits. Use points far enough apart to establish scale and direction. Changing an approved plan's alignment requires approving its updated coordinates again.
- **Save draft** retains the review in the project and revokes any previous MCP approval of that page. **Approve for MCP** requires a decision on every suggestion and an alignment for plans. It saves the reviewed data and shows the resource URI. Revit Undo reverses the save.

Detection works from visible plan linework, including clear scans, but is tuned for straight horizontal and vertical wall outlines. It can miss or misclassify walls and openings, especially on noisy scans or drawings with other line patterns. PDF text suggestions use embedded text and do not OCR scans. Verify important heights, material layers, and opening dimensions against the source.

## Review and reuse construction types

When you import pages, Drawing Review looks for Danish and English construction notes for exterior and interior walls, floors, slabs on grade, roofs, foundations, beams, and columns. It suggests a construction type when it recognizes a heading or type code. When adjacent notes list materials and thicknesses, they are shown in drawing order with the source text. Missing layers and total thickness remain blank. Import a page again if it was added to the project before construction detection was available.

1. Open the reference page and select a construction suggestion. Use **Edit details** to verify the category, code, total thickness, and layers against the drawing. Approve the page to make its reviewed types available on other plans.
2. On a plan, inspect the detected geometry and use **Align plan**. **Find wall types** matches detected wall traces against reviewed types, based on an exact code on the mark or a unique match to the stated total thickness. If more than one type fits, select the wall traces and choose **Assign construction type**. These actions reuse the traces the add-in found in the plan.
3. Review and approve the plan. MCP receives construction definitions and approved wall type assignments, including a reference to the source view and PDF page. Approve a new version of the plan if you change a construction definition used on it.

Construction definitions for roofs, foundations, beams, and columns capture what the PDF says about the type. Their placement and extents still need project evidence. Floors without a clear rectangular perimeter also need further geometry. A scanned PDF without a text layer can supply plan linework, but the add-in cannot read its construction notes.

## Automatic visual review

**Analyze selected drawings with Codex** is enabled by default on import. **Import and analyze** sends the selected page images to the signed-in Codex model. Each selected drawing gets its own review. Plans can also use up to the first four selected elevations or sections as visual references. Uncheck the option to import using only the local detector.

- **Plans** get proposed wall, door, and window geometry from visible plan linework.
- **Elevations** get editable reference notes for visible openings, sill/head heights, and height or roof datums.
- **Sections** get editable notes for level and roof relationships and documented wall, floor, roof, or foundation assemblies. Rotate and crop detection can read a sideways scan and place the notes back on the original page.

Height proposals quote drawing values when legible. A review mark does not assign a Revit elevation automatically. Use **Edit details** to convert a reference into a project level only after you have checked its datum.

The add-in includes the agent script. Once per Windows installation, install Python 3.10 or later and a current, signed-in [Codex CLI](https://developers.openai.com/codex/cli). Install Pillow once with `py -m pip install 'Pillow>=10.1'`. Reopen Revit if you just installed Python or Codex.

The progress window lists the page being analyzed. **Stop analysis** keeps imported pages and completed suggestions. Failed or stopped pages can be retried with **Analyze with Codex** in the page's Drawing Review editor. A retry analyzes the current page alone, replaces its pending agent notes or plan traces, and preserves reviewed marks.

The first successfully analyzed drawing opens automatically. Select a proposed wall, door or window and choose **Edit details** to see the evidence. Use **Move or redraw** to adjust endpoints before approval. Analysis saves draft suggestions and does not create model elements.

## Hand reviewed pages to MCP

Approval publishes the reviewed data for the page. It creates drafting annotations and does not start a modeling operation. Give the approved resource URI to your MCP client and ask it to model the approved scope. The client must first bind to the intended Revit project using its normal target workflow. If it can read MCP resources, it can fetch the URI shown at approval or the URI returned by `revit_get_current_view_info` while the approved view is active. Follow `nextOffset` until all marks have been read. The **Export approved JSON** option works with clients that read local files instead.

Confirm the model's level, family types, heights, and opening hosts before placing elements. Plan marks include project XY in millimetres, and openings drawn as spans also include a span length. Elevation and section marks serve as references. A previously exported JSON remains a snapshot even if you later save a draft or approve a replacement. Export the current approval when handing off through files.

## Build and verify

Close Revit 2027. Run the following from the repository root on Windows with the .NET 10 SDK:

```powershell
dotnet build .\src\plugin-r27\RvtMcp.Plugin.R27.csproj -c Release
dotnet publish .\src\server\RvtMcp.Server.csproj -c Release -r win-x64 --self-contained true '-p:PublishSingleFile=true' -o .\publish\drawing-review-mcp
```

The first command installs the add-in for your Windows user. The second command builds the gateway that exposes the approved review resource. For that resource, point your MCP client at `publish\drawing-review-mcp\RvtMcp.Server.exe` and reconnect. The JSON export path also works with an older gateway.

In a Revit test project, import a multipage PDF containing a plan and an elevation or section. Check the selected pages and marks, adjust the plan alignment, and save a draft. Verify that the resource rejects the draft. Then approve the page and confirm that returned marks and coordinates match the review. Save a revised draft and check that the prior resource is unavailable. Verify Revit Undo restores the prior state.
