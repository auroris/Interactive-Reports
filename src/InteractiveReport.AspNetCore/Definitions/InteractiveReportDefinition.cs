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

    /// <summary>Gets the saved-report identifier to create or update.</summary>
    public required long Id { get; init; }

    /// <summary>Gets the configured report definition this saved report belongs to.</summary>
    public required string ReportName { get; init; }

    /// <summary>Gets or sets the saved report's display title.</summary>
    public required string Title { get; set; }

    /// <summary>
    /// Gets or sets whether the report has ordinary public/global publication. This property is
    /// named <c>isGlobal</c> in the HTTP JSON contract.
    /// </summary>
    [JsonPropertyName("isGlobal")]
    public bool Public { get; set; }

    /// <summary>
    /// Gets or sets whether the saved report is the report family's default. This is named
    /// <c>isDefault</c> in the HTTP JSON contract.
    /// </summary>
    [JsonPropertyName("isDefault")]
    public bool Default { get; set; }

    /// <summary>Gets or sets the canonical owner identity to persist.</summary>
    public string? Owner { get; set; }

    /// <summary>
    /// Gets or sets the typed client-authored report state. On update this is null when the request did
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
    /// Gets whether this request will replace the stored state. <see langword="false"/> means an update
    /// keeps the existing state JSON untouched.
    /// </summary>
    [JsonIgnore]
    public bool StateChanged { get; private set; }
}
