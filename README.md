![.NET 8.0](https://img.shields.io/badge/dotnet-8.0-blue)
![.NET 10.0](https://img.shields.io/badge/dotnet-10.0-purple)

# EFCore Returning Extensions

A research-driven extension for Entity Framework Core that experiments 
with RETURNING\OUTPUT clause support, enabling extension methods 
that return affected rows as enumerable queries.

### 🔬 Research Motivation

Standard EF Core's ExecuteUpdate and ExecuteDelete return only row counts, 
leaving developers to make additional round trips to retrieve affected data. 
This package investigates whether it's possible to:

- Inject RETURNING\OUTPUT clauses into generated SQL while preserving EF Core's semantics

## Features

- 🚀 **Single Round Trip** - Retrieve affected rows without additional SELECT queries

## Limitations

- ⚠️ **Queryable Composition** - The returned IQueryable<T> should only be used for enumeration
- ⚠️ **RETURNING/OUTPUT Clause Restrictions** - Extension method just build sql query with RETURNING/OUTPUT clause with corresponding restrictions
- ⚠️ **Include() Restrictions** - Queries with **Include()** may not be translated correctly for now, **Join()** method may help

## 🧪 Demo

### 💪 Traditional approach
```cs
// Update
using var transaction = dbContext.Database.CreateTransaction();

var updatedItems = dbContext.Items
    .Where(x => x.Name.Length < 100)
    .ToList();

updatedItems.ForEach(x => x.Name = x.Name + "_suffix");

dbContext.SaveChanges();
transaction.Commit();

// Delete
using var transaction = dbContext.Database.CreateTransaction();

var query = dbContext.Items
    .Where(x => x.Name.Length < 100);

var deletedItems = query
    .ToList();

query.ExecuteDelete();
transaction.Commit();
```

### ✅ Alternative
```cs
// Configure
...
dbContextOptionsBuilder.ReplaceSqlServerQueryServices();
...

// And use
var updatedItems = dbContext.Items
    .AsNoTracking()
    .Where(x => x.Name.Length < 100)
    .Updated(s => s.SetProperty(p => p.Name, p => p.Name + "_suffix"), x => x)
    .ToList();

var deletedItems = dbContext.Items
    .AsNoTracking()
    .Where(x => x.Name.Length < 100)
    .Deleted(x => x)
    .ToList();
```

## 🎯 Supported .NET Versions

| Provider   | ![.NET 8.0](https://img.shields.io/badge/dotnet-8.0-blue)                                                                                                           | ![.NET 10.0](https://img.shields.io/badge/dotnet-10.0-purple)                                                                                                          |
| -----------| ------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| SQL Server | [![NuGet 8.1.0](https://img.shields.io/badge/8.1.0-004880?style=flat-square&logo=nuget)](https://www.nuget.org/packages/EFCore.ReturningExtensions.SqlServer/8.1.0) | [![NuGet 10.1.0](https://img.shields.io/badge/10.1.0-004880?style=flat-square&logo=nuget)](https://www.nuget.org/packages/EFCore.ReturningExtensions.SqlServer/10.1.0) |
| PostgreSQL | [![NuGet 8.0.0](https://img.shields.io/badge/8.0.0-004880?style=flat-square&logo=nuget)](https://www.nuget.org/packages/EFCore.ReturningExtensions.Npgsql/8.0.0)    | [![NuGet 10.0.0](https://img.shields.io/badge/10.0.0-004880?style=flat-square&logo=nuget)](https://www.nuget.org/packages/EFCore.ReturningExtensions.Npgsql/10.0.0)    |
| SQLite     | [![NuGet 8.0.0](https://img.shields.io/badge/8.0.0-004880?style=flat-square&logo=nuget)](https://www.nuget.org/packages/EFCore.ReturningExtensions.Sqlite/8.0.0)    | [![NuGet 10.0.0](https://img.shields.io/badge/10.0.0-004880?style=flat-square&logo=nuget)](https://www.nuget.org/packages/EFCore.ReturningExtensions.Sqlite/10.0.0)    |
