using GseTechPro.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace GseTechPro.Tests;

/// <summary>
/// Phase 10C.4: PCS transmission, PCS disc parking brake and Ford MSG-425 trees for the TUG 660-28.
/// Checks the rows that matter for safety and the "no made-up specs" rule after a real seed.
/// </summary>
public sealed class GseComponentTreeTests : IAsyncLifetime
{
    private const string Todo = "TODO: needs spec from manual";
    private string _dbPath = "";

    public async Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"gse-10c4-{Guid.NewGuid():N}", "gse.db");
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
        await new DataServices(new DatabaseOptions { DatabasePath = _dbPath }).InitializeAsync();
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(Path.GetDirectoryName(_dbPath)!, recursive: true); } catch (IOException) { }
        return Task.CompletedTask;
    }

    private object? Scalar(string sql)
    {
        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    [Theory]
    [InlineData("tree-tug66028-trans-no-engage", "sym-trans-no-engage", 27)]
    [InlineData("tree-tug66028-park-brake-burnish", "sym-park-brake-weak", 11)]
    [InlineData("tree-tug66028-ford-mil", "sym-engine-mil-on", 9)]
    public void Tree_IsSeeded_AsDraft_ForThe66028(string treeId, string symptomId, int steps)
    {
        Assert.Equal("eqm-tug-660-28", Scalar($"SELECT equipment_model_id FROM troubleshooting_trees WHERE id='{treeId}'"));
        Assert.Equal(symptomId, Scalar($"SELECT symptom_id FROM troubleshooting_trees WHERE id='{treeId}'"));
        Assert.Equal("draft", Scalar($"SELECT status FROM troubleshooting_trees WHERE id='{treeId}'"));
        Assert.Equal((long)steps, Scalar($"SELECT COUNT(*) FROM troubleshooting_steps WHERE tree_id='{treeId}'"));
    }

    [Fact]
    public void LinePressure_UsesTheCitedPcsRanges_AndOtherNewPointsStayTodo()
    {
        Assert.Equal(170.0, Convert.ToDouble(Scalar("SELECT spec_min FROM test_points WHERE id='tp-tug66028-trans-line-pressure-4lhd'")));
        Assert.Equal(193.0, Convert.ToDouble(Scalar("SELECT spec_max FROM test_points WHERE id='tp-tug66028-trans-line-pressure-4lhd'")));
        Assert.Equal(198.0, Convert.ToDouble(Scalar("SELECT spec_min FROM test_points WHERE id='tp-tug66028-trans-line-pressure-4lhdx'")));
        Assert.Equal(227.0, Convert.ToDouble(Scalar("SELECT spec_max FROM test_points WHERE id='tp-tug66028-trans-line-pressure-4lhdx'")));
        Assert.Equal(0L, Scalar("""
            SELECT COUNT(*) FROM test_points WHERE id LIKE 'tp-tug66028-trans-line-pressure-%'
              AND spec_source NOT LIKE 'PCS 4-Speed Transmission Quick Reference Guide v1.3, p.2,%';
            """));
        Assert.Equal(Todo, Scalar("SELECT spec_source FROM test_points WHERE id='tp-tug66028-tcm-ignition-feed'"));
        Assert.Equal(DBNull.Value, Scalar("SELECT spec_min FROM test_points WHERE id='tp-tug66028-tcm-ignition-feed'"));
    }

    [Fact]
    public void HazardousSteps_CarryTheirWarnings()
    {
        // Every line-pressure step warns about >300 psi; the TDD step warns about sudden movement; the burnish
        // step (dynamic stops) warns about aircraft proximity; brake work warns about lining dust.
        Assert.Equal(0L, Scalar("""
            SELECT COUNT(*) FROM troubleshooting_steps WHERE test_point_id LIKE 'tp-tug66028-trans-line-pressure-%'
              AND safety_warning_ids NOT LIKE '%sw-trans-line-pressure%';
            """));
        Assert.Contains("sw-trans-direct-control", (string)Scalar("SELECT safety_warning_ids FROM troubleshooting_steps WHERE id='st-tug66028-trans-ne-14-forward-solenoid'")!);
        Assert.Contains("sw-aircraft-proximity", (string)Scalar("SELECT safety_warning_ids FROM troubleshooting_steps WHERE id='st-tug66028-park-brake-04-burnish'")!);
        Assert.Contains("sw-brake-dust", (string)Scalar("SELECT safety_warning_ids FROM troubleshooting_steps WHERE id='st-tug66028-park-brake-02-inspect'")!);
        Assert.Equal(3L, Scalar("SELECT COUNT(*) FROM safety_warnings WHERE id IN ('sw-trans-direct-control','sw-trans-line-pressure','sw-brake-dust') AND source_citation LIKE '%p.%';"));
    }

    [Fact]
    public void ShiftInhibitTerminal_ForbidsBypassing_AndMarksThePinTodo()
    {
        var text = (string)Scalar("SELECT terminal_result FROM troubleshooting_steps WHERE id='st-tug66028-trans-ne-t09-inhibit'")!;
        Assert.Contains("Do not jumper or bypass", text);
        Assert.Contains(Todo, text);
    }
}
