using EFCore.ReturningExtensions.Extensions;
using EFCore.ReturningExtensions.Tests.Data;
using Microsoft.EntityFrameworkCore;

namespace EFCore.ReturningExtensions.Tests;

public sealed partial class DeleteWithReturningTests
{
    [TestClass]
    public sealed class Postgres : DeleteWithReturningTests<PgUsersDbContext>
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
                            -- @__p_0='10000'
                            DELETE FROM "User" AS u
                            USING (
                                SELECT u0."Id", u0."Name"
                                FROM "User" AS u0
                                WHERE length(u0."Name")::int < @__p_0
                            ) AS t
                            WHERE u."Id" = t."Id" AND (u."Name" = 'A' OR u."Name" = 'B')
                            RETURNING u."Id", u."Name" || '_' AS "Name"
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
                            DELETE FROM "User" AS u
                            USING (
                                SELECT u0."Id", u0."Name"
                                FROM "User" AS u0
                                WHERE length(u0."Name")::int < 10000
                            ) AS t
                            WHERE u."Id" = t."Id" AND (u."Name" = 'A' OR u."Name" = 'B')
                            RETURNING u."Id", u."Name" || '_' AS "Name"
                            """, query.ToQueryString());
        }
        [TestMethod]
        public void Deleted_WhenInclude_ReturnDeleteWithOutputClause()
        {
            // Arrange
            using var ctx = GetDbContext();

            // Act
            var query = ctx.Accounts
                .AsNoTracking()
                .Include(x => x.Type)
                .ThenInclude(x => x.SubType)
                .Where(p => p.Name.Length < 100 && p.Type.TypeName.Length < 1000 && p.Type.SubType.StName.Length < 10000)
                .Deleted(x => x);

            // Assert
            Assert.AreEqual($"""
                             DELETE FROM "Account" AS a
                             USING "AccountType" AS a0, "AccountSubType" AS a1
                             WHERE a."TypeId" = a0."Id" AND a0."SubTypeId" = a1."Id" AND length(a."Name")::int < 100 AND length(a0."TypeName")::int < 1000 AND length(a1."StName")::int < 10000
                             RETURNING a."Id", a."Name", a."TypeId", a0."Id", a0."SubTypeId", a0."TypeName", a1."Id", a1."StName"
                             """, query.ToQueryString());
        }
    }
}