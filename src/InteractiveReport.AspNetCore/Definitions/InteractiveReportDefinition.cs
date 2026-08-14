using System.Text.Json.Serialization;
using InteractiveReport.Core.Model;

namespace InteractiveReport.AspNetCore.Definitions;

/// <summary>
/// A mutable, typed saved-report definition assembled from client-authored JSON before
/// authorization and persistence. Authorization code may inspect or narrow this
/// definition. The values present after authorization are the values the server
/// validates and stores.
/// </summary>
/// <remarks>
/// For an update, metadata contains the effective values after applying the submitted
/// patch to the current saved-report metadata. <see cref="State"/> is populated only
/// when the client submitted replacement state, so the server never has to deserialize
/// an existing stored state document merely to authorize an update.
/// </remarks>
public sealed class InteractiveReportDefinition
{
    private ReportState? _state;

    /// <summary>The saved-report id that will be created or updated.</summary>
    public required string Id { get; init; }

    /// <summary>The report/data-source definition this saved report belongs to.</summary>
    public required string ReportName { get; init; }

    /// <summary>The saved report's display title.</summary>
    public required string Title { get; set; }

    /// <summary>
    /// Whether the report has ordinary public/global publication. A primary report is
    /// also broadly visible through its separate flag. This property is named
    /// <c>isGlobal</c> in the HTTP JSON contract.
    /// </summary>
    [JsonPropertyName("isGlobal")]
    public bool Public { get; set; }

    /// <summary>
    /// Whether the saved report is a primary report. This is named <c>isPrimary</c> in
    /// the HTTP JSON contract.
    /// </summary>
    [JsonPropertyName("isPrimary")]
    public bool Primary { get; set; }

    /// <summary>The canonical owner identity that will be stored.</summary>
    public string? Owner { get; set; }

    /// <summary>
    /// Typed client-authored report state. On update this is null when the request did
    /// not replace state. Assigning it marks the state as changed.
    /// </summary>
    public ReportState? State
    {
        get => _state;
        set
        {
            _state = value;
            StateChanged = true;
        }
    }

    /// <summary>
    /// True when this request will replace the stored state. False means an update
    /// keeps the existing state JSON untouched.
    /// </summary>
    [JsonIgnore]
    public bool StateChanged { get; private set; }
}
