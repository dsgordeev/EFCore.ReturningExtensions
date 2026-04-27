# EF Core NonQuery Extensions

## Features

- 🚀 **ExecuteUpdate** and **ExecuteDelete** - Add methods support

## Installation

### .NET CLI
```bash
dotnet add package EFCore.ReturningExtensions.NonQueryPatch.SqlServer
```

## Usage

### Configure
```csharp
public class ItemsDbContext(DbContextOptions<ItemsDbContext> options) : DbContext(options)
{
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.UseSqlServer(connectionString, o =>
        {
            o.UseNonQueryPatchExtensions();
        });
    }
}
```

### Update
To update
```csharp
var updatedItemsCount = dbContext.Items
    .AsNoTracking()
    .Where(x => x.Name.Length < 100)
    .ExecuteUpdate(s => s.SetProperty(p => p.Name, p => p.Name + "_suffix"))
```

### Delete
```csharp
var deletedItemsCount = dbContext.Items
    .AsNoTracking()
    .Where(x => x.Name.Length < 100)
    .ExecuteDelete();
```
