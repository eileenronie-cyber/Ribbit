using Ribbit.Geometry.Sheets;
using Xunit;

namespace Ribbit.Tests;

/// <summary>
/// Part Sheets Duplicator's plan: what each worksheet row will do, decided
/// before anything is written. The window shows this plan and the Revit side
/// carries it out, so these are the tool's decisions, not a model of them.
/// Row texts follow the real workbook (Downloads\Duplicator.xlsx).
/// </summary>
public class DuplicatorPlannerTests
{
    private const string Base = "Sh - 58 - Fire Protection Layout, DC Building 1";

    private static readonly DupTemplate FpA = new(900, "Sheet - 58 Series - Fire Protection - Layout", true, "Floor Plan");
    private static readonly DupTemplate FpB = new(901, "Sheet - 58 Series - Fire Protection - Zoning", true, "Floor Plan");
    private static readonly DupTemplate Rcp = new(902, "Sheet - 63 Series - General Lighting - RCP", false, "Ceiling Plan");

    private static readonly Dictionary<string, long> AllBases = new() { ["1"] = 100, ["2"] = 101, ["roof"] = 102 };

    private static DupRow R(int row, string fpv, string template = "", string phase = "")
        => new(row, "58 Series - Fire Protection", fpv, phase, template);

    private static DupModel M(params DupView[] views) => new(views, [FpA, FpB, Rcp]);

    private static DupPlan Plan(IReadOnlyList<DupRow> rows, DupModel? model = null,
        Dictionary<string, long>? bases = null, ExistingMode mode = ExistingMode.Skip)
        => DuplicatorPlanner.Plan(rows, model ?? M(), bases ?? AllBases, mode);

    private static DupStep Row(DupPlan p, int excelRow) => p.Steps.Single(s => s.ExcelRow == excelRow);

    // ------------------------------------------------------------ creating

    [Fact]
    public void AnOverallRowAndItsSheets_MakeOneParentAndDependentsUnderIt()
    {
        DupPlan p = Plan(
        [
            R(2, $"{Base}, Level 01, Overall", FpA.Name),
            R(3, $"{Base}, Level 01, Sheet 1 of 2", FpA.Name),
            R(4, $"{Base}, Level 01, Sheet 2 of 2", FpA.Name),
        ]);

        DupStep parent = Row(p, 2);
        Assert.Equal(DupAction.CreateParent, parent.Action);
        Assert.Equal(100, parent.BaseViewId);
        Assert.Equal(TemplatePlan.Apply, parent.Template);
        Assert.Equal(900, parent.TemplateId);

        int parentIndex = p.Steps.IndexOf(parent);
        foreach (int r in new[] { 3, 4 })
        {
            DupStep d = Row(p, r);
            Assert.Equal(DupAction.CreateDependent, d.Action);
            Assert.Equal(parentIndex, d.ParentStep);
            Assert.Equal($"{Base}, Level 01, Overall", d.ParentName);
            Assert.True(d.MirrorFromParent);
            // A dependent shows its parent's template: none is set on it.
            Assert.Equal(TemplatePlan.FromParent, d.Template);
            Assert.Equal(900, d.TemplateId);
            Assert.Empty(d.Notes);
        }

        Assert.Equal(new DupCounts(3, 1, 2, 0, 0, 0, 0, 0, 1), p.Counts);
    }

