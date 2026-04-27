using EFCore.ReturningExtensions.Extensions;
using EFCore.ReturningExtensions.Tests.Data;
using Microsoft.EntityFrameworkCore;

namespace EFCore.ReturningExtensions.Tests;

public sealed partial class UpdateWithReturningTests
{
    [TestClass]
    public sealed class MySql : UpdateWithReturningTests<MySqlUsersDbContext>
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
                            UPDATE `User` AS `u`
                            SET `u`.`Name` = SUBSTRING(CONCAT(`u`.`Name`, '_'), 0 + 1, 1)
                            WHERE (CHAR_LENGTH(`u`.`Name`) < 100) AND (CHAR_LENGTH(`u`.`Name`) < 10000)
                            RETURNING OLD_VALUE(`Id`), CONCAT(CONCAT(`u`.`Name`, '<|'), OLD_VALUE(`Name`)) AS `Name2`
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
                    (x, y) => y);

            // Assert
            Assert.AreEqual("""
                            UPDATE `User` AS `u`
                            SET `u`.`Name` = SUBSTRING(CONCAT(`u`.`Name`, '_'), 0 + 1, 1)
                            WHERE (CHAR_LENGTH(`u`.`Name`) < 100) AND (CHAR_LENGTH(`u`.`Name`) < 10000)
                            RETURNING OLD_VALUE(`Id`), OLD_VALUE(`Name`)
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
                            UPDATE `User` AS `u`
                            SET `u`.`Name` = SUBSTRING(CONCAT(`u`.`Name`, '_'), 0 + 1, 1)
                            WHERE (CHAR_LENGTH(`u`.`Name`) < 100) AND (CHAR_LENGTH(`u`.`Name`) < 10000)
                            RETURNING `u`.`Id`, `u`.`Name`
                            """, query.ToQueryString());
        }

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
                            SET @__hundred_0 = 100;
                            SET @__p_1 = 10000;
                            
                            UPDATE `User` AS `u`
                            SET `u`.`Name` = SUBSTRING(CONCAT(`u`.`Name`, '_'), 0 + 1, 1)
                            WHERE (CHAR_LENGTH(`u`.`Name`) < @__hundred_0) AND (CHAR_LENGTH(`u`.`Name`) < @__p_1)
                            RETURNING `u`.`Id`, `u`.`Name`
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
                            UPDATE `User` AS `u`
                            SET `u`.`Name` = SUBSTRING(CONCAT(`u`.`Name`, '_'), 0 + 1, 1)
                            WHERE (CHAR_LENGTH(`u`.`Name`) < 100) AND (CHAR_LENGTH(`u`.`Name`) < 10000)
                            RETURNING `u`.`Id`, `u`.`Name`
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
                             UPDATE `Account` AS `a`,
                             CROSS JOIN `AccountType` AS `a0`
                             `AccountSubType` AS `a1`
                             SET `a`.`Name` = SUBSTRING(CONCAT(`a`.`Name`, '_'), 0 + 1, 1)
                             WHERE ((((CHAR_LENGTH(`a`.`Name`) < 100) AND (CHAR_LENGTH(`a0`.`TypeName`) < 1000)) AND (CHAR_LENGTH(`a1`.`StName`) < 10000)) AND (`a`.`TypeId` = `a0`.`Id`)) AND (`a0`.`SubTypeId` = `a1`.`Id`)
                             RETURNING `a`.`Id`, `a`.`Name`, `a`.`TypeId`, `a0`.`Id`, `a0`.`SubTypeId`, `a0`.`TypeName`, `a1`.`Id`, `a1`.`StName`
                             """, query.ToQueryString());
        }

        [TestMethod]
        public void Updated_When_ReturnUpdateWithOutputClause()
        {
            // Arrange
            using var ctx = GetDbContext();

            // Act
            var query = ctx.Accounts
                .AsNoTracking()
                .Where(p => p.Name.Length < 100)
                .Updated(s =>
                        s.SetProperty(p => p.Name, p => (p.Name + "_").Substring(0, 1)),
                    x => new { x.Id, Name = x.Name + "_" });

            // Assert
            Assert.AreEqual("""
                             UPDATE `Account` AS `a`
                             SET `a`.`Name` = SUBSTRING(CONCAT(`a`.`Name`, '_'), 0 + 1, 1)
                             WHERE CHAR_LENGTH(`a`.`Name`) < 100
                             RETURNING `a`.`Id`, CONCAT(`a`.`Name`, '_') AS `Name`
                             """, query.ToQueryString());
        }

        [TestMethod]
        public void Updated_WhenOrderByAndLimit_ReturnUpdateWithOutputClause()
        {
            // Arrange
            using var ctx = GetDbContext();

            // Act
            var query = ctx.Accounts
                .AsNoTracking()
                .Where(p => p.Name.Length < 100)
                .OrderBy(x => x.Id)
                .Take(10)
                .Updated(s =>
                        s.SetProperty(p => p.Name, p => (p.Name + "_").Substring(0, 1)),
                    x => new { x.Id, Name = x.Name + "_" });

            // Assert
            Assert.AreEqual("""
                             
                             UPDATE `Account` AS `a`
                             SET `a`.`Name` = SUBSTRING(CONCAT(`a`.`Name`, '_'), 0 + 1, 1)
                             WHERE CHAR_LENGTH(`a`.`Name`) < 100
                             ORDER BY `a`.`Id`
                             LIMIT 10
                             RETURNING `a`.`Id`, CONCAT(`a`.`Name`, '_') AS `Name`
                             """, query.ToQueryString());
        }
    }
}