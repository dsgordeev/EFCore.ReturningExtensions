using EFCore.ReturningExtensions.Extensions;
using EFCore.ReturningExtensions.Tests.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EFCore.ReturningExtensions.Tests
{
    public sealed partial class UpdateWithReturningTests
    {
        [TestClass]
        public sealed class Sqlite : UpdateWithReturningTests<SqliteUsersDbContext>
        {
            [TestMethod]
            public void Updated_WhenSqliteAndMixed_ReturnUpdateWithOutputClause()
            {
                // Arrange
                using var scope = ServiceProvider.CreateScope();
                using var ctx = GetDbContext();

                // Act
                var query = ctx.Users
                    .AsNoTracking()
                    .Where(p => p.Name.Length < 100)
                    .Join(ctx.Users.Where(p => p.Name.Length < 100 * 100), x => x.Id, x => x.Id, (x, y) => x)
                    .Updated(s =>
                            s.SetProperty(p => p.Name, p => (p.Name + "_").Substring(0, 1)),
                        (x, y) => new { y.Id, Name2 = x.Name + "<|" + y.Name });

                // Assert
                Assert.AreEqual("""
                                UPDATE "User" AS "u"
                                SET "Name" = substr("u"."Name" || '_', 0 + 1, 1)
                                FROM (
                                    SELECT "u0"."Id", "u0"."Name"
                                    FROM "User" AS "u0"
                                    WHERE length("u0"."Name") < 10000
                                ) AS "u1"
                                WHERE "u"."Id" = "u1"."Id" AND length("u"."Name") < 100
                                RETURNING "Id", "Name" || '<|' || "Name"
                                """, query.ToQueryString());
            }

            [TestMethod]
            public void Updated_WhenSqliteAndDeletedOnly_ReturnUpdateWithOutputClause()
            {
                // Arrange
                using var scope = ServiceProvider.CreateScope();
                using var ctx = GetDbContext();

                // Act
                var query = ctx.Users
                    .AsNoTracking()
                    .Where(p => p.Name.Length < 100)
                    .Join(ctx.Users.Where(p => p.Name.Length < 100 * 100), x => x.Id, x => x.Id, (x, y) => x)
                    .Updated(s =>
                            s.SetProperty(p => p.Name, p => (p.Name + "_").Substring(0, 1)),
                        (x, y) => y);

                // Assert
                Assert.AreEqual("""
                                UPDATE "User" AS "u"
                                SET "Name" = substr("u"."Name" || '_', 0 + 1, 1)
                                FROM (
                                    SELECT "u0"."Id", "u0"."Name"
                                    FROM "User" AS "u0"
                                    WHERE length("u0"."Name") < 10000
                                ) AS "u1"
                                WHERE "u"."Id" = "u1"."Id" AND length("u"."Name") < 100
                                RETURNING "Id", "Name"
                                """, query.ToQueryString());
            }

            [TestMethod]
            public void Updated_WhenSqliteFallback_ReturnUpdateWithOutputClause()
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
                    .Join(ctx.Users.Where(p => p.Name.Length < 100 * 100), x => x.Id, x => x.Id, (x, y) => x)
                    .Updated(s =>
                            s.SetProperty(p => p.Name, p => (p.Name + "_").Substring(0, 1)),
                        (x, y) => x);

                // Assert
                Assert.AreEqual("""
                                UPDATE "User" AS "u"
                                SET "Name" = substr("u"."Name" || '_', 0 + 1, 1)
                                FROM (
                                    SELECT "u0"."Id", "u0"."Name"
                                    FROM "User" AS "u0"
                                    WHERE length("u0"."Name") < 10000
                                ) AS "u1"
                                WHERE "u"."Id" = "u1"."Id" AND length("u"."Name") < 100
                                RETURNING "Id", "Name"
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
                    .Join(ctx.Users.Where(p => p.Name.Length < 100 * hundred), x => x.Id, x => x.Id, (x, y) => x)
                    .Updated(s =>
                            s.SetProperty(p => p.Name, p => (p.Name + "_").Substring(0, 1)),
                        x => x);

                // Assert
                Assert.AreEqual("""
                                .param set @p 10000
                                .param set @hundred 100

                                UPDATE "User" AS "u"
                                SET "Name" = substr("u"."Name" || '_', 0 + 1, 1)
                                FROM (
                                    SELECT "u0"."Id", "u0"."Name"
                                    FROM "User" AS "u0"
                                    WHERE length("u0"."Name") < @p
                                ) AS "u1"
                                WHERE "u"."Id" = "u1"."Id" AND length("u"."Name") < @hundred
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
                                ) AS "u1"
                                WHERE "u"."Id" = "u1"."Id" AND length("u"."Name") < 100
                                RETURNING "Id", "Name"
                                """, query.ToQueryString());
            }
        }
    }
}
