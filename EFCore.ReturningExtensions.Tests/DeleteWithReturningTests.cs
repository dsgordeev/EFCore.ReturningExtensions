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
                            DECLARE @p int = 10000;
                            
                            DELETE [u]
                            OUTPUT DELETED.[Id], DELETED.[Name] + N'_'
                            FROM [User] AS [u]
                            INNER JOIN (
                                SELECT [u0].[Id], [u0].[Name]
                                FROM [User] AS [u0]
                                WHERE CAST(LEN([u0].[Name]) AS int) < @p
                            ) AS [u1] ON [u].[Id] = [u1].[Id]
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
                            ) AS [u1] ON [u].[Id] = [u1].[Id]
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
                            -- @p='10000'
                            DELETE FROM "User" AS u
                            USING (
                                SELECT u0."Id", u0."Name"
                                FROM "User" AS u0
                                WHERE length(u0."Name")::int < @p
                            ) AS u1
                            WHERE u."Id" = u1."Id" AND (u."Name" = 'A' OR u."Name" = 'B')
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
                            ) AS u1
                            WHERE u."Id" = u1."Id" AND (u."Name" = 'A' OR u."Name" = 'B')
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
                            .param set @p 10000
                            
                            DELETE FROM "User" AS "u5"
                            WHERE "u5"."Id" IN (
                                SELECT "u6"."Id"
                                FROM "User" AS "u6"
                                INNER JOIN (
                                    SELECT "u8"."Id", "u8"."Name"
                                    FROM "User" AS "u8"
                                    WHERE length("u8"."Name") < @p
                                ) AS "u7" ON "u6"."Id" = "u7"."Id"
                                WHERE "u6"."Name" = 'A' OR "u6"."Name" = 'B'
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
                            DELETE FROM "User" AS "u5"
                            WHERE "u5"."Id" IN (
                                SELECT "u6"."Id"
                                FROM "User" AS "u6"
                                INNER JOIN (
                                    SELECT "u8"."Id", "u8"."Name"
                                    FROM "User" AS "u8"
                                    WHERE length("u8"."Name") < 10000
                                ) AS "u7" ON "u6"."Id" = "u7"."Id"
                                WHERE "u6"."Name" = 'A' OR "u6"."Name" = 'B'
                            )
                            RETURNING "Id", "Name" || '_'
                            """, query.ToQueryString());
        }
    }
}