    // The old engine never registered a parent during a dry run, so every sheet
    // on a level "auto-created" its own Overall in the preview.
    [Fact]
    public void SheetsWithNoOverallRow_ShareOneAutomaticOverall()
    {
        DupPlan p = Plan(
        [
            R(5, $"{Base}, Level 02, Sheet 1 of 3", FpA.Name),
            R(6, $"{Base}, Level 02, Sheet 2 of 3", FpA.Name),
            R(7, $"{Base}, Level 02, Sheet 3 of 3", FpA.Name),
        ]);

        DupStep auto = Assert.Single(p.Steps, s => s.IsAutomatic);
        Assert.Equal(DupAction.CreateParent, auto.Action);
        Assert.Equal($"{Base}, Level 02, Overall", auto.Name);
        Assert.Equal(5, auto.NeededByRow);
        Assert.Equal(101, auto.BaseViewId);
        Assert.All(p.Steps.Where(s => !s.IsAutomatic), d => Assert.Equal(p.Steps.IndexOf(auto), d.ParentStep));
        Assert.Equal(1, p.Counts.CreateParents);
        Assert.Equal(1, p.Counts.Automatic);
        Assert.Equal(3, p.Counts.CreateDependents);
    }

    [Fact]
    public void ASheetAboveItsOverallRow_StillUsesThatRowsOverall()
    {
        DupPlan p = Plan(
        [
            R(2, $"{Base}, Level 01, Sheet 1 of 2", FpA.Name),
            R(3, $"{Base}, Level 01, Overall", FpB.Name),
        ]);

        Assert.DoesNotContain(p.Steps, s => s.IsAutomatic);
        DupStep parent = Row(p, 3);
        Assert.Equal(DupAction.CreateParent, parent.Action);
        Assert.Equal(901, parent.TemplateId);
        Assert.Equal(p.Steps.IndexOf(parent), Row(p, 2).ParentStep);
        Assert.True(p.Steps.IndexOf(parent) < p.Steps.IndexOf(Row(p, 2)), "a parent must run before its dependent");
    }

    [Fact]
    public void APlainRow_IsAnIndependentViewFromTheBaseView()
    {
        DupPlan p = Plan([R(2, "Sh - 25 - MEP Phasing Diagram Standard Strategy, DC Building 1, Roof Level")]);
        DupStep s = Row(p, 2);
        Assert.Equal(DupAction.CreateParent, s.Action);
        Assert.Equal(102, s.BaseViewId);
        Assert.Equal(TemplatePlan.None, s.Template);
    }

    [Fact]
    public void ThePhase_IsAppended_AndSheetsFindTheirOverallByTheSameName()
    {
        DupPlan p = Plan(
        [
            R(2, "Drainage Layout, Level 01, Overall", phase: "Phase 01"),
            R(3, "Drainage Layout, Level 01, Sheet 1 of 4", phase: "Phase 01"),
        ]);

        Assert.Equal("Drainage Layout, Level 01, Overall, Phase 01", Row(p, 2).Name);
        Assert.Equal("Drainage Layout, Level 01, Sheet 1 of 4, Phase 01", Row(p, 3).Name);
        Assert.Equal(p.Steps.IndexOf(Row(p, 2)), Row(p, 3).ParentStep);
        Assert.DoesNotContain(p.Steps, s => s.IsAutomatic);
    }

    // The real workbook's "Overview" rows are not "Overall": the sheets get an
    // Overall of their own. The plan must SHOW that, not hide it.
    [Fact]
    public void AnOverviewRow_IsNotTheSheetsOverall_AndThePlanShowsTheExtraView()
    {
        DupPlan p = Plan(
        [
            R(2, "Sh - 70 - MEP Primary Plant Layout, DC Building 1, Roof Level, Overview"),
            R(3, "Sh - 70 - MEP Primary Plant Layout, DC Building 1, Roof Level, Sheet 1 of 3"),
        ]);

        Assert.Equal(DupAction.CreateParent, Row(p, 2).Action);
        DupStep auto = Assert.Single(p.Steps, s => s.IsAutomatic);
        Assert.EndsWith("Roof Level, Overall", auto.Name);
        Assert.Equal(2, p.Counts.CreateParents);
    }

    // ------------------------------------------------------------ existing

