using System.Data.Common;

namespace InteractiveReport.Core.Execution;

/// <summary>
/// Maps a definition's named connection to an unopened
/// DbConnection. Hosts should point report connections at a read-only database
/// principal: the engine only ever SELECTs, but the principal makes that a
/// guarantee rather than a habit.
/// </summary>
public interface IReportConnectionFactory
{
    /// <summary>
    /// Creates an unopened database connection for one report execution.
    /// </summary>
    /// <param name="name">The registered connection name from the resolved report definition.</param>
    /// <returns>A new unopened connection owned by the caller.</returns>
    DbConnection CreateConnection(string name);
}
