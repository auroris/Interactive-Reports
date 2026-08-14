using InteractiveReport.Core.Model;
using InteractiveReport.Core.SavedReports;

namespace InteractiveReport.AspNetCore;

/// <summary>
/// The built-in, administrators-only report over the saved-report store: the admin
/// widget's listing IS a report, riding the ordinary schema/query/export pipeline.
/// Synthesized from the live SavedReports options (never from configuration — the
/// name is reserved), it works because configured documents sync into the same table
/// (ConfiguredReportDocumentSynchronizer) and provenance is a column.
///
/// Per-row actions are action-renderer columns: the SQL computes each button's label,
/// a NULL label renders no button (how configured rows lose Publish/Reassign/Delete),
/// and the hidden ID travels as the action key. The client wrapper listens for the
/// ir-action events these produce.
/// </summary>
internal static class SavedReportsListingDefinition
{
    internal const string Name = "__saved-reports";

    internal static bool Matches(string name)
        => string.Equals(name, Name, StringComparison.OrdinalIgnoreCase);

    internal static ReportDefinition Create(SavedReportsOptions saved)
    {
        var cfg = ServiceCollectionExtensions.ResolveStoreConfig(saved);
        SavedReportStoreConfig.EnsureValidTableName(cfg.TableName);
        return new ReportDefinition
        {
            Name = Name,
            Title = "Saved Reports",
            Connection = cfg.ConnectionName,
            Dialect = cfg.Dialect,
            Sql = Sql(cfg.Dialect, cfg.TableName),
            Authorization = new ReportAuthorization { AdministratorsOnly = true },
            ColumnLabels = new()
            {
                ["ID"] = "Id",
                ["REPORT_NAME"] = "Report",
                ["TITLE"] = "Title",
                ["OWNER"] = "Owner",
                ["SCOPE"] = "Scope",
                ["PRIMARY_STATUS"] = "Primary",
                ["MODIFIED"] = "Modified (UTC)",
                ["ACTION_PUBLISH"] = "Publish",
                ["ACTION_PRIMARY"] = "Primary",
                ["ACTION_REASSIGN"] = "Reassign",
                ["ACTION_STATE"] = "State",
                ["ACTION_DOWNLOAD"] = "Download",
                ["ACTION_DELETE"] = "Delete",
            },
            DefaultState = DefaultState(),
        };
    }

    private static ReportState DefaultState()
    {
        static ColumnFormat Action(string command) => new()
        {
            DisplayAs = "action",
            Command = command,
            KeyColumn = "ID",
        };
        return new ReportState
        {
            Pipeline =
            [
                new PipelineStage
                {
                    Shape = new StageShape { Kind = "source" },
                    Layer = new StageLayer
                    {
                        // ID stays hidden; the action keyColumn carries it in row data.
                        Columns =
                        [
                            "REPORT_NAME", "TITLE", "OWNER", "SCOPE", "PRIMARY_STATUS", "MODIFIED",
                            "ACTION_PUBLISH", "ACTION_PRIMARY", "ACTION_REASSIGN", "ACTION_STATE",
                            "ACTION_DOWNLOAD", "ACTION_DELETE",
                        ],
                        Sorts = [new SortRule { Col = "REPORT_NAME" }, new SortRule { Col = "TITLE" }],
                        Formats = new()
                        {
                            ["ACTION_PUBLISH"] = Action("toggleGlobal"),
                            ["ACTION_PRIMARY"] = Action("togglePrimary"),
                            ["ACTION_REASSIGN"] = Action("reassign"),
                            ["ACTION_STATE"] = Action("openState"),
                            ["ACTION_DOWNLOAD"] = Action("download"),
                            ["ACTION_DELETE"] = Action("delete"),
                        },
                    },
                },
            ],
        };
    }

