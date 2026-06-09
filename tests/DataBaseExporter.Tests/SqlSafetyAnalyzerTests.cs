using DataBaseExporter.Core.Database;

namespace DataBaseExporter.Tests;

[TestClass]
public sealed class SqlSafetyAnalyzerTests
{
    [TestMethod]
    public void Analyze_AcceptsSimpleSelect()
    {
        var analyzer = new SqlSafetyAnalyzer();

        var result = analyzer.Analyze("select id, name from users", requireReadOnly: true, refuseMultipleStatements: true);

        Assert.IsTrue(result.IsAccepted, result.Reason);
    }

    [TestMethod]
    public void Analyze_RejectsDelete()
    {
        var analyzer = new SqlSafetyAnalyzer();

        var result = analyzer.Analyze("delete from users", requireReadOnly: true, refuseMultipleStatements: true);

        Assert.IsFalse(result.IsAccepted);
        StringAssert.Contains(result.Reason, "read-only");
    }

    [TestMethod]
    public void Analyze_RejectsMultipleStatements()
    {
        var analyzer = new SqlSafetyAnalyzer();

        var result = analyzer.Analyze("select * from users; select * from orders", requireReadOnly: true, refuseMultipleStatements: true);

        Assert.IsFalse(result.IsAccepted);
        StringAssert.Contains(result.Reason, "Multiple");
    }

    [TestMethod]
    public void Analyze_IgnoresDangerousWordsInsideStringLiteral()
    {
        var analyzer = new SqlSafetyAnalyzer();

        var result = analyzer.Analyze("select 'drop table users' as text", requireReadOnly: true, refuseMultipleStatements: true);

        Assert.IsTrue(result.IsAccepted, result.Reason);
    }
}
