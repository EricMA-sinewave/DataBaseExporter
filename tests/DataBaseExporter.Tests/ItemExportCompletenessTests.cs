using System.Reflection;
using DataBaseExporter.Core.Exporting;

namespace DataBaseExporter.Tests;

[TestClass]
public sealed class ItemExportCompletenessTests
{
    [TestMethod]
    public void BuildRequiredItemTableKeys_IncludesRootAndRelationshipTables()
    {
        var profile = new ItemExportProfile
        {
            RootTable = "t_background",
            RootKeyColumn = "id",
            Relationships =
            [
                new ItemRelationship(null, "t_background", "floor_id", null, "t_floor", "id")
            ]
        };

        var keys = InvokeBuildRequiredItemTableKeys(profile).ToArray();

        CollectionAssert.AreEquivalent(new[] { "t_background", "t_floor" }, keys);
    }

    [TestMethod]
    public void HasRequiredItemTables_ReturnsFalseWhenRelationshipTableHasNoRows()
    {
        var tables = new Dictionary<string, List<IReadOnlyDictionary<string, object?>>>(StringComparer.OrdinalIgnoreCase)
        {
            ["t_background"] = [new Dictionary<string, object?> { ["id"] = 1 }]
        };

        var complete = InvokeHasRequiredItemTables(tables, new[] { "t_background", "t_floor" });

        Assert.IsFalse(complete);
    }

    [TestMethod]
    public void HasRequiredItemTables_ReturnsTrueWhenAllRequiredTablesHaveRows()
    {
        var tables = new Dictionary<string, List<IReadOnlyDictionary<string, object?>>>(StringComparer.OrdinalIgnoreCase)
        {
            ["t_background"] = [new Dictionary<string, object?> { ["id"] = 1 }],
            ["t_floor"] = [new Dictionary<string, object?> { ["id"] = 10 }]
        };

        var complete = InvokeHasRequiredItemTables(tables, new[] { "t_background", "t_floor" });

        Assert.IsTrue(complete);
    }

    private static IEnumerable<string> InvokeBuildRequiredItemTableKeys(ItemExportProfile profile)
    {
        var method = typeof(DatabaseExportService).GetMethod(
            "BuildRequiredItemTableKeys",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.IsNotNull(method);
        return (IEnumerable<string>)method.Invoke(null, new object[] { profile })!;
    }

    private static bool InvokeHasRequiredItemTables(
        IReadOnlyDictionary<string, List<IReadOnlyDictionary<string, object?>>> tables,
        IReadOnlyCollection<string> requiredTableKeys)
    {
        var method = typeof(DatabaseExportService).GetMethod(
            "HasRequiredItemTables",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.IsNotNull(method);
        return (bool)method.Invoke(null, new object[] { tables, requiredTableKeys })!;
    }
}