    /// <summary>
    /// One plain SELECT per dialect. Values are pre-rendered text: SCOPE and the
    /// action labels are CASE expressions over ORIGIN/IS_GLOBAL/IS_PRIMARY, and MODIFIED trims
    /// the ISO-8601 "o" text to sortable "yyyy-MM-dd HH:mm". No ORDER BY (sorting
    /// belongs to report state); no bracket/brace characters (SqlKata rewrites them
    /// even inside raw SQL). Postgres quotes every identifier because the store's
    /// DDL created quoted-uppercase names.
    /// </summary>
    private static string Sql(ReportDialect dialect, string table) => dialect switch
    {
        ReportDialect.Sqlite or ReportDialect.Oracle => $"""
            SELECT ID, REPORT_NAME, TITLE, OWNER, IS_GLOBAL, IS_PRIMARY,
                CASE WHEN ORIGIN = 'configured' THEN 'Read only'
                     WHEN IS_GLOBAL = 1 THEN 'Global' ELSE 'Private' END AS "SCOPE",
                CASE WHEN IS_PRIMARY = 1 THEN 'Yes' ELSE 'No' END AS PRIMARY_STATUS,
                REPLACE(SUBSTR(MODIFIED_UTC, 1, 16), 'T', ' ') AS MODIFIED,
                CASE WHEN ORIGIN = 'configured' THEN NULL
                     WHEN IS_GLOBAL = 1 THEN 'Unpublish' ELSE 'Publish' END AS ACTION_PUBLISH,
                CASE WHEN IS_PRIMARY = 1 THEN 'Unflag' ELSE 'Make primary' END AS ACTION_PRIMARY,
                CASE WHEN ORIGIN = 'configured' THEN NULL ELSE 'Reassign' END AS ACTION_REASSIGN,
                'State' AS ACTION_STATE,
                'Download' AS ACTION_DOWNLOAD,
                CASE WHEN ORIGIN = 'configured' THEN NULL ELSE 'Delete' END AS ACTION_DELETE
            FROM {table}
            """,
        ReportDialect.SqlServer => $"""
            SELECT ID, REPORT_NAME, TITLE, OWNER, IS_GLOBAL, IS_PRIMARY,
                CASE WHEN ORIGIN = N'configured' THEN N'Read only'
                     WHEN IS_GLOBAL = 1 THEN N'Global' ELSE N'Private' END AS SCOPE,
                CASE WHEN IS_PRIMARY = 1 THEN N'Yes' ELSE N'No' END AS PRIMARY_STATUS,
                REPLACE(SUBSTRING(MODIFIED_UTC, 1, 16), N'T', N' ') AS MODIFIED,
                CASE WHEN ORIGIN = N'configured' THEN NULL
                     WHEN IS_GLOBAL = 1 THEN N'Unpublish' ELSE N'Publish' END AS ACTION_PUBLISH,
                CASE WHEN IS_PRIMARY = 1 THEN N'Unflag' ELSE N'Make primary' END AS ACTION_PRIMARY,
                CASE WHEN ORIGIN = N'configured' THEN NULL ELSE N'Reassign' END AS ACTION_REASSIGN,
                N'State' AS ACTION_STATE,
                N'Download' AS ACTION_DOWNLOAD,
                CASE WHEN ORIGIN = N'configured' THEN NULL ELSE N'Delete' END AS ACTION_DELETE
            FROM {table}
            """,
        ReportDialect.Postgres => $"""
            SELECT "ID", "REPORT_NAME", "TITLE", "OWNER", "IS_GLOBAL", "IS_PRIMARY",
                CASE WHEN "ORIGIN" = 'configured' THEN 'Read only'
                     WHEN "IS_GLOBAL" = 1 THEN 'Global' ELSE 'Private' END AS "SCOPE",
                CASE WHEN "IS_PRIMARY" = 1 THEN 'Yes' ELSE 'No' END AS "PRIMARY_STATUS",
                REPLACE(SUBSTR("MODIFIED_UTC", 1, 16), 'T', ' ') AS "MODIFIED",
                CASE WHEN "ORIGIN" = 'configured' THEN NULL
                     WHEN "IS_GLOBAL" = 1 THEN 'Unpublish' ELSE 'Publish' END AS "ACTION_PUBLISH",
                CASE WHEN "IS_PRIMARY" = 1 THEN 'Unflag' ELSE 'Make primary' END AS "ACTION_PRIMARY",
                CASE WHEN "ORIGIN" = 'configured' THEN NULL ELSE 'Reassign' END AS "ACTION_REASSIGN",
                'State' AS "ACTION_STATE",
                'Download' AS "ACTION_DOWNLOAD",
                CASE WHEN "ORIGIN" = 'configured' THEN NULL ELSE 'Delete' END AS "ACTION_DELETE"
            FROM "{table}"
            """,
        _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, null),
    };
}
