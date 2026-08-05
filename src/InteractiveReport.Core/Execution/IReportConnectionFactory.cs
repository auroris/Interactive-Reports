using System.Data.Common;

namespace InteractiveReport.Core.Execution;

/// <summary>
/// Host-registered mapping from a definition's named connection to an unopened
/// DbConnection. Hosts should point report connections at a read-only database
/// principal: the engine only ever SELECTs, but the principal makes that a
/// guarantee rather than a habit.
/// </summary>
public interface IReportConnectionFactory
{
    DbConnection CreateConnection(string name);
}
