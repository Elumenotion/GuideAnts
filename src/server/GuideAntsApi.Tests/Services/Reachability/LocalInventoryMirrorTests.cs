using FluentAssertions;
using GuideAntsApi.Models;
using GuideAntsApi.Services.Reachability;

namespace GuideAntsApi.Tests.Services.Reachability;

/// <summary>T-U3 — the inventory mirror is last-known truth: live reads set
/// Live; a failed probe keeps the prior snapshot (and its ObservedUtc); a
/// malformed/failed read never clobbers a good snapshot (P6).</summary>
[TestClass]
public sealed class LocalInventoryMirrorTests
{
    private static readonly DateTime T0 = new(2026, 11, 11, 14, 55, 0, DateTimeKind.Utc);
    private static readonly DateTime T1 = new(2026, 11, 11, 15, 0, 0, DateTimeKind.Utc);

    private static NotebookToolbarLocalModelOptionDto Model(string ref_, bool complete = true, bool active = false) =>
        new(ref_, ref_, complete, active);

    [TestMethod]
    public void LiveRead_SetsLive_WithObservedUtc()
    {
        var mirror = new LocalInventoryMirror();
        mirror.SetLive("SpeechSynthesis", new[] { Model("model-a"), Model("model-b") }, T0);

        mirror.TryGet("SpeechSynthesis", out var models, out var source, out var observedUtc).Should().BeTrue();
        source.Should().Be(InventorySource.Live);
        observedUtc.Should().Be(T0);
        models.Should().HaveCount(2).And.BeEquivalentTo(new[] { Model("model-a"), Model("model-b") });
    }

    [TestMethod]
    public void FailedProbe_KeepsLastKnownSnapshot_WithItsOriginalObservedUtc()
    {
        var mirror = new LocalInventoryMirror();
        mirror.SetLive("Embeddings", new[] { Model("model-x") }, T0);
        mirror.MarkFailed("Embeddings", T1); // a later probe failed

        mirror.TryGet("Embeddings", out var models, out var source, out var observedUtc).Should().BeTrue();
        source.Should().Be(InventorySource.LastKnown, "a failed read re-stamps, it does not erase");
        observedUtc.Should().Be(T0, "the surface can say 'last known · observed 14:55'");
        models.Should().BeEquivalentTo(new[] { Model("model-x") });
    }

    [TestMethod]
    public void FailedProbe_WithNoPriorSnapshot_LeavesNone_TheSurfaceSaysNotYetObserved()
    {
        var mirror = new LocalInventoryMirror();
        mirror.MarkFailed("ImageGeneration", T1);

        mirror.TryGet("ImageGeneration", out var models, out var source, out var observedUtc).Should().BeFalse();
        source.Should().Be(InventorySource.None);
        models.Should().BeEmpty();
        observedUtc.Should().BeNull();
    }

    [TestMethod]
    public void SubsequentLiveRead_OverwritesSnapshot()
    {
        var mirror = new LocalInventoryMirror();
        mirror.SetLive("SpeechTranscription", new[] { Model("old") }, T0);
        mirror.MarkFailed("SpeechTranscription", T1);
        mirror.SetLive("SpeechTranscription", new[] { Model("new") }, T1);

        mirror.TryGet("SpeechTranscription", out var models, out var source, out var observedUtc).Should().BeTrue();
        source.Should().Be(InventorySource.Live);
        observedUtc.Should().Be(T1);
        models.Should().BeEquivalentTo(new[] { Model("new") });
    }
}
