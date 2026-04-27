using EFCore.ReturningExtensions.Extensions;
using EFCore.ReturningExtensions.Tests.Data;
using Microsoft.EntityFrameworkCore;

namespace EFCore.ReturningExtensions.Tests;

public sealed partial class UpdateWithReturningTests
{
    [TestClass]
    public sealed class Postgres : UpdateWithReturningTests<PgUsersDbContext>
    {
        [TestMethod]
        public void Updated_WhenAndMixed_ReturnUpdateWithReturningClause()
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
                    (x, y) => new { y.Id, Name2 = x.Name + "<|" + y.Name });

            // Assert
            Assert.AreEqual("""
                            UPDATE "User" AS u
                            SET "Name" = substring(u."Name" || '_', 1, 1)
                            FROM (
                                SELECT u0."Id", u0."Name"
                                FROM "User" AS u0
                                WHERE length(u0."Name")::int < 10000
                            ) AS t
                            INNER JOIN "User" AS OLD ON t."Id" = OLD."Id"
                            WHERE u."Id" = t."Id" AND length(u."Name")::int < 100
                            RETURNING OLD."Id", u."Name" || '<|' || OLD."Name" AS "Name2"
                            """, query.ToQueryString());
        }

        [TestMethod]
        public void Updated_WhenAndDeletedOnly_ReturnUpdateWithReturningClause()
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
                    (x, y) => x);

            // Assert
            Assert.AreEqual("""
                            UPDATE "User" AS u
                            SET "Name" = substring(u."Name" || '_', 1, 1)
                            FROM (
                                SELECT u0."Id", u0."Name"
                                FROM "User" AS u0
                                WHERE length(u0."Name")::int < 10000
                            ) AS t
                            INNER JOIN "User" AS OLD ON t."Id" = OLD."Id"
                            WHERE u."Id" = t."Id" AND length(u."Name")::int < 100
                            RETURNING u."Id", u."Name"
                            """, query.ToQueryString());
        }

        [TestMethod]
        public void Updated_WhenFallback_ReturnUpdateWithReturningClause()
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
                    (x, y) => x);

            // Assert
            Assert.AreEqual("""
                            UPDATE "User" AS u
                            SET "Name" = substring(u."Name" || '_', 1, 1)
                            FROM (
                                SELECT u0."Id", u0."Name"
                                FROM "User" AS u0
                                WHERE length(u0."Name")::int < 10000
                            ) AS t
                            INNER JOIN "User" AS OLD ON t."Id" = OLD."Id"
                            WHERE u."Id" = t."Id" AND length(u."Name")::int < 100
                            RETURNING u."Id", u."Name"
                            """, query.ToQueryString());
        }

        [TestMethod]
        public void Updated_WhenParameter_ReturnUpdateWithReturningClause()
        {
            // Arrange
            using var ctx = GetDbContext();

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
                            INNER JOIN "User" AS OLD ON t."Id" = OLD."Id"
                            WHERE u."Id" = t."Id" AND length(u."Name")::int < @__hundred_0
                            RETURNING u."Id", u."Name"
                            """, query.ToQueryString());
        }

        [TestMethod]
        public void Updated_WhenParameterLess_ReturnUpdateWithReturningClause()
        {
            // Arrange
            using var ctx = GetDbContext();

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
                            INNER JOIN "User" AS OLD ON t."Id" = OLD."Id"
                            WHERE u."Id" = t."Id" AND length(u."Name")::int < 100
                            RETURNING u."Id", u."Name"
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
                    x => new { x.Id, Name = x.Name + "_" });

            // Assert
            Assert.AreEqual($"""
                             UPDATE "Account" AS a
                             SET "Name" = substring(a."Name" || '_', 1, 1)
                             FROM "AccountType" AS a0
                             INNER JOIN "AccountSubType" AS a1 ON a0."SubTypeId" = a1."Id"
                             INNER JOIN "Account" AS OLD ON a1."Id" = OLD."Id"
                             WHERE a."TypeId" = a0."Id" AND length(a."Name")::int < 100 AND length(a0."TypeName")::int < 1000 AND length(a1."StName")::int < 10000
                             RETURNING a."Id", a."Name", a."TypeId", a0."Id", a0."SubTypeId", a0."TypeName", a1."Id", a1."StName"
                             """, query.ToQueryString());
        }
    }
}