# EF Core Returning Extensions

## Features

- 🚀 **Single Round Trip** - Retrieve affected rows without additional SELECT queries

## Installation

### .NET CLI
```bash
dotnet add package EFCore.ReturningExtensions.Sqlite --version 8.0.0
```

## Usage

### Configure
```csharp
public class ItemsDbContext(DbContextOptions<ItemsDbContext> options) : DbContext(options)
{
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.UseSqlite(connectionString);
        optionsBuilder.ReplaceSqliteQueryServices();
    }
}
```

### Update
```csharp
var updatedItems = dbContext.Items
    .AsNoTracking()
    .Where(x => x.Name.Length < 100)
    .Updated(s => s.SetProperty(p => p.Name, p => p.Name + "_suffix"), x => x)
    .ToList();
```

### Delete
```csharp
var deletedItems = dbContext.Items
    .AsNoTracking()
    .Where(x => x.Name.Length < 100)
    .Deleted(x => x)
    .ToList();
```
