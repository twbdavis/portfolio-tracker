using PortfolioTracker.Core.Data;
using PortfolioTracker.Core.Models;

namespace PortfolioTracker.Core.Tests;

internal static class TestData
{
    public static Account Account(Database db, string number = "...1234", string name = "Taxable",
        TaxTreatment treatment = TaxTreatment.Taxable)
    {
        var repo = new AccountRepository(db);
        var account = new Account
        {
            AccountNumber = number, DisplayName = name, TaxTreatment = treatment,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        repo.Insert(account);
        return account;
    }
}
