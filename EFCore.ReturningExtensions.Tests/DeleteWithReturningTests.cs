using EFCore.ReturningExtensions.Extensions;
using EFCore.ReturningExtensions.Tests.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EFCore.ReturningExtensions.Tests
{
    [TestClass]
    public sealed class DeleteWithReturningTests
    {
        [TestMethod]
        public void Deleted_WhenSqlServerAndParameter_ReturnDeleteWithOutputClause()
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
                .Where(p => p.Name == "A" || p.Name == "B")
                .Join(ctx.Users.Where(p => p.Name.Length < hundred * 100), x => x.Id, x => x.Id, (x, y) => x)
                .Deleted(x => new { x.Id, Name = x.Name + "_" });

            // Assert
            Assert.AreEqual("""
                            DECLARE @__p_0 int = 10000;
                            
                            DELETE [u]
                            OUTPUT DELETED.[Id], DELETED.[Name] + N'_'
                            FROM [User] AS [u]
                            INNER JOIN (
                                SELECT [u0].[Id], [u0].[Name]
                                FROM [User] AS [u0]
                                WHERE CAST(LEN([u0].[Name]) AS int) < @__p_0
                            ) AS [t] ON [u].[Id] = [t].[Id]
                            WHERE [u].[Name] = N'A' OR [u].[Name] = N'B'
                            """, query.ToQueryString());
        }

        [TestMethod]
        public void Deleted_WhenSqlServer_ReturnDeleteWithOutputClause()
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
                .Where(p => p.Name == "A" || p.Name == "B")
                .Join(ctx.Users.Where(p => p.Name.Length < 100 * 100), x => x.Id, x => x.Id, (x, y) => x)
                .Deleted(x => new { x.Id, Name = x.Name + "_" });

            // Assert
            Assert.AreEqual("""
                            DELETE [u]
                            OUTPUT DELETED.[Id], DELETED.[Name] + N'_'
                            FROM [User] AS [u]
                            INNER JOIN (
                                SELECT [u0].[Id], [u0].[Name]
                                FROM [User] AS [u0]
                                WHERE CAST(LEN([u0].[Name]) AS int) < 10000
                            ) AS [t] ON [u].[Id] = [t].[Id]
                            WHERE [u].[Name] = N'A' OR [u].[Name] = N'B'
                            """, query.ToQueryString());
        }

        [TestMethod]
        public void Deleted_WhenPostgresAndParameter_ReturnDeleteWithOutputClause()
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
                .Where(p => p.Name == "A" || p.Name == "B")
                .Join(ctx.Users.Where(p => p.Name.Length < hundred * 100), x => x.Id, x => x.Id, (x, y) => x)
                .Deleted(x => new { x.Id, Name = x.Name + "_" });

            // Assert
            Assert.AreEqual("""
                            -- @__p_0='10000'
                            DELETE FROM "User" AS u
                            USING (
                                SELECT u0."Id", u0."Name"
                                FROM "User" AS u0
                                WHERE length(u0."Name")::int < @__p_0
                            ) AS t
                            WHERE u."Id" = t."Id" AND (u."Name" = 'A' OR u."Name" = 'B')
                            RETURNING u."Id", u."Name" || '_'
                            """, query.ToQueryString());
        }

        [TestMethod]
        public void Deleted_WhenPostgres_ReturnDeleteWithOutputClause()
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
                .Where(p => p.Name == "A" || p.Name == "B")
                .Join(ctx.Users.Where(p => p.Name.Length < 100 * 100), x => x.Id, x => x.Id, (x, y) => x)
                .Deleted(x => new { x.Id, Name = x.Name + "_" });

            // Assert
            Assert.AreEqual("""
                            DELETE FROM "User" AS u
                            USING (
                                SELECT u0."Id", u0."Name"
                                FROM "User" AS u0
                                WHERE length(u0."Name")::int < 10000
                            ) AS t
                            WHERE u."Id" = t."Id" AND (u."Name" = 'A' OR u."Name" = 'B')
                            RETURNING u."Id", u."Name" || '_'
                            """, query.ToQueryString());
        }

        [TestMethod]
        public void Deleted_WhenSqliteAndParameter_ReturnDeleteWithOutputClause()
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
                .Where(p => p.Name == "A" || p.Name == "B")
                .Join(ctx.Users.Where(p => p.Name.Length < hundred * 100), x => x.Id, x => x.Id, (x, y) => x)
                .Deleted(x => new { x.Id, Name = x.Name + "_" });

            // Assert
            Assert.AreEqual("""
                            .param set @__p_0 10000
                            
                            DELETE FROM "User" AS "u"
                            WHERE "u"."Id" IN (
                                SELECT "u0"."Id"
                                FROM "User" AS "u0"
                                INNER JOIN (
                                    SELECT "u1"."Id", "u1"."Name"
                                    FROM "User" AS "u1"
                                    WHERE length("u1"."Name") < @__p_0
                                ) AS "t" ON "u0"."Id" = "t"."Id"
                                WHERE "u0"."Name" = 'A' OR "u0"."Name" = 'B'
                            )
                            RETURNING "Id", "Name" || '_'
                            """, query.ToQueryString());
        }

        [TestMethod]
        public void Deleted_WhenSqlite_ReturnDeleteWithOutputClause()
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
                .Where(p => p.Name == "A" || p.Name == "B")
                .Join(ctx.Users.Where(p => p.Name.Length < 100 * 100), x => x.Id, x => x.Id, (x, y) => x)
                .Deleted(x => new { x.Id, Name = x.Name + "_" });

            // Assert
            Assert.AreEqual("""
                            DELETE FROM "User" AS "u"
                            WHERE "u"."Id" IN (
                                SELECT "u0"."Id"
                                FROM "User" AS "u0"
                                INNER JOIN (
                                    SELECT "u1"."Id", "u1"."Name"
                                    FROM "User" AS "u1"
                                    WHERE length("u1"."Name") < 10000
                                ) AS "t" ON "u0"."Id" = "t"."Id"
                                WHERE "u0"."Name" = 'A' OR "u0"."Name" = 'B'
                            )
                            RETURNING "Id", "Name" || '_'
                            """, query.ToQueryString());
        }
    }
}
