# EF Core Returning Extensions

## Features

- 🚀 **Single Round Trip** - Retrieve affected rows without additional SELECT queries

## Installation

### .NET CLI
```bash
dotnet add package EFCore.ReturningExtensions.SqlServer --version 10.1.0
```

## Usage

### Configure
```csharp
public class ItemsDbContext(DbContextOptions<ItemsDbContext> options) : DbContext(options)
{
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.UseSqlServer(connectionString);
        optionsBuilder.ReplaceSqlServerQueryServices();
    }
}
```

### Update
To get updated
```csharp
var updatedItems = dbContext.Items
    .AsNoTracking()
    .Where(x => x.Name.Length < 100)
    .Updated(s => s.SetProperty(p => p.Name, p => p.Name + "_suffix"), x => x)
    .ToList();
```
or to get previous values
```csharp
var previousItems = dbContext.Items
    .AsNoTracking()
    .Where(x => x.Name.Length < 100)
    .Updated(s => s.SetProperty(p => p.Name, p => p.Name + "_suffix"), (x, y) => y)
    .ToList();
```
or to get mixed values
```csharp
var itemChanges = dbContext.Items
    .AsNoTracking()
    .Where(x => x.Name.Length < 100)
    .Updated(s => s.SetProperty(p => p.Name, p => p.Name + "_suffix"), (x, y) => new { Change = y.Name + "|>" + x.Name  })
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
