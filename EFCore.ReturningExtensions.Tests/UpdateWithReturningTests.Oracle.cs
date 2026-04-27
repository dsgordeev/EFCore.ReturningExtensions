using EFCore.ReturningExtensions.Extensions;
using EFCore.ReturningExtensions.Tests.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EFCore.ReturningExtensions.Tests;

public sealed partial class UpdateWithReturningTests
{
    [TestClass]
    public sealed class Oracle : UpdateWithReturningTests<OracleUsersDbContext>
    {
        [TestMethod]
        public void Updated_WhenAndMixed_ReturnUpdateWithOutputClause()
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
                            UPDATE "User" "u"
                            SET "u"."Name" = SUBSTR("u"."Name" || N'_', 0 + 1, 1)
                            FROM (
                                SELECT "u0"."Id", "u0"."Name"
                                FROM "User" "u0"
                                WHERE CAST(LENGTH("u0"."Name") AS NUMBER(10)) < 10000
                            ) "u1"
                            WHERE (("u"."Id" = "u1"."Id") AND (CAST(LENGTH("u"."Name") AS NUMBER(10)) < 100))
                            RETURNING "OLD"."Id", ("u"."Name" || N'<|') || "OLD"."Name" "Name2"
                            """, query.ToQueryString());
        }

        [TestMethod]
        public void Updated_WhenAndDeletedOnly_ReturnUpdateWithOutputClause()
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
                            UPDATE "User" "u"
                            SET "u"."Name" = SUBSTR("u"."Name" || N'_', 0 + 1, 1)
                            FROM (
                                SELECT "u0"."Id", "u0"."Name"
                                FROM "User" "u0"
                                WHERE CAST(LENGTH("u0"."Name") AS NUMBER(10)) < 10000
                            ) "u1"
                            WHERE (("u"."Id" = "u1"."Id") AND (CAST(LENGTH("u"."Name") AS NUMBER(10)) < 100))
                            RETURNING "OLD"."Id", "OLD"."Name"
                            """, query.ToQueryString());
        }

        [TestMethod]
        public void Updated_WhenFallback_ReturnUpdateWithOutputClause()
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
                    (x, y) => x);

            // Assert
            Assert.AreEqual("""
                            UPDATE "User" "u"
                            SET "u"."Name" = SUBSTR("u"."Name" || N'_', 0 + 1, 1)
                            FROM (
                                SELECT "u0"."Id", "u0"."Name"
                                FROM "User" "u0"
                                WHERE CAST(LENGTH("u0"."Name") AS NUMBER(10)) < 10000
                            ) "u1"
                            WHERE (("u"."Id" = "u1"."Id") AND (CAST(LENGTH("u"."Name") AS NUMBER(10)) < 100))
                            RETURNING "u"."Id", "u"."Name"
                            """, query.ToQueryString());
        }

        [TestMethod]
        public void Updated_WhenAndParameter_ReturnUpdateWithOutputClause()
        {
            // Arrange
            using var scope = ServiceProvider.CreateScope();
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
                            DECLARE 
                            l_sql     varchar2(32767);
                            l_cur     pls_integer;
                            l_execute pls_integer;

                            BEGIN
                            l_cur := dbms_sql.open_cursor;
                            l_sql := 'UPDATE "User" "u"
                            SET "u"."Name" = SUBSTR("u"."Name" || N''_'', 0 + 1, 1)
                            FROM (
                                SELECT "u0"."Id", "u0"."Name"
                                FROM "User" "u0"
                                WHERE CAST(LENGTH("u0"."Name") AS NUMBER(10)) < :p
                            ) "u1"
                            WHERE (("u"."Id" = "u1"."Id") AND (CAST(LENGTH("u"."Name") AS NUMBER(10)) < :hundred))
                            RETURNING "u"."Id", "u"."Name"';
                            dbms_sql.parse(l_cur, l_sql, dbms_sql.native);
                            dbms_sql.bind_variable(l_cur, ':p', 10000);
                            dbms_sql.bind_variable(l_cur, ':hundred', 100);
                            l_execute:= dbms_sql.execute(l_cur);
                            dbms_sql.return_result(l_cur);
                            END;

                            """, query.ToQueryString());
        }

        [TestMethod]
        public void Updated_When_ReturnUpdateWithOutputClause()
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
                    x => new { x.Id, Name = x.Name + "_" });

            // Assert
            Assert.AreEqual("""
                            UPDATE "User" "u"
                            SET "u"."Name" = SUBSTR("u"."Name" || N'_', 0 + 1, 1)
                            FROM (
                                SELECT "u0"."Id", "u0"."Name"
                                FROM "User" "u0"
                                WHERE CAST(LENGTH("u0"."Name") AS NUMBER(10)) < 10000
                            ) "u1"
                            WHERE (("u"."Id" = "u1"."Id") AND (CAST(LENGTH("u"."Name") AS NUMBER(10)) < 100))
                            RETURNING "u"."Id", "u"."Name" || N'_' "Name"
                            """, query.ToQueryString());
        }
    }
}