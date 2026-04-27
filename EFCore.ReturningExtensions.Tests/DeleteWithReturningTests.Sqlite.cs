using EFCore.ReturningExtensions.Extensions;
using EFCore.ReturningExtensions.Tests.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EFCore.ReturningExtensions.Tests
{
    public sealed partial class DeleteWithReturningTests
    {
        [TestClass]
        public sealed class Sqlite : DeleteWithReturningTests<SqliteUsersDbContext>
        {
            [TestMethod]
            public void Deleted_WhenParameter_ReturnDeleteWithReturningClause()
            {
                // Arrange
                using var scope = ServiceProvider.CreateScope();
                using var ctx = GetDbContext();

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
                                RETURNING "Id", "Name" || '_' AS "Name"
                                """, query.ToQueryString());
            }

            [TestMethod]
            public void Deleted_WhenParameterLess_ReturnDeleteWithReturningClause()
            {
                // Arrange
                using var scope = ServiceProvider.CreateScope();
                using var ctx = GetDbContext();

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
                                RETURNING "Id", "Name" || '_' AS "Name"
                                """, query.ToQueryString());
            }
        }
    }
}
