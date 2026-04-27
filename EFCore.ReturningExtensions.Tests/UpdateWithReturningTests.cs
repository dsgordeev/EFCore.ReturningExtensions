using EFCore.ReturningExtensions.Extensions;
using EFCore.ReturningExtensions.Tests.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EFCore.ReturningExtensions.Tests
{
    [TestClass]
    public sealed class UpdateWithReturningTests
    {
        [TestMethod]
        public void Updated_WhenSqlServerAndParameter_ReturnUpdateWithOutputClause()
        {
            // Arrange
            using var scope = new ServiceCollection()
                .AddDbContext<SqlUsersDbContext>()
                .BuildServiceProvider()
                .CreateScope();

            using var ctx = scope.ServiceProvider.GetRequiredService<SqlUsersDbContext>();

            var hundred = 10 * 10;

            // Act
            var query = ctx.Users
                .AsNoTracking()
                .Where(p => p.Name.Length < hundred)
                .Select(x => new User { Id = x.Id, Name = x.Name })
                .Select(x => x)
                .Join(ctx.Users.Where(p => p.Name.Length < 100 * hundred), x => x.Id, x => x.Id, (x, y) => x)
                .Updated(s =>
                        s.SetProperty(p => p.Name, p => (p.Name + "_").Substring(0, 1)),
                    x => x);

            // Assert
            Assert.AreEqual("""
                            DECLARE @__p_1 int = 10000;
                            DECLARE @__hundred_0 int = 100;
                            
                            UPDATE [u]
                            SET [u].[Name] = SUBSTRING([u].[Name] + N'_', 0 + 1, 1)
                            OUTPUT INSERTED.[Id], INSERTED.[Name]
                            FROM [User] AS [u]
                            INNER JOIN (
                                SELECT [u0].[Id], [u0].[Name]
                                FROM [User] AS [u0]
                                WHERE CAST(LEN([u0].[Name]) AS int) < @__p_1
                            ) AS [t] ON [u].[Id] = [t].[Id]
                            WHERE CAST(LEN([u].[Name]) AS int) < @__hundred_0
                            """, query.ToQueryString());
        }

        [TestMethod]
        public void Updated_WhenSqlServer_ReturnUpdateWithOutputClause()
        {
            // Arrange
            using var scope = new ServiceCollection()
                .AddDbContext<SqlUsersDbContext>()
                .BuildServiceProvider()
                .CreateScope();

            using var ctx = scope.ServiceProvider.GetRequiredService<SqlUsersDbContext>();

            // Act
            var query = ctx.Users
                .AsNoTracking()
                .Where(p => p.Name.Length < 100)
                .Select(x => new User { Id = x.Id, Name = x.Name })
                .Select(x => x)
                .Join(ctx.Users.Where(p => p.Name.Length < 100 * 100), x => x.Id, x => x.Id, (x, y) => x)
                .Updated(s =>
                        s.SetProperty(p => p.Name, p => (p.Name + "_").Substring(0, 1)),
                    x => x);

            // Assert
            Assert.AreEqual("""
                            UPDATE [u]
                            SET [u].[Name] = SUBSTRING([u].[Name] + N'_', 0 + 1, 1)
                            OUTPUT INSERTED.[Id], INSERTED.[Name]
                            FROM [User] AS [u]
                            INNER JOIN (
                                SELECT [u0].[Id], [u0].[Name]
                                FROM [User] AS [u0]
                                WHERE CAST(LEN([u0].[Name]) AS int) < 10000
                            ) AS [t] ON [u].[Id] = [t].[Id]
                            WHERE CAST(LEN([u].[Name]) AS int) < 100
                            """, query.ToQueryString());
        }

        [TestMethod]
        public void Updated_WhenPostgresAndParameter_ReturnUpdateWithReturningClause()
        {
            // Arrange
            using var scope = new ServiceCollection()
                .AddDbContext<PgUsersDbContext>()
                .BuildServiceProvider()
                .CreateScope();

            using var ctx = scope.ServiceProvider.GetRequiredService<PgUsersDbContext>();

            var hundred = 10 * 10;

            // Act
            var query = ctx.Users
                .AsNoTracking()
                .Where(p => p.Name.Length < hundred)
                .Select(x => new User { Id = x.Id, Name = x.Name })
                .Select(x => x)
                .Join(ctx.Users.Where(p => p.Name.Length < 100 * hundred), x => x.Id, x => x.Id, (x, y) => x)
                .Updated(s =>
                        s.SetProperty(p => p.Name, p => (p.Name + "_").Substring(0, 1)),
                    x => x);

            // Assert
            Assert.AreEqual("""
                            -- @__p_1='10000'
                            -- @__hundred_0='100'
                            UPDATE "User" AS u
                            SET "Name" = substring(u."Name" || '_', 1, 1)
                            FROM (
                                SELECT u0."Id", u0."Name"
                                FROM "User" AS u0
                                WHERE length(u0."Name")::int < @__p_1
                            ) AS t
                            WHERE u."Id" = t."Id" AND length(u."Name")::int < @__hundred_0
                            RETURNING u."Id", u."Name"
                            """, query.ToQueryString());
        }

        [TestMethod]
        public void Updated_WhenPostgres_ReturnUpdateWithReturningClause()
        {
            // Arrange
            using var scope = new ServiceCollection()
                .AddDbContext<PgUsersDbContext>()
                .BuildServiceProvider()
                .CreateScope();

            using var ctx = scope.ServiceProvider.GetRequiredService<PgUsersDbContext>();

            // Act
            var query = ctx.Users
                .AsNoTracking()
                .Where(p => p.Name.Length < 100)
                .Select(x => new User { Id = x.Id, Name = x.Name })
                .Select(x => x)
                .Join(ctx.Users.Where(p => p.Name.Length < 100 * 100), x => x.Id, x => x.Id, (x, y) => x)
                .Updated(s =>
                        s.SetProperty(p => p.Name, p => (p.Name + "_").Substring(0, 1)),
                    x => x);

            // Assert
            Assert.AreEqual("""
                            UPDATE "User" AS u
                            SET "Name" = substring(u."Name" || '_', 1, 1)
                            FROM (
                                SELECT u0."Id", u0."Name"
                                FROM "User" AS u0
                                WHERE length(u0."Name")::int < 10000
                            ) AS t
                            WHERE u."Id" = t."Id" AND length(u."Name")::int < 100
                            RETURNING u."Id", u."Name"
                            """, query.ToQueryString());
        }

        [TestMethod]
        public void Updated_WhenSqliteAndParameter_ReturnUpdateWithReturningClause()
        {
            // Arrange
            using var scope = new ServiceCollection()
                .AddDbContext<SqliteUsersDbContext>()
                .BuildServiceProvider()
                .CreateScope();

            using var ctx = scope.ServiceProvider.GetRequiredService<SqliteUsersDbContext>();

            var hundred = 10 * 10;

            // Act
            var query = ctx.Users
                .AsNoTracking()
                .Where(p => p.Name.Length < hundred)
                .Select(x => new User { Id = x.Id, Name = x.Name })
                .Select(x => x)
                .Join(ctx.Users.Where(p => p.Name.Length < 100 * hundred), x => x.Id, x => x.Id, (x, y) => x)
                .Updated(s =>
                        s.SetProperty(p => p.Name, p => (p.Name + "_").Substring(0, 1)),
                    x => x);

            // Assert
            Assert.AreEqual("""
                            .param set @__p_1 10000
                            .param set @__hundred_0 100
                            
                            UPDATE "User" AS "u"
                            SET "Name" = substr("u"."Name" || '_', 0 + 1, 1)
                            FROM (
                                SELECT "u0"."Id", "u0"."Name"
                                FROM "User" AS "u0"
                                WHERE length("u0"."Name") < @__p_1
                            ) AS "t"
                            WHERE "u"."Id" = "t"."Id" AND length("u"."Name") < @__hundred_0
                            RETURNING "Id", "Name"
                            """, query.ToQueryString());
        }

        [TestMethod]
        public void Updated_WhenSqlite_ReturnUpdateWithReturningClause()
        {
            // Arrange
            using var scope = new ServiceCollection()
                .AddDbContext<SqliteUsersDbContext>()
                .BuildServiceProvider()
                .CreateScope();

            using var ctx = scope.ServiceProvider.GetRequiredService<SqliteUsersDbContext>();

            // Act
            var query = ctx.Users
                .AsNoTracking()
                .Where(p => p.Name.Length < 100)
                .Select(x => new User { Id = x.Id, Name = x.Name })
                .Select(x => x)
                .Join(ctx.Users.Where(p => p.Name.Length < 100 * 100), x => x.Id, x => x.Id, (x, y) => x)
                .Updated(s =>
                        s.SetProperty(p => p.Name, p => (p.Name + "_").Substring(0, 1)),
                    x => x);

            // Assert
            Assert.AreEqual("""
                            UPDATE "User" AS "u"
                            SET "Name" = substr("u"."Name" || '_', 0 + 1, 1)
                            FROM (
                                SELECT "u0"."Id", "u0"."Name"
                                FROM "User" AS "u0"
                                WHERE length("u0"."Name") < 10000
                            ) AS "t"
                            WHERE "u"."Id" = "t"."Id" AND length("u"."Name") < 100
                            RETURNING "Id", "Name"
                            """, query.ToQueryString());
        }
    }
}
