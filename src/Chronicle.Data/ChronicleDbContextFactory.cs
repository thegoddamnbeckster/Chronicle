using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Chronicle.Data;

public class ChronicleDbContextFactory : IDesignTimeDbContextFactory<ChronicleDbContext>
{
    public ChronicleDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<ChronicleDbContext>();
        optionsBuilder.UseSqlite("Data Source=chronicle.db");
        return new ChronicleDbContext(optionsBuilder.Options);
    }
}
