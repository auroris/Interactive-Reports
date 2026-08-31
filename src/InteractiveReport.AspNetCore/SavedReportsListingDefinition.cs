using InteractiveReport.Core.Model;
using InteractiveReport.Core.SavedReports;

namespace InteractiveReport.AspNetCore;

/// <summary>
/// Defines the built-in, administrators-only report over the saved-report store. The admin
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
    /// <summary>The reserved configured-report name used to route the synthetic listing.</summary>
    internal const string Name = "__saved-reports";

    /// <summary>
    /// Determines whether a route name selects the synthetic saved-reports listing.
    /// </summary>
    /// <param name="name">The case-insensitive report route name.</param>
    /// <returns><see langword="true"/> when the name identifies the synthetic saved-reports listing; otherwise, <see langword="false"/>.</returns>
    internal static bool Matches(string name)
        => string.Equals(name, Name, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Creates the synthetic report definition that exposes saved-report metadata as a regular report.
    /// </summary>
    /// <param name="cfg">The validated saved-report store configuration.</param>
    /// <returns>An administrators-only definition targeting the configured persistence table.</returns>
    internal static ReportDefinition Create(SavedReportStoreConfig cfg)
    {
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

    /// <summary>
    /// Creates the default report state for the saved-reports listing.
    /// </summary>
    /// <returns>A listing state that selects metadata, sorts by report and title, and configures action renderers.</returns>
    private static ReportState DefaultState()
    {
        // Accepts an action command and returns the renderer configuration for that command.
        // The returned format always uses the listing row's ID as its action key.
        static ColumnFormat Action(string command) => new()
        {
            DisplayAs = "action",
            Command = command,
            KeyColumn = "ID",
        };
        return new ReportState
        {
            ActiveTable = "listing",
            Tables = new(StringComparer.OrdinalIgnoreCase)
            {
                ["listing"] = new ReportTable
                {
                    From = "definition",
                    Composables =
                    [
                        new TableComposable
                        {
                            Kind = "select",
                            // ID stays hidden; the action keyColumn carries it in row data.
                            Columns =
                            [
                                "REPORT_NAME", "TITLE", "OWNER", "SCOPE", "PRIMARY_STATUS", "MODIFIED",
                                "ACTION_PUBLISH", "ACTION_PRIMARY", "ACTION_REASSIGN", "ACTION_STATE",
                                "ACTION_DOWNLOAD", "ACTION_DELETE",
                            ],
                        },
                        new TableComposable
                        {
                            Kind = "sort",
                            Sorts = [new SortRule { Col = "REPORT_NAME" }, new SortRule { Col = "TITLE" }],
                        },
                        new TableComposable
                        {
                            Kind = "formats",
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
                    ],
                },
            },
        };
    }

    /// <summary>
    /// Returns one plain SELECT per dialect. Values are pre-rendered text: SCOPE and the action labels are CASE
    /// expressions over ORIGIN/IS_GLOBAL/IS_PRIMARY, and MODIFIED trims the ISO-8601 "o" text to sortable
    /// "yyyy-MM-dd HH:mm". No ORDER BY (sorting belongs to report state); no bracket/brace characters
    /// (SqlKata rewrites them even inside raw SQL). PostgreSQL quotes every identifier because the store's DDL
    /// created quoted-uppercase names.
    /// </summary>
    /// <param name="dialect">The database dialect whose SQL rules apply.</param>
    /// <param name="table">The validated effective persistence table name.</param>
    /// <returns>The generated SQL text.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="dialect"/> is not supported.</exception>
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
