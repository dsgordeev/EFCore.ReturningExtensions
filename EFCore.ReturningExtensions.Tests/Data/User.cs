namespace EFCore.ReturningExtensions.Tests.Data
{
    public class User
    {
        public long Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    public class Account
    {
        public long Id { get; set; }
        public string Name { get; set; } = string.Empty;

        public long TypeId { get; set; }
        public AccountType Type { get; set; } = null!;
    }

    public class AccountType
    {
        public long Id { get; set; }
        public string TypeName { get; set; } = string.Empty;

        public long SubTypeId { get; set; }
        public AccountSubType SubType { get; set; } = null!;
    }

    public class AccountSubType
    {
        public long Id { get; set; }
        public string StName { get; set; } = string.Empty;
    }
}
