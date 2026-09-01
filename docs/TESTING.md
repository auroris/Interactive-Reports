# Testing

## Everyday suite

```sh
dotnet test
npm test
```

The Core and ASP.NET test projects run with zero infrastructure (SQLite in-memory).
Coverage: canonical planner/lowerer/terminal goldens ×4 dialects (exact SQL and ordered
bindings), the row-condition corpus × dialect matrix, expression
parser/emitter, the shared computed/filter/highlight rule plan (including disabled-rule
elision), validator rules, SQL-safety corpus, saved-report store, CSV writer, and
end-to-end engine passes. The ASP.NET suite also starts a real local server over a clean
file-backed SQLite report database. It obtains the synthetic default report document
from the schema endpoint, queries it, adopts the server-enriched document returned
beside the data, and saves that document under a random title,
restarts the server, and loads it again. It exercises an explicitly configured SQLite
connection string first, then points saved-report persistence at a registered report
database and repeats the restart/load check. A separate HTTP test proves that a
report-only host creates no persistence directory, database, or table and that direct
persistence and administration requests fail cleanly until storage is configured.

`npm test` builds the packaged client and runs the fast DOM-level unit suite with
Node.js and happy-dom.

## Browser UI automation

Playwright is configured to run Chromium against the real Workbench host. Install the
browser once, then run the browser suite:

```sh
npx playwright install chromium
npm run test:ui
```

The configuration starts `samples/Workbench` on `http://127.0.0.1:5042`, reuses a
compatible local server outside CI, and retains traces and screenshots on failure. The
full-application suite covers the synthetic default report, live querying, search,
paging, report-attribute changes, CSV download, aggregate chart rendering, saved-report
save/reload across a page refresh, the administration lifecycle, and non-administrator
authorization. Saved-report test data uses random names and is removed even when an
assertion fails.

## Live-dialect verification (M5)

`InteractiveReport.Live.Tests` runs the engine corpus against **real SQL Server, Oracle,
and PostgreSQL**. Each test skips (never fails) unless the matching environment variable
holds a connection string:

| Variable | Target | Example |
|---|---|---|
| `IR_TEST_SQLSERVER` | SQL Server 2019+ | `Server=vm-host;Database=irtest;User Id=irtest;Password=***;TrustServerCertificate=True` |
| `IR_TEST_ORACLE` | Oracle 19c+/XE 21c | `User Id=irtest;Password=***;Data Source=vm-host:1521/XEPDB1` |
| `IR_TEST_POSTGRES` | PostgreSQL 14+ | `Host=vm-host;Port=5432;Database=irtest;Username=irtest;Password=***` |

Run just the battery:

```bash
dotnet test tests/InteractiveReport.Live.Tests
```

**What it does:** on first use per run it **drops and recreates a table named
`IR_TEST_ORDERS`** in each target database and seeds the canonical 10 rows, then runs
expression filters and highlights/search/explicit null-or-empty conditions/aggregates/breaks/computed columns (including CASE,
date-part extraction over native and ISO-text dates, and the date vocabulary —
NOW/TO_DATE/DATE_TRUNC/TO_STRING, whole-day arithmetic, and BETWEEN), request-scoped
UTC `NOW()` parity, definition `TimeZone` session pinning for developer SQL and native
database conversions, context-param
binding/groupBy/pivot/export against it.

Text comparisons and sorts use the target database's configured collation. The live
battery checks deterministic cases, but exact cross-dialect text ordering is portable
only when each test database uses a binary/ordinal collation.

The server-level persistence scenario also runs against every configured live dialect.
For each database it starts the ASP.NET host over `SELECT * FROM IR_TEST_ORDERS`, obtains
the application's synthetic `defaultState`, queries it, saves the returned document with
its refreshed table-schema caches under a random title, restarts, and loads it. The first pass proves that the default store is a
separate local SQLite file and does not create a persistence table in the report
database. The second pass configures a random saved-report table in the report database
and repeats the restart/load check. Random live tables are removed after the test.

Finally, the complete saved-report create/list/update/delete contract runs against SQL
Server, Oracle, and PostgreSQL in a dedicated `IR_SAVED_REPORTS_TEST` table. Point the
environment variables at scratch databases, not anything you care about.

Expected numbers are identical on every dialect by design, including the explicit
`NOTES IS NULL OR NOTES = ''` condition (4): SQLite/SQL Server/PostgreSQL count three
NULLs plus one empty string, while Oracle stores the empty string as a fourth NULL.

