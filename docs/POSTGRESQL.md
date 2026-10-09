# Running Chronicle on PostgreSQL

SQLite is the default and is fine for most libraries. PostgreSQL is the option for a very large library (the Database page warns
when the SQLite file passes the size you set), for several people hammering the server at once, or when you would rather have a
database server you already back up. Chronicle supports both; this page covers moving an existing SQLite database across.

## What you need

* A PostgreSQL server, version 13 or newer, and an **empty database** on it with a user that owns it (it creates tables and the
  `citext` extension, which PostgreSQL allows a database owner to do).
* Chronicle stopped while you copy, so the copy is a consistent snapshot (the command only *reads* the SQLite file, so a copy made
  while Chronicle runs cannot damage anything, but it may miss the last few changes).

## Copy the data

1. Start Chronicle once on the current version so it brings the SQLite database up to date, then stop it. (The command refuses
   an out-of-date database and says so.)
2. Create the empty PostgreSQL database, for example `createdb -U postgres chronicle`.
3. From the Chronicle folder:

```powershell
.\Chronicle.API.exe --copy-database-to-postgres "Host=localhost;Port=5432;Database=chronicle;Username=chronicle;Password=..."
```

   (From a source checkout: `dotnet run --project src/Chronicle.API -- --copy-database-to-postgres "..."`.)

It creates the PostgreSQL tables from Chronicle's PostgreSQL migrations, copies every table (parents before children, so no
constraint is ever switched off), makes the id counters continue after the copied ids, and then compares the row count of every
table. It prints one line per table and ends with "Every table's row count matches", or tells you what does not and exits with an
error. Rows that point at an item that no longer exists (leftovers older versions could leave behind; SQLite does not stop
them, PostgreSQL would refuse them) are left out and counted in the summary. The SQLite file is never changed. If it fails part-way, fix the cause and run it again with `--replace`, which empties the
PostgreSQL database first. Without `--replace` it refuses a database that already holds accounts or library items.

## Switch Chronicle over

Set these for the Chronicle process and restart it:

| Setting | Value |
|---|---|
| `DATABASE_PROVIDER` | `postgresql` |
| `ConnectionStrings__DefaultConnection` | the same connection string |

(or `Database:Provider` / `ConnectionStrings:DefaultConnection` in `appsettings.json`). A connection string beginning `Host=` also
selects PostgreSQL by itself. On start Chronicle applies any PostgreSQL migrations it is missing. To go back to SQLite, remove the
two settings; the SQLite file is exactly as you left it (changes made while on PostgreSQL are not carried back).

## Differences to know about

* **Backups and maintenance.** The Database page's backup, restore and rebuild tools are for SQLite. On PostgreSQL the page says so;
  back up with `pg_dump` and size it with your usual tools.
* **Case-insensitive file names.** SQLite compares the known-file-name column without regard to case through a collation;
  PostgreSQL uses the `citext` type for the same column.
* **Two migration sets.** The SQLite migrations (`Chronicle.Data`) and the PostgreSQL migrations (`Chronicle.Data.Postgres`) are kept
  side by side; a developer adds both with `scripts/Add-Migration.ps1 <Name>`.

## For developers: testing against a real server

Set `CHRONICLE_TEST_PG` to a connection string for a server's `postgres` database (for example
`Host=127.0.0.1;Port=5432;Username=postgres`) and the PostgreSQL tests run: the copier (`DatabaseCopierTests`) and a smoke test of
the whole application on PostgreSQL (`PostgresSmokeTests`). Each creates and drops its own throwaway database. Without the variable
they are skipped.
