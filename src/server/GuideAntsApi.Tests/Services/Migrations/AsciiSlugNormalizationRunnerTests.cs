using System.Collections;
using System.Reflection;
using GuideAntsApi.Services;
using GuideAntsApi.Services.Migrations;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace GuideAntsApi.Tests.Services.Migrations;

/// <summary>
/// <see cref="AsciiSlugNormalizationRunner"/> only exposes <c>RunAsync</c>, which
/// requires a live SQL Server for everything beyond the storage-root guard. The
/// deterministic core of the runner is the private static <c>BuildPlan</c>
/// method, which is exercised here via reflection so the slug de-duplication and
/// normalization rules are covered with real assertions.
/// </summary>
[TestClass]
public sealed class AsciiSlugNormalizationRunnerTests
{
    private static readonly Type RunnerType = typeof(AsciiSlugNormalizationRunner);
    private static readonly Type ProjectRowType = RunnerType.GetNestedType("ProjectRow", BindingFlags.NonPublic)!;
    private static readonly Type NotebookRowType = RunnerType.GetNestedType("NotebookRow", BindingFlags.NonPublic)!;

    [TestMethod]
    public async Task RunAsync_MissingStorageRoot_ThrowsDirectoryNotFound()
    {
        var missingRoot = Path.Combine(Path.GetTempPath(), "ascii-missing-" + Guid.NewGuid().ToString("N"));
        var runner = new AsciiSlugNormalizationRunner("Server=unused;", missingRoot, NullLogger.Instance);

        Func<Task> act = () => runner.RunAsync(apply: false);

        await act.Should().ThrowAsync<DirectoryNotFoundException>();
    }

    [TestMethod]
    public void BuildPlan_KeepsSafeUniqueSlugs_NormalizesUnsafeOnes()
    {
        var safeId = Guid.NewGuid();
        var unsafeId = Guid.NewGuid();
        var projects = BuildProjectList(
            (safeId, "Safe Project", "alpha"),
            (unsafeId, "Needs Fixing", "Bad Slug!"));

        var plan = InvokeBuildPlan(projects, BuildNotebookList());

        var projectChanges = ReadProjectChanges(plan);
        projectChanges.Should().ContainSingle();
        projectChanges[0].ProjectId.Should().Be(unsafeId);
        projectChanges[0].OldSlug.Should().Be("Bad Slug!");
        projectChanges[0].NewSlug.Should().Be(SlugGenerator.Generate("Needs Fixing"));

        var targets = ReadTargetProjectSlugs(plan);
        targets[safeId].Should().Be("alpha");
        targets[unsafeId].Should().Be(SlugGenerator.Generate("Needs Fixing"));
    }

    [TestMethod]
    public void BuildPlan_DuplicateSafeSlugs_NormalizesLaterOccurrence()
    {
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var projects = BuildProjectList(
            (firstId, "First", "dup"),
            (secondId, "Second", "dup"));

        var plan = InvokeBuildPlan(projects, BuildNotebookList());

        var projectChanges = ReadProjectChanges(plan);
        projectChanges.Should().ContainSingle();
        projectChanges[0].ProjectId.Should().Be(secondId);
        projectChanges[0].NewSlug.Should().Be(SlugGenerator.Generate("Second"));

        var targets = ReadTargetProjectSlugs(plan);
        targets[firstId].Should().Be("dup");
    }

    [TestMethod]
    public void BuildPlan_NormalizationCollisions_AppendNumericSuffix()
    {
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        // Both have unsafe slugs and identical titles, forcing the same base slug.
        var projects = BuildProjectList(
            (firstId, "Same Title", "BAD ONE"),
            (secondId, "Same Title", "BAD TWO"));

        var plan = InvokeBuildPlan(projects, BuildNotebookList());

        var baseSlug = SlugGenerator.Generate("Same Title");
        var projectChanges = ReadProjectChanges(plan);
        projectChanges.Should().HaveCount(2);
        projectChanges.Select(c => c.NewSlug)
            .Should().BeEquivalentTo(new[] { baseSlug, SlugGenerator.AddNumericSuffix(baseSlug, 2) });
    }

    [TestMethod]
    public void BuildPlan_NormalizesNotebookSlugsPerProject()
    {
        var projectId = Guid.NewGuid();
        var notebookId = Guid.NewGuid();
        var projects = BuildProjectList((projectId, "Project", "alpha"));
        var notebooks = BuildNotebookList((notebookId, projectId, "My Notebook", "Bad Notebook Slug!"));

        var plan = InvokeBuildPlan(projects, notebooks);

        ReadProjectChanges(plan).Should().BeEmpty();

        var notebookChanges = ReadNotebookChanges(plan);
        notebookChanges.Should().ContainSingle();
        notebookChanges[0].NotebookId.Should().Be(notebookId);
        notebookChanges[0].ProjectId.Should().Be(projectId);
        notebookChanges[0].NewSlug.Should().Be(SlugGenerator.Generate("My Notebook"));
    }

    private static IList BuildProjectList(params (Guid Id, string Title, string Slug)[] rows)
    {
        var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(ProjectRowType))!;
        foreach (var row in rows)
        {
            list.Add(CreateRecord(ProjectRowType, row.Id, row.Title, row.Slug));
        }
        return list;
    }

    private static IList BuildNotebookList(params (Guid Id, Guid ProjectId, string Title, string Slug)[] rows)
    {
        var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(NotebookRowType))!;
        foreach (var row in rows)
        {
            list.Add(CreateRecord(NotebookRowType, row.Id, row.ProjectId, row.Title, row.Slug));
        }
        return list;
    }

    private static object InvokeBuildPlan(IList projects, IList notebooks)
    {
        var method = RunnerType.GetMethod("BuildPlan", BindingFlags.NonPublic | BindingFlags.Static)!;
        return method.Invoke(null, new object[] { projects, notebooks })!;
    }

    private static List<(Guid ProjectId, string OldSlug, string NewSlug)> ReadProjectChanges(object plan)
    {
        var result = new List<(Guid, string, string)>();
        var changes = (IEnumerable)GetProp(plan, "ProjectChanges")!;
        foreach (var change in changes)
        {
            result.Add((
                (Guid)GetProp(change, "ProjectId")!,
                (string)GetProp(change, "OldSlug")!,
                (string)GetProp(change, "NewSlug")!));
        }
        return result;
    }

    private static List<(Guid NotebookId, Guid ProjectId, string OldSlug, string NewSlug)> ReadNotebookChanges(object plan)
    {
        var result = new List<(Guid, Guid, string, string)>();
        var changes = (IEnumerable)GetProp(plan, "NotebookChanges")!;
        foreach (var change in changes)
        {
            result.Add((
                (Guid)GetProp(change, "NotebookId")!,
                (Guid)GetProp(change, "ProjectId")!,
                (string)GetProp(change, "OldSlug")!,
                (string)GetProp(change, "NewSlug")!));
        }
        return result;
    }

    private static Dictionary<Guid, string> ReadTargetProjectSlugs(object plan) =>
        (Dictionary<Guid, string>)GetProp(plan, "TargetProjectSlugs")!;

    private static object CreateRecord(Type type, params object[] args)
    {
        var ctor = type
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .First(c => c.GetParameters().Length == args.Length);
        return ctor.Invoke(args);
    }

    private static object? GetProp(object instance, string name) =>
        instance.GetType().GetProperty(name)!.GetValue(instance);
}