    [Fact]
    public void AnExistingOverall_IsUsed_AndItsLevelNeedsNoBaseView()
    {
        DupModel m = M(new DupView(500, $"{Base}, Level 01, Overall", 0, 0));
        DupPlan p = Plan(
            [R(2, $"{Base}, Level 01, Sheet 1 of 2"), R(3, $"{Base}, Level 01, Sheet 2 of 2")],
            m, bases: []);

        Assert.All(p.Steps, s => Assert.Equal(DupAction.CreateDependent, s.Action));
        Assert.All(p.Steps, s => Assert.Equal(500, s.ParentId));
        DupLevel level = Assert.Single(p.Levels);
        Assert.False(level.NeedsBaseView);
        Assert.Empty(p.MissingBaseViews);
    }

    [Fact]
    public void AnExistingView_IsLeftAloneInSkipMode()
    {
        DupModel m = M(new DupView(500, $"{Base}, Level 01, Overall", 0, 901));
        DupStep s = Row(Plan([R(2, $"{Base}, Level 01, Overall", FpA.Name)], m), 2);
        Assert.Equal(DupAction.Exists, s.Action);
        Assert.Equal(TemplatePlan.LeftAlone, s.Template);
        Assert.False(s.Writes);
    }

    [Fact]
    public void AnExistingView_GetsItsTemplateInUpdateMode()
    {
        DupModel m = M(new DupView(500, $"{Base}, Level 01, Overall", 0, 901));
        DupStep s = Row(Plan([R(2, $"{Base}, Level 01, Overall", FpA.Name)], m, mode: ExistingMode.Update), 2);
        Assert.Equal(DupAction.Update, s.Action);
        Assert.Equal(500, s.ExistingId);
        Assert.Equal(TemplatePlan.Apply, s.Template);
        Assert.Equal(900, s.TemplateId);
        Assert.True(s.Writes);
    }

    [Fact]
    public void UpdatingAViewThatAlreadyHasTheTemplate_WritesNothing()
    {
        DupModel m = M(new DupView(500, $"{Base}, Level 01, Overall", 0, 900));
        DupStep s = Row(Plan([R(2, $"{Base}, Level 01, Overall", FpA.Name)], m, mode: ExistingMode.Update), 2);
        Assert.Equal(TemplatePlan.AlreadySet, s.Template);
        Assert.False(s.Writes);
    }

    [Fact]
    public void AnExistingSheet_IsUpdatedAndMirrorsItsOverall()
    {
        DupModel m = M(
            new DupView(500, $"{Base}, Level 01, Overall", 0, 0),
            new DupView(501, $"{Base}, Level 01, Sheet 1 of 2", 500, 0));
        DupStep s = Row(Plan([R(2, $"{Base}, Level 01, Sheet 1 of 2", FpA.Name)], m, mode: ExistingMode.Update), 2);
        Assert.Equal(DupAction.Update, s.Action);
        Assert.Equal(500, s.ParentId);
        Assert.True(s.MirrorFromParent);
    }

    // Before: every sheet row re-stamped the parent's template on its own.
    [Fact]
    public void AnExistingOverallWithNoRow_IsUpdatedOnce_InUpdateModeOnly()
    {
        DupModel m = M(new DupView(500, $"{Base}, Level 01, Overall", 0, 0));
        DupRow[] rows =
        [
            R(2, $"{Base}, Level 01, Sheet 1 of 3", FpA.Name),
            R(3, $"{Base}, Level 01, Sheet 2 of 3", FpA.Name),
            R(4, $"{Base}, Level 01, Sheet 3 of 3", FpA.Name),
        ];

        DupStep auto = Assert.Single(Plan(rows, m, mode: ExistingMode.Update).Steps, s => s.IsAutomatic);
        Assert.Equal(DupAction.Update, auto.Action);
        Assert.Equal(500, auto.ExistingId);

        Assert.DoesNotContain(Plan(rows, m, mode: ExistingMode.Skip).Steps, s => s.IsAutomatic);
    }

