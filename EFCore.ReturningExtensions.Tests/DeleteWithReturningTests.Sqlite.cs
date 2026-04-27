using EFCore.ReturningExtensions.Extensions;
using EFCore.ReturningExtensions.Tests.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EFCore.ReturningExtensions.Tests
{
    public sealed partial class DeleteWithReturningTests
    {
        [TestClass]
        public sealed class Sqlite
        {
            [TestMethod]
            public void Deleted_WhenParameter_ReturnDeleteWithReturningClause()
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
                            RETURNING "Id", "Name" || '_' AS "Name"
                            """, query.ToQueryString());
            }

            [TestMethod]
            public void Deleted_WhenParameterLess_ReturnDeleteWithReturningClause()
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
                            RETURNING "Id", "Name" || '_' AS "Name"
                            """, query.ToQueryString());
            }
        }
    }
}
