using Chronicle.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Chronicle.Tests.Unit.Services;

/// <summary>
/// SQLite has one writer at a time. A write that cannot get the lock in time throws "database is locked", which used to
/// end a plugin's whole enrichment pass; it is now recognised as a transient condition to wait out.
/// </summary>
public class DatabaseBusyTests
{
    [Fact]
    public void RecognisesALockedOrBusyDatabase_EvenWrappedByEntityFramework()
    {
        Assert.True(MetadataEnrichmentService.IsDatabaseBusy(new SqliteException("database is locked", 5)));
        Assert.True(MetadataEnrichmentService.IsDatabaseBusy(new SqliteException("database table is locked", 6)));
        Assert.True(MetadataEnrichmentService.IsDatabaseBusy(
            new DbUpdateException("An error occurred while saving the entity changes.", new SqliteException("database is locked", 5))));
    }

    [Fact]
    public void DoesNotTreatOtherFailuresAsBusy()
    {
        Assert.False(MetadataEnrichmentService.IsDatabaseBusy(new InvalidOperationException("boom")));
        Assert.False(MetadataEnrichmentService.IsDatabaseBusy(new SqliteException("constraint failed", 19)));
        Assert.False(MetadataEnrichmentService.IsDatabaseBusy(
            new DbUpdateException("x", new SqliteException("UNIQUE constraint failed", 19))));
    }
}