    [Fact]
    public void AnExistingSheetWithNoOverall_NeverCausesAnOverallToBeMade()
    {
        DupModel m = M(new DupView(501, $"{Base}, Level 01, Sheet 1 of 2", 0, 0));
        DupPlan p = Plan([R(2, $"{Base}, Level 01, Sheet 1 of 2")], m);
        Assert.DoesNotContain(p.Steps, s => s.Creates);
        Assert.False(Assert.Single(p.Levels).NeedsBaseView);
    }

    [Fact]
    public void AnExistingName_MatchesWhateverItsCase()
    {
        DupModel m = M(new DupView(500, $"{Base}, LEVEL 01, OVERALL", 0, 0));
        Assert.Equal(DupAction.Exists, Row(Plan([R(2, $"{Base}, Level 01, Overall")], m), 2).Action);
    }

    // Before: the dependent Overall was skipped as a parent, then "Overall (1)"
    // was made next to it on every run.
    [Fact]
    public void AnOverallThatIsItselfADependent_IsAnError_NotADuplicate()
    {
        DupModel m = M(
            new DupView(400, "Some primary view", 0, 0),
            new DupView(500, $"{Base}, Level 01, Overall", 400, 0));
        DupPlan p = Plan([R(2, $"{Base}, Level 01, Sheet 1 of 2")], m);
        DupStep s = Row(p, 2);
        Assert.Equal(DupAction.Error, s.Action);
        Assert.Contains("itself a dependent", s.Notes[0]);
        Assert.DoesNotContain(p.Steps, x => x.Creates);
    }

    [Fact]
    public void AnExistingSheetUnderAnotherParent_SaysSoWhenUpdated()
    {
        DupModel m = M(
            new DupView(400, "Old parent", 0, 0),
            new DupView(500, $"{Base}, Level 01, Overall", 0, 0),
            new DupView(501, $"{Base}, Level 01, Sheet 1 of 2", 400, 0));
        DupStep s = Row(Plan([R(2, $"{Base}, Level 01, Sheet 1 of 2")], m, mode: ExistingMode.Update), 2);
        Assert.Contains(s.Notes, n => n.Contains("\"Old parent\""));
    }

    // ------------------------------------------------------------ sister sheets

    // What a real run on the 91-series model did: Sheet 1-3 of 3 hang under
    // "..., Level 01" (no "Overall" anywhere), and the new Sheet 3 of 4 got an
    // Overall of its own. It belongs beside its sisters.
    private static DupModel Sisters() => M(
        new DupView(400, "Sh - 91 - MEP Builders Work, Level 01", 0, 0),
        new DupView(401, $"{Base}, Level 01, Sheet 1 of 3", 400, 0),
        new DupView(402, $"{Base}, Level 01, Sheet 2 of 3", 400, 0),
        new DupView(403, $"{Base}, Level 01, Sheet 3 of 3", 400, 0));

    [Fact]
    public void ANewSheet_GoesUnderTheParentItsSistersUse_NotANewOverall()
    {
        DupPlan p = Plan([R(2, $"{Base}, Level 01, Sheet 1 of 3"), R(3, $"{Base}, Level 01, Sheet 3 of 4")], Sisters(), bases: []);
        DupStep s = Row(p, 3);
        Assert.Equal(DupAction.CreateDependent, s.Action);
        Assert.Equal(400, s.ParentId);
        Assert.True(s.ParentFromSisters);
        Assert.Equal("Sh - 91 - MEP Builders Work, Level 01", s.ParentName);
        Assert.Contains(s.Notes, n => n.Contains("3 sister sheets"));
        Assert.DoesNotContain(p.Steps, x => x.Action == DupAction.CreateParent);
        Assert.False(Assert.Single(p.Levels).NeedsBaseView);
    }

    [Fact]
    public void SistersSplitAcrossParents_GoWithTheMost()
    {
        DupModel m = M(
            new DupView(400, "Parent A", 0, 0),
            new DupView(410, "Parent B", 0, 0),
            new DupView(401, $"{Base}, Level 01, Sheet 1 of 3", 410, 0),
            new DupView(402, $"{Base}, Level 01, Sheet 2 of 3", 400, 0),
            new DupView(403, $"{Base}, Level 01, Sheet 3 of 3", 400, 0));
        Assert.Equal(400, Row(Plan([R(2, $"{Base}, Level 01, Sheet 3 of 4")], m), 2).ParentId);
    }

    [Fact]
    public void AnOverallByName_StillWinsOverSisters()
    {
        DupModel m = M([.. Sisters().FloorPlans, new DupView(500, $"{Base}, Level 01, Overall", 0, 0)]);
        DupStep s = Row(Plan([R(2, $"{Base}, Level 01, Sheet 3 of 4")], m), 2);
        Assert.Equal(500, s.ParentId);
        Assert.False(s.ParentFromSisters);
    }

    [Fact]
    public void AnOverallRow_StillWinsOverSisters()
    {
        DupPlan p = Plan([R(2, $"{Base}, Level 01, Overall"), R(3, $"{Base}, Level 01, Sheet 3 of 4")], Sisters());
        Assert.Equal(p.Steps.IndexOf(Row(p, 2)), Row(p, 3).ParentStep);
    }

    [Fact]
    public void ASisterUnderADependentView_IsNoParent()
    {
        DupModel m = M(
            new DupView(300, "Top", 0, 0),
            new DupView(400, "Cropped piece", 300, 0),
            new DupView(401, $"{Base}, Level 01, Sheet 1 of 3", 400, 0));
        DupPlan p = Plan([R(2, $"{Base}, Level 01, Sheet 3 of 4")], m);
        Assert.True(Assert.Single(p.Steps, s => s.IsAutomatic).Creates);
    }

    [Theory]
    [InlineData("A, Level 01, Sheet 1 of 3", "a, level 01")]
    [InlineData("A, Level 01, Sheet 3 of 4", "a, level 01")]
    [InlineData("A, Level 01, Sheet 2 of 3, Phase 1", "a, level 01, phase 1")]
    [InlineData("A Level 01 Sheet 1 of 2", "a level 01")]
    public void SheetFamilyKey_DropsOnlyTheSheetPart(string name, string key)
        => Assert.Equal(key, ViewNaming.SheetFamilyKey(name));

    // ------------------------------------------------------------ templates on dependents

    // Proven in Revit: a template set on a new sheet under an untemplated
    // parent read back as none. The plan says what will really show.
    [Fact]
    public void ASheetUnderAParentWithAnotherTemplate_SaysItShowsTheParents()
    {
        DupPlan p = Plan([R(2, $"{Base}, Level 01, Overall", FpB.Name), R(3, $"{Base}, Level 01, Sheet 1 of 2", FpA.Name)]);
        DupStep s = Row(p, 3);
        Assert.Equal(901, s.TemplateId);
        Assert.Equal(FpB.Name, s.TemplateName);
        Assert.Contains(s.Notes, n => n.Contains($"\"{FpA.Name}\" does not show"));
    }

    [Fact]
    public void ASheetUnderAnExistingUntemplatedParent_ShowsNone()
    {
        DupModel m = M(new DupView(500, $"{Base}, Level 01, Overall", 0, 0));
        DupStep s = Row(Plan([R(2, $"{Base}, Level 01, Sheet 1 of 2", FpA.Name)], m), 2);
        Assert.Equal(0, s.TemplateId);
        Assert.Contains(s.Notes, n => n.Contains("template (none)"));
    }

    [Fact]
    public void AnAutomaticOverallWithNoTemplate_KeepsItsBaseViewsTemplate_AndItsSheetsShowIt()
    {
        DupModel m = M(new DupView(100, "Base 01", 0, 901));
        DupStep s = Row(Plan([R(2, $"{Base}, Level 01, Sheet 1 of 2")], m), 2);
        Assert.Equal(901, s.TemplateId);
    }

    [Fact]
    public void UpdatingAnExistingSheet_NeverSetsItsTemplate()
    {
        DupModel m = M(
            new DupView(500, $"{Base}, Level 01, Overall", 0, 900),
            new DupView(501, $"{Base}, Level 01, Sheet 1 of 2", 500, 0));
        DupStep s = Row(Plan([R(2, $"{Base}, Level 01, Sheet 1 of 2", FpB.Name)], m, mode: ExistingMode.Update), 2);
        Assert.Equal(TemplatePlan.FromParent, s.Template);
        // Its Overall has no row, so Update gives the Overall this sheet's
        // template (B) - and the sheet shows it through its parent.
        Assert.Equal(901, s.TemplateId);
        Assert.True(s.Writes);   // still mirrors the B&W values
    }

    // ------------------------------------------------------------ the user's choices

    private static Dictionary<int, RowChoice> Choose(params (int Row, RowChoice Choice)[] c)
        => c.ToDictionary(x => x.Row, x => x.Choice);

    private static DupPlan Plan(IReadOnlyList<DupRow> rows, Dictionary<int, RowChoice> choices, DupModel? model = null,
        Dictionary<string, long>? bases = null)
        => DuplicatorPlanner.Plan(rows, model ?? M(), bases ?? AllBases, ExistingMode.Skip, choices);

    [Fact]
    public void ASheetRow_CanBeMadeAnIndependentView()
    {
        DupPlan p = Plan([R(2, $"{Base}, Level 01, Sheet 1 of 2", FpA.Name)], Choose((2, new RowChoice(RowChoiceKind.Independent))));
        DupStep s = Row(p, 2);
        Assert.Equal(DupAction.CreateParent, s.Action);
        Assert.Equal(100, s.BaseViewId);
        Assert.Equal(TemplatePlan.Apply, s.Template);
        Assert.True(s.ByYou);
        Assert.DoesNotContain(p.Steps, x => x.IsAutomatic);
    }

    // The real workbook's case: sheets that belong under an "Overview" row.
    [Fact]
    public void Sheets_CanBePutUnderAnotherRowsView()
    {
        string overview = "Sh - 70 - MEP Primary Plant Layout, DC Building 1, Roof Level, Overview";
        RowChoice under = new(RowChoiceKind.DependentOf, overview);
        DupPlan p = Plan(
        [
            R(2, overview, FpA.Name),
            R(3, "Sh - 70 - MEP Primary Plant Layout, DC Building 1, Roof Level, Sheet 1 of 3"),
            R(4, "Sh - 70 - MEP Primary Plant Layout, DC Building 1, Roof Level, Sheet 2 of 3"),
        ], Choose((3, under), (4, under)));

        int parent = p.Steps.IndexOf(Row(p, 2));
        Assert.All(new[] { Row(p, 3), Row(p, 4) }, s =>
        {
            Assert.Equal(DupAction.CreateDependent, s.Action);
            Assert.Equal(parent, s.ParentStep);
            Assert.True(s.ByYou);
        });
        Assert.DoesNotContain(p.Steps, x => x.IsAutomatic);
        Assert.Equal(1, p.Counts.CreateParents);
    }

    [Fact]
    public void ARow_CanBePutUnderAnExistingViewWithAnyName_EvenWithNoLevelInIt()
    {
        DupModel m = M(new DupView(400, "Anything the project calls it", 0, 0));
        DupPlan p = Plan([R(2, "Sitewide Plan, Part A")],
            Choose((2, new RowChoice(RowChoiceKind.DependentOf, "anything the PROJECT calls it"))), m);
        DupStep s = Row(p, 2);
        Assert.Equal(DupAction.CreateDependent, s.Action);
        Assert.Equal(400, s.ParentId);
        Assert.Empty(p.MissingBaseViews);
    }

