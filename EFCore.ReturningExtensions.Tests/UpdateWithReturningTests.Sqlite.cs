using EFCore.ReturningExtensions.Extensions;
using EFCore.ReturningExtensions.Tests.Data;
using Microsoft.EntityFrameworkCore;

namespace EFCore.ReturningExtensions.Tests;

public sealed partial class UpdateWithReturningTests
{
    [TestClass]
    public sealed class Sqlite : UpdateWithReturningTests<SqliteUsersDbContext>
    {
        [TestMethod]
        public void Updated_WhenAndParameter_ReturnUpdateWithReturningClause()
        {
            // Arrange
            using var ctx = GetDbContext();

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
        public void Updated_When_ReturnUpdateWithReturningClause()
        {
            // Arrange
            using var ctx = GetDbContext();

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
                            ) AS "t"
                            WHERE "u"."Id" = "t"."Id" AND length("u"."Name") < 100
                            RETURNING "Id", "Name"
                            """, query.ToQueryString());
        }

        [TestMethod]
        public void Updated_WhenInclude_ReturnUpdateWithOutputClause()
        {
            // Arrange
            using var ctx = GetDbContext();

            // Act
            var query = ctx.Accounts
                .AsNoTracking()
                .Include(x => x.Type)
                .ThenInclude(x => x.SubType)
                .Where(p => p.Name.Length < 100 && p.Type.TypeName.Length < 1000 && p.Type.SubType.StName.Length < 10000)
                .Updated(s =>
                        s.SetProperty(p => p.Name, p => (p.Name + "_").Substring(0, 1)),
                    x => x);

            // Assert
            Assert.AreEqual($"""
                             UPDATE "Account" AS "a"
                             SET "Name" = substr("a"."Name" || '_', 0 + 1, 1)
                             FROM "AccountType" AS "a0"
                             INNER JOIN "AccountSubType" AS "a1" ON "a0"."SubTypeId" = "a1"."Id"
                             WHERE "a"."TypeId" = "a0"."Id" AND length("a"."Name") < 100 AND length("a0"."TypeName") < 1000 AND length("a1"."StName") < 10000
                             RETURNING "Id", "Name", "TypeId", "Id", "SubTypeId", "TypeName", "Id", "StName"
                             """, query.ToQueryString());
        }
    }
}