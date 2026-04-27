using EFCore.ReturningExtensions.Extensions;
using EFCore.ReturningExtensions.Tests.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EFCore.ReturningExtensions.Tests;

public sealed partial class DeleteWithReturningTests
{
    [TestClass]
    public sealed class Oracle : DeleteWithReturningTests<OracleUsersDbContext>
    {
        [TestMethod]
        public void Deleted_WhenParameter_ReturnDeleteWithOutputClause()
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
                            DECLARE 
                            l_sql     varchar2(32767);
                            l_cur     pls_integer;
                            l_execute pls_integer;

                            BEGIN
                            l_cur := dbms_sql.open_cursor;
                            l_sql := 'DELETE FROM "User" "u5"
                            WHERE "u5"."Id" IN (
                                SELECT "u6"."Id"
                                FROM "User" "u6"
                                INNER JOIN (
                                    SELECT "u8"."Id", "u8"."Name"
                                    FROM "User" "u8"
                                    WHERE CAST(LENGTH("u8"."Name") AS NUMBER(10)) < :p
                                ) "u7" ON "u6"."Id" = "u7"."Id"
                                WHERE (("u6"."Name" = N''A'') OR ("u6"."Name" = N''B''))
                            )
                            RETURNING "u5"."Id", "u5"."Name" || N''_'' "Name"';
                            dbms_sql.parse(l_cur, l_sql, dbms_sql.native);
                            dbms_sql.bind_variable(l_cur, ':p', 10000);
                            l_execute:= dbms_sql.execute(l_cur);
                            dbms_sql.return_result(l_cur);
                            END;

                            """, query.ToQueryString());
        }

        [TestMethod]
        public void Deleted_WhenParameterLess_ReturnDeleteWithOutputClause()
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
                            DELETE FROM "User" "u5"
                            WHERE "u5"."Id" IN (
                                SELECT "u6"."Id"
                                FROM "User" "u6"
                                INNER JOIN (
                                    SELECT "u8"."Id", "u8"."Name"
                                    FROM "User" "u8"
                                    WHERE CAST(LENGTH("u8"."Name") AS NUMBER(10)) < 10000
                                ) "u7" ON "u6"."Id" = "u7"."Id"
                                WHERE (("u6"."Name" = N'A') OR ("u6"."Name" = N'B'))
                            )
                            RETURNING "u5"."Id", "u5"."Name" || N'_' "Name"
                            """, query.ToQueryString());
        }
    }
}