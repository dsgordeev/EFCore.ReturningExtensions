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
                                
                                DELETE FROM "User" AS "uz"
                                WHERE "uz"."Id" IN (
                                SELECT "u"."Id"
                                FROM "User" AS "u"
                                INNER JOIN (
                                    SELECT "u0"."Id", "u0"."Name"
                                    FROM "User" AS "u0"
                                    WHERE length("u0"."Name") < @__p_0
                                ) AS "t" ON "u"."Id" = "t"."Id"
                                WHERE ("u"."Name" = 'A') OR ("u"."Name" = 'B')
                                )
                                RETURNING "Id", "Name" || '_' AS "Name"
                                """, query.ToQueryString());
            }

            [TestMethod]
            public void Deleted_WhenParameterLess_ReturnDeleteWithReturningClause()
            {
                // Arrange
                using var ctx = GetDbContext();

                // Act
                var query = ctx.Users
                    .AsNoTracking()
                    .Where(p => p.Name == "A" || p.Name == "B")
                    .Join(ctx.Users.Where(p => p.Name.Length < 100 * 100), x => x.Id, x => x.Id, (x, y) => x)
                    .Deleted(x => new { x.Id, Name = x.Name + "_" });

                // Assert
                Assert.AreEqual("""
                                DELETE FROM "User" AS "uz"
                                WHERE "uz"."Id" IN (
                                SELECT "u"."Id"
                                FROM "User" AS "u"
                                INNER JOIN (
                                    SELECT "u0"."Id", "u0"."Name"
                                    FROM "User" AS "u0"
                                    WHERE length("u0"."Name") < 10000
                                ) AS "t" ON "u"."Id" = "t"."Id"
                                WHERE ("u"."Name" = 'A') OR ("u"."Name" = 'B')
                                )
                                RETURNING "Id", "Name" || '_' AS "Name"
                                """, query.ToQueryString());
            }
        }
    }
}
