# Testing

## Everyday suite

```
dotnet test
```

Everything except `LiveDialectTests` runs with zero infrastructure (SQLite in-memory).
Coverage: composer goldens ×3 dialects, the full operator × dialect matrix, expression
parser/emitter, validator rules, SQL-safety corpus, saved-report store, CSV writer, and
end-to-end engine passes.

## Live-dialect verification (M5)

`LiveDialectTests` runs the engine corpus against **real SQL Server, Oracle, and
PostgreSQL**. Each test skips (never fails) unless the matching environment variable
holds a connection string:

| Variable | Target | Example |
|---|---|---|
| `IR_TEST_SQLSERVER` | SQL Server 2019+ | `Server=vm-host;Database=irtest;User Id=irtest;Password=***;TrustServerCertificate=True` |
| `IR_TEST_ORACLE` | Oracle 19c+/XE 21c | `User Id=irtest;Password=***;Data Source=vm-host:1521/XEPDB1` |
| `IR_TEST_POSTGRES` | PostgreSQL 14+ | `Host=vm-host;Port=5432;Database=irtest;Username=irtest;Password=***` |

Run just the battery:

```bash
dotnet test tests/InteractiveReport.Core.Tests --filter "FullyQualifiedName~LiveDialectTests"
```

**What it does:** on first use per run it **drops and recreates a table named
`IR_TEST_ORDERS`** in the target database and seeds the canonical 10 rows, then runs
filters/search/blank semantics/aggregates/breaks/computed columns/context-param
binding/groupBy/pivot/export against it. Point it at a scratch database, not anything
you care about.

Expected numbers are identical on every dialect by design — including the blank-count
test (4): SQLite/SQL Server/PostgreSQL count 3 NULLs + 1 empty string, while Oracle
turns the empty string into a fourth NULL at insert. If that test passes on every
engine, the per-dialect `blank` semantics are doing their job.

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
```

3. `TrustServerCertificate=True` in the connection string avoids TLS trust errors with
   the default self-signed certificate — fine for a test VM.

### Oracle VM setup (once)

1. Install Oracle Database XE 21c (free). The default pluggable database service is
   `XEPDB1`; the listener is on 1521 — allow it through the VM firewall.
2. Create the test user (run in `sqlplus system/...@//localhost:1521/XEPDB1`):

```sql
CREATE USER irtest IDENTIFIED BY "YourStrongPassword1!";
GRANT CREATE SESSION, CREATE TABLE TO irtest;
ALTER USER irtest QUOTA UNLIMITED ON USERS;
```

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
dotnet test tests/InteractiveReport.Core.Tests --filter "FullyQualifiedName~LiveDialectTests" -v normal
```

Every `LiveDialectTests` entry flips from Skipped to Passed per variable that is set —
or run `run-live-tests.ps1` at the repo root, which sets all three for the dev VM and
probes the ports first.