    [Theory]
    [InlineData("No Such View", "is not in the model")]
    [InlineData("Dep", "is a dependent view")]
    public void AChosenParentThatCannotBeOne_IsAnErrorSayingWhy(string parent, string why)
    {
        DupModel m = M(new DupView(400, "Top", 0, 0), new DupView(401, "Dep", 400, 0));
        DupStep s = Row(Plan([R(2, $"{Base}, Level 01, Sheet 1 of 2")],
            Choose((2, new RowChoice(RowChoiceKind.DependentOf, parent))), m), 2);
        Assert.Equal(DupAction.Error, s.Action);
        Assert.Contains(why, s.Notes[0]);
    }

    [Fact]
    public void ASkippedRow_DoesNothing_AndNeedsNothing()
    {
        DupPlan p = Plan([R(2, $"{Base}, Level 01, Overall"), R(3, "no level at all")],
            Choose((2, new RowChoice(RowChoiceKind.Skip)), (3, new RowChoice(RowChoiceKind.Skip))), bases: []);
        Assert.All(p.Steps, s => Assert.Equal(DupAction.Skipped, s.Action));
        Assert.Equal(2, p.Counts.Skipped);
        Assert.Empty(p.MissingBaseViews);
        Assert.Equal(0, p.Counts.Actionable);
    }

    [Fact]
    public void ParentChoices_AreExistingIndependentViewsAndTheRunsNewOnes()
    {
        DupModel m = M(new DupView(400, "Top", 0, 0), new DupView(401, "Dep", 400, 0));
        DupPlan p = Plan([R(2, $"{Base}, Level 01, Overall"), R(3, $"{Base}, Level 01, Sheet 1 of 2")], m);
        Assert.Equal([$"{Base}, Level 01, Overall", "Top"], p.ParentChoices);
    }

    // ------------------------------------------------------------ problems

    [Fact]
    public void NoBaseView_MakesTheLevelsRowsErrors_AndNamesTheLevel()
    {
        DupPlan p = Plan(
        [
            R(2, $"{Base}, Level 01, Overall"),
            R(3, $"{Base}, Level 01, Sheet 1 of 2"),
            R(4, $"{Base}, Level 02, Overall"),
        ], bases: new() { ["2"] = 101 });

        Assert.Equal(["1"], p.MissingBaseViews);
        Assert.Equal(DupAction.Error, Row(p, 2).Action);
        Assert.Equal(DupAction.Error, Row(p, 3).Action);
        Assert.Contains("row 2", Row(p, 3).Notes[0]);
        Assert.Equal(DupAction.CreateParent, Row(p, 4).Action);
        Assert.DoesNotContain(p.Steps, s => s.IsAutomatic);
    }

    [Fact]
    public void NoBaseViewAndNoOverallRow_GivesOneErrorForTheOverall()
    {
        DupPlan p = Plan(
            [R(2, $"{Base}, Level 01, Sheet 1 of 2"), R(3, $"{Base}, Level 01, Sheet 2 of 2")], bases: []);
        Assert.Single(p.Steps, s => s.IsAutomatic && s.Action == DupAction.Error);
        Assert.Equal(DupAction.Error, Row(p, 2).Action);
        Assert.Equal(DupAction.Error, Row(p, 3).Action);
    }

    [Fact]
    public void AMissingTemplate_IsListedOnce_AndTheViewIsStillMade()
    {
        DupPlan p = Plan(
        [
            R(2, $"{Base}, Level 01, Overall", "No Such Template"),
            R(3, $"{Base}, Level 02, Overall", "no such template"),
        ]);
        Assert.Equal(["No Such Template"], p.MissingTemplates);
        Assert.Equal(DupAction.CreateParent, Row(p, 2).Action);
        Assert.Equal(TemplatePlan.NotFound, Row(p, 2).Template);
        Assert.Equal(0, p.Counts.TemplatesToApply);
    }

