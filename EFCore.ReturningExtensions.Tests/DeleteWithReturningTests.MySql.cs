using EFCore.ReturningExtensions.Extensions;
using EFCore.ReturningExtensions.Tests.Data;
using Microsoft.EntityFrameworkCore;

namespace EFCore.ReturningExtensions.Tests;

public sealed partial class DeleteWithReturningTests
{
    [TestClass]
    public sealed class MySql : DeleteWithReturningTests<MySqlUsersDbContext>
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
                            SET @__p_0 = 10000;
                            
                            DELETE
                            FROM `User` AS `u`
                            WHERE ((`u`.`Name` = 'A') OR (`u`.`Name` = 'B')) AND (CHAR_LENGTH(`u`.`Name`) < @__p_0)
                            RETURNING `u`.`Id`, CONCAT(`u`.`Name`, '_') AS `Name`
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
                            DELETE
                            FROM `User` AS `u`
                            WHERE ((`u`.`Name` = 'A') OR (`u`.`Name` = 'B')) AND (CHAR_LENGTH(`u`.`Name`) < 10000)
                            RETURNING `u`.`Id`, CONCAT(`u`.`Name`, '_') AS `Name`
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
            Assert.AreEqual("""
                             DELETE
                             FROM `Account` AS `a``AccountType` AS `a0`
                             `AccountSubType` AS `a1`
                             WHERE ((((CHAR_LENGTH(`a`.`Name`) < 100) AND (CHAR_LENGTH(`a0`.`TypeName`) < 1000)) AND (CHAR_LENGTH(`a1`.`StName`) < 10000)) AND (`a`.`TypeId` = `a0`.`Id`)) AND (`a0`.`SubTypeId` = `a1`.`Id`)
                             RETURNING `a`.`Id`, `a`.`Name`, `a`.`TypeId`, `a0`.`Id`, `a0`.`SubTypeId`, `a0`.`TypeName`, `a1`.`Id`, `a1`.`StName`
                             """, query.ToQueryString());
        }

        [TestMethod]
        public void Deleted_When_ReturnDeleteWithOutputClause()
        {
            // Arrange
            using var ctx = GetDbContext();

            // Act
            var query = ctx.Accounts
                .AsNoTracking()
                .Where(p => p.Name.Length < 100)
                .Deleted(x => x);

            // Assert
            Assert.AreEqual("""
                            DELETE
                            FROM `Account` AS `a`
                            WHERE CHAR_LENGTH(`a`.`Name`) < 100
                            RETURNING `a`.`Id`, `a`.`Name`, `a`.`TypeId`
                            """, query.ToQueryString());
        }

        [TestMethod]
        public void Deleted_WhenOrderAndLimit_ReturnDeleteWithOutputClause()
        {
            // Arrange
            using var ctx = GetDbContext();

            // Act
            var query = ctx.Accounts
                .AsNoTracking()
                .Where(p => p.Name.Length < 100)
                .OrderBy(x => x.Id)
                .Take(10)
                .Deleted(x => x);

            // Assert
            Assert.AreEqual("""
                            
                            DELETE
                            FROM `Account` AS `a`
                            WHERE CHAR_LENGTH(`a`.`Name`) < 100
                            ORDER BY `a`.`Id`
                            LIMIT 10
                            RETURNING `a`.`Id`, `a`.`Name`, `a`.`TypeId`
                            """, query.ToQueryString());
        }
    }
}