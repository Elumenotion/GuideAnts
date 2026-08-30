using AntRunner.Chat.Abstractions;
using FluentAssertions;
using GuideAntsApi.Options;
using GuideAntsApi.Services.Conversations.Attachments;
using Microsoft.Extensions.Options;

namespace GuideAntsApi.Tests.Services.Conversations;

[TestClass]
public sealed class AttachmentRenderCacheTests
{
    private static AttachmentRenderCache Create(long sizeLimitBytes = 1024 * 1024) =>
        new(Microsoft.Extensions.Options.Options.Create(new AttachmentRenderCacheOptions
        {
            SizeLimitBytes = sizeLimitBytes,
            SlidingExpirationMinutes = 30
        }));

    [TestMethod]
    public void TryGet_Miss_ReturnsFalse()
    {
        using var cache = Create();
        cache.TryGet(Guid.NewGuid(), 1, out _).Should().BeFalse();
    }

    [TestMethod]
    public void SetThenGet_SameKey_ReturnsContent()
    {
        using var cache = Create();
        var fileId = Guid.NewGuid();
        cache.Set(fileId, 100, [new ChatContent("hello")]);

        cache.TryGet(fileId, 100, out var contents).Should().BeTrue();
        contents.Should().ContainSingle(c => c.Text == "hello");
    }

    [TestMethod]
    public void Get_AfterLastModifiedChanges_Misses()
    {
        using var cache = Create();
        var fileId = Guid.NewGuid();
        cache.Set(fileId, 100, [new ChatContent("v1")]);

        cache.TryGet(fileId, 200, out _).Should().BeFalse();
    }

    [TestMethod]
    public void Set_EmptyContent_IsNotCached()
    {
        using var cache = Create();
        var fileId = Guid.NewGuid();
        cache.Set(fileId, 100, []);

        cache.TryGet(fileId, 100, out _).Should().BeFalse();
    }

    [TestMethod]
    public void ReturnedList_IsACopy_MutationDoesNotPoisonCache()
    {
        using var cache = Create();
        var fileId = Guid.NewGuid();
        cache.Set(fileId, 100, [new ChatContent("hello")]);

        cache.TryGet(fileId, 100, out var first).Should().BeTrue();
        first.Add(new ChatContent("junk"));

        cache.TryGet(fileId, 100, out var second).Should().BeTrue();
        second.Should().HaveCount(1);
    }

    [TestMethod]
    public void SizeLimit_EvictsWhenExceeded()
    {
        using var cache = Create(sizeLimitBytes: 10);
        var bigId = Guid.NewGuid();
        cache.Set(bigId, 1, [new ChatContent(new string('x', 100))]);

        // Entry larger than the cache's SizeLimit is rejected outright by MemoryCache.
        cache.TryGet(bigId, 1, out _).Should().BeFalse();
    }
}