    // The real workbook asks for RCP templates on floor plans. Revit accepts one
    // (proven in the in-Revit suite), so it is applied as asked - and flagged.
    [Fact]
    public void ACeilingPlanTemplate_IsAppliedAsAsked_AndFlaggedWithItsKind()
    {
        DupPlan p = Plan([R(2, $"{Base}, Level 01, Overall", Rcp.Name)]);
        Assert.Equal(TemplatePlan.Apply, Row(p, 2).Template);
        Assert.Equal(902, Row(p, 2).TemplateId);
        Assert.Equal("Ceiling Plan", Row(p, 2).TemplateKind);
        Assert.Equal((Rcp.Name, "Ceiling Plan"), Assert.Single(p.OtherKindTemplates));
    }

    [Fact]
    public void TemplateNames_MatchWhateverTheirCase()
        => Assert.Equal(900, Row(Plan([R(2, $"{Base}, Level 01, Overall", FpA.Name.ToUpperInvariant())]), 2).TemplateId);

    [Fact]
    public void ARepeatedRow_IsIgnored_AndPointsAtTheFirst()
    {
        DupPlan p = Plan([R(2, $"{Base}, Level 01, Sheet 1 of 2"), R(3, $"{Base}, level 01, sheet 1 of 2")]);
        DupStep s = Row(p, 3);
        Assert.Equal(DupAction.Repeat, s.Action);
        Assert.Contains("Row 2", s.Notes[0]);
        Assert.Equal(1, p.Counts.CreateDependents);
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("Sh - 80 - Plant Replacement Layout - Sitewide Overall", "No level")]
    public void RowsWithNoNameOrNoLevel_AreErrorsThatSayWhy(string fpv, string why)
    {
        DupStep s = Row(Plan([R(2, fpv)]), 2);
        Assert.Equal(DupAction.Error, s.Action);
        Assert.Contains(why, s.Notes[0]);
    }

    [Fact]
    public void AReservedName_GetsAFreeOne_AndSaysWhoHasIt()
    {
        DupModel m = new([], [FpA], new Dictionary<string, string> { [$"{Base}, Level 01, Overall"] = "a view template" });
        DupStep s = Row(Plan([R(2, $"{Base}, Level 01, Overall")], m), 2);
        Assert.Equal($"{Base}, Level 01, Overall (1)", s.Name);
        Assert.Equal($"{Base}, Level 01, Overall", s.RenamedFrom);
        Assert.Contains("a view template", string.Join(" ", s.Notes));
    }

    // ------------------------------------------------------------- levels

    [Fact]
    public void Levels_AreListedOnce_WithWhetherTheyNeedABaseView()
    {
        DupModel m = M(new DupView(500, "Drainage Layout, Level RF, Overall", 0, 0));
        DupPlan p = Plan(
        [
            R(2, "Drainage Layout, Level 00, Overall"),
            R(3, "Drainage Layout, Level 00, Sheet 1 of 4"),
            R(4, "Drainage Layout, Level RF, Overall"),
        ], m, bases: new() { ["0"] = 99 });

        Assert.Equal(["0", "level rf"], p.Levels.Select(l => l.Key));
        Assert.True(p.Levels[0].NeedsBaseView);
        Assert.Equal(99, p.Levels[0].BaseViewId);
        Assert.Equal(2, p.Levels[0].Rows);
        Assert.False(p.Levels[1].NeedsBaseView);
        Assert.Empty(p.MissingBaseViews);
    }

    [Fact]
    public void InSheetOrder_PutsAnAutomaticOverallJustAboveTheFirstSheetThatNeededIt()
    {
        DupPlan p = Plan(
        [
            R(2, $"{Base}, Level 01, Overall"),
            R(3, $"{Base}, Level 02, Sheet 1 of 2"),
            R(4, $"{Base}, Level 02, Sheet 2 of 2"),
        ]);
        List<DupStep> order = [.. p.InSheetOrder()];
        Assert.Equal(2, order[0].ExcelRow);
        Assert.True(order[1].IsAutomatic);
        Assert.Equal(3, order[2].ExcelRow);
        Assert.Equal(4, order[3].ExcelRow);
    }
}