Two dialect-specific boolean tests pin opposite emissions of the same expression:
`CASE WHEN LARGE_FLAG THEN …` lowers to `= 1` on SQL Server (bit is a value, not a
condition) and emits the column bare on PostgreSQL (boolean IS a condition; `= 1`
would be a type error).

PostgreSQL note: the battery creates `IR_TEST_ORDERS` unquoted, so Postgres folds the
names to lowercase. That is deliberate — it proves the engine's case-insensitive
schema matching and response dictionaries absorb identifier folding with no
special-casing.

### SQL Server VM setup (once)

1. Install SQL Server (Developer edition is free) with mixed-mode auth, or enable it
   after install. Enable TCP/IP in Configuration Manager; default port 1433; allow it
   through the VM firewall.
2. Create the scratch database and login (run in `sqlcmd` or SSMS as sa):

```sql
CREATE DATABASE irtest;
CREATE LOGIN irtest WITH PASSWORD = 'YourStrongPassword1!';
USE irtest;
CREATE USER irtest FOR LOGIN irtest;
ALTER ROLE db_owner ADD MEMBER irtest;   -- test user seeds tables; prod guidance differs
USE master;
ALTER DATABASE irtest SET ALLOW_SNAPSHOT_ISOLATION ON;
```

The last statement enables the live snapshot-concurrency case. The test skips that one
case, with the same command in its reason, when the database retains SQL Server's default
`OFF` setting. `READ_COMMITTED_SNAPSHOT` is not required because the report engine
explicitly opens a `SNAPSHOT` transaction. Enabling the option can wait for existing
update transactions to finish; verify completion with:

```sql
SELECT snapshot_isolation_state_desc
FROM sys.databases
WHERE name = N'irtest';  -- must be ON, not IN_TRANSITION_TO_ON
```

3. `TrustServerCertificate=True` in the connection string avoids TLS trust errors with
   the default self-signed certificate — fine for a test VM.

### Oracle VM setup (once)

1. Install Oracle Database XE 21c (free). The default pluggable database service is
   `XEPDB1`; the listener is on 1521 — allow it through the VM firewall.
2. Create the test user (run in `sqlplus system/...@//localhost:1521/XEPDB1`):

```sql
CREATE USER irtest IDENTIFIED BY "YourStrongPassword1!";
GRANT CREATE SESSION, CREATE TABLE, CREATE SEQUENCE TO irtest;
ALTER USER irtest QUOTA UNLIMITED ON USERS;
```

   `CREATE SEQUENCE` backs the identity column of the saved-report table; without it the
   store's auto-create fails with ORA-01031 and every Oracle saved-report test fails.

3. The battery connects as `irtest` and works in that user's own schema.

### PostgreSQL VM setup (once)

1. Install PostgreSQL 14+ ; ensure `listen_addresses` covers the host-only interface
   (`postgresql.conf`), add a `pg_hba.conf` rule for the host network
   (`host all all 192.168.56.0/24 scram-sha-256`), and allow TCP 5432 through the VM
   firewall.
2. Create the scratch database and login (run in `psql` as postgres):

```sql
CREATE ROLE irtest LOGIN PASSWORD 'YourStrongPassword1!';
CREATE DATABASE irtest OWNER irtest;
```

3. The battery connects as `irtest` and works in that database's public schema.

### Note on the read-only-principal guidance

The architecture recommends pointing *report* connections at a read-only principal
(§11). The **test** user deliberately violates that — it needs DDL to seed
`IR_TEST_ORDERS`. Don't reuse it as a production report principal.

## PowerShell one-liners for the VM session

```powershell
$env:IR_TEST_SQLSERVER = "Server=vm-host;Database=irtest;User Id=irtest;Password=***;TrustServerCertificate=True"
$env:IR_TEST_ORACLE    = "User Id=irtest;Password=***;Data Source=vm-host:1521/XEPDB1"
$env:IR_TEST_POSTGRES  = "Host=vm-host;Port=5432;Database=irtest;Username=irtest;Password=***"
dotnet test tests/InteractiveReport.Live.Tests -v normal
```

Every live entry flips from Skipped to Passed per variable that is set. Alternatively,
run `run-live-tests.ps1` at the repo root; it sets all three for the dev VM, probes the
ports first, and includes the PostgreSQL saved-report corpus.
