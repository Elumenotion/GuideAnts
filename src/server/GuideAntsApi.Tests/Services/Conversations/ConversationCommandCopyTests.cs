using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services.Conversations.Commands;
using GuideAntsApi.Tests.TestUtils;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

namespace GuideAntsApi.Tests.Services.Conversations;

[TestClass]
public sealed class ConversationCommandCopyTests
{
    private ApplicationDbContext _db = null!;
    private ConversationCommandService _commands = null!;
    private Guid _notebookId;
    private Guid _sourceConversationId;

    [TestInitialize]
    public void TestInitialize()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        _db = new ApplicationDbContext(options);
        _commands = new ConversationCommandService(
            new TestServiceScopeFactory(_db),
            Mock.Of<ILogger<ConversationCommandService>>());

        _notebookId = Guid.NewGuid();
        var notebook = new Notebook { Id = _notebookId, ProjectId = Guid.NewGuid(), Title = "NB" };
        _db.Notebooks.Add(notebook);

        _sourceConversationId = Guid.NewGuid();
        var source = new NotebookConversation
        {
            Id = _sourceConversationId,
            NotebookId = _notebookId,
            Title = "Source",
            Notebook = notebook
        };

        var userMsg = new NotebookConversationMessage
        {
            Id = Guid.NewGuid(),
            NotebookConversationId = _sourceConversationId,
            NotebookConversation = source,
            Role = ChatRole.User,
            Content = "Do the thing",
            UserId = Guid.NewGuid(),
            AssistantName = "Claude",
            Created = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc),
            TurnIndex = 1,
            MessageSequence = 1
        };
        userMsg.Attachments.Add(new MessageAttachment
        {
            Id = Guid.NewGuid(),
            MessageId = userMsg.Id,
            Message = userMsg,
            RelativePath = "Output/doc.md",
            Type = AttachmentType.Referenced,
            OrderIndex = 0,
            Created = userMsg.Created
        });

        var assistantMsg = new NotebookConversationMessage
        {
            Id = Guid.NewGuid(),
            NotebookConversationId = _sourceConversationId,
            NotebookConversation = source,
            Role = ChatRole.Assistant,
            Content = "Done.",
            AssistantName = "Claude",
            ModelDeploymentId = "gpt-4o-mini",
            ToolCalls = "[{\"id\":\"call_1\",\"type\":\"function\",\"function\":{\"name\":\"noop\",\"arguments\":\"{}\"}}]",
            ThinkingBlocksJson = "{\"blocks\":[]}",
            Created = new DateTime(2026, 10, 1, 12, 0, 1, DateTimeKind.Utc),
            TurnIndex = 1,
            MessageSequence = 2,
            IsEdited = true
        };
        assistantMsg.EditHistory = new MessageEditHistory
        {
            MessageId = assistantMsg.Id,
            OriginalContent = "Original text",
            FirstEditedByUserId = userMsg.UserId,
            FirstEditedAt = assistantMsg.Created
        };

        var toolMsg = new NotebookConversationMessage
        {
            Id = Guid.NewGuid(),
            NotebookConversationId = _sourceConversationId,
            NotebookConversation = source,
            Role = ChatRole.Tool,
            Content = "tool result",
            ToolCallId = "call_1",
            FunctionName = "noop",
            Created = new DateTime(2026, 10, 1, 12, 0, 2, DateTimeKind.Utc),
            TurnIndex = 1,
            MessageSequence = 3
        };

        source.Messages.Add(userMsg);
        source.Messages.Add(assistantMsg);
        source.Messages.Add(toolMsg);

        source.Turns.Add(new ConversationTurn
        {
            Id = Guid.NewGuid(),
            NotebookConversationId = _sourceConversationId,
            TurnIndex = 1,
            AssistantName = "Claude",
            ModelDeploymentId = "gpt-4o-mini",
            Instructions = "Do the thing",
            FilesCreated = "[\"Output/doc.md\"]",
            WorkingDirectory = "Output/",
            Created = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc),
            Status = "completed",
            UsageJson = "{\"inputTokens\":100,\"outputTokens\":50}",
            ChatRunOutputJson = "{\"status\":\"completed\"}",
            ExecutionId = Guid.NewGuid()
        });

        _db.NotebookConversations.Add(source);
        _db.SaveChanges();
    }

    [TestCleanup]
    public void TestCleanup() => _db.Dispose();

    [TestMethod]
    public async Task CreateConversation_WithSourceConversation_ClonesMessagesTurnsAndAttachments()
    {
        var dto = await _commands.CreateConversationAsync(_notebookId, "Source (Copy)", _sourceConversationId);

        dto.Id.Should().NotBe(_sourceConversationId);
        dto.Title.Should().Be("Source (Copy)");

        var copy = await _db.NotebookConversations
            .Include(c => c.Messages).ThenInclude(m => m.EditHistory)
            .Include(c => c.Messages).ThenInclude(m => m.Attachments)
            .Include(c => c.Turns)
            .AsNoTracking()
            .FirstAsync(c => c.Id == dto.Id);

        copy.NotebookId.Should().Be(_notebookId);
        copy.Messages.Should().HaveCount(3);
        copy.Turns.Should().HaveCount(1);

        // Messages: same ordering/content/tool state, fresh primary keys.
        var ordered = copy.Messages
            .OrderBy(m => m.TurnIndex).ThenBy(m => m.MessageSequence)
            .Select(m => new { m.Role, m.Content, m.TurnIndex, m.MessageSequence, m.ToolCallId, m.FunctionName, m.ToolCalls, m.ThinkingBlocksJson })
            .ToList();
        var sourceOrdered = await _db.NotebookConversationMessages
            .Where(m => m.NotebookConversationId == _sourceConversationId)
            .OrderBy(m => m.TurnIndex).ThenBy(m => m.MessageSequence)
            .Select(m => new { m.Role, m.Content, m.TurnIndex, m.MessageSequence, m.ToolCallId, m.FunctionName, m.ToolCalls, m.ThinkingBlocksJson })
            .ToListAsync();
        ordered.Should().BeEquivalentTo(sourceOrdered);

        // Fresh primary keys
        var sourceIds = _db.NotebookConversationMessages.AsNoTracking()
            .Where(m => m.NotebookConversationId == _sourceConversationId)
            .Select(m => m.Id).ToList();
        copy.Messages.Should().OnlyContain(m => !sourceIds.Contains(m.Id));

        // Attachments follow their message, pointing at the same notebook path.
        var userClone = copy.Messages.First(m => m.Role == ChatRole.User);
        userClone.Attachments.Should().ContainSingle(a => a.RelativePath == "Output/doc.md");
        userClone.Attachments.First().MessageId.Should().Be(userClone.Id);

        // Edit history is cloned with the new message id.
        var assistantClone = copy.Messages.First(m => m.Role == ChatRole.Assistant);
        assistantClone.IsEdited.Should().BeTrue();
        assistantClone.EditHistory.Should().NotBeNull();
        assistantClone.EditHistory!.OriginalContent.Should().Be("Original text");
        assistantClone.EditHistory.MessageId.Should().Be(assistantClone.Id);

        // Turn rows: structure copied, usage/run output NOT copied (no double-counted usage).
        var turn = copy.Turns.Single();
        turn.Id.Should().NotBe((await _db.ConversationTurns.AsNoTracking().Where(t => t.NotebookConversationId == _sourceConversationId).SingleAsync()).Id);
        turn.TurnIndex.Should().Be(1);
        turn.AssistantName.Should().Be("Claude");
        turn.Instructions.Should().Be("Do the thing");
        turn.Status.Should().Be("completed");
        turn.FilesCreated.Should().Be("[\"Output/doc.md\"]");
        turn.UsageJson.Should().BeNull();
        turn.ChatRunOutputJson.Should().BeNull();
        turn.ExecutionId.Should().BeNull();

        // Copy starts uncompacted even if the source was compacted.
        copy.CompactionBoundaryTurnIndex.Should().BeNull();
    }

    [TestMethod]
    public async Task CreateConversation_WithSourceOutsideNotebook_Throws()
    {
        var otherNotebook = new Notebook { Id = Guid.NewGuid(), ProjectId = Guid.NewGuid(), Title = "Other" };
        _db.Notebooks.Add(otherNotebook);
        var foreign = new NotebookConversation { Id = Guid.NewGuid(), NotebookId = otherNotebook.Id, Title = "Foreign" };
        _db.NotebookConversations.Add(foreign);
        _db.SaveChanges();

        var act = () => _commands.CreateConversationAsync(_notebookId, "Nope", foreign.Id);
        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [TestMethod]
    public async Task CreateConversation_WithMultiTurnSource_ClonesEveryRowWithoutJoinFanOut()
    {
        // Guards the split projected reads in CreateConversationAsync: the old
        // Messages + Turns Include loaded a cartesian product (messages x turns),
        // which is what made a copy take ~70-120 s. With more than one turn, any
        // regression back to a joined load shows up as duplicated rows here.
        var multiTurnId = Guid.NewGuid();
        var multiTurn = new NotebookConversation
        {
            Id = multiTurnId,
            NotebookId = _notebookId,
            Title = "Multi-turn source"
        };
        _db.NotebookConversations.Add(multiTurn);

        for (var turn = 1; turn <= 3; turn++)
        {
            for (var seq = 1; seq <= 2; seq++)
            {
                var msg = new NotebookConversationMessage
                {
                    Id = Guid.NewGuid(),
                    NotebookConversationId = multiTurnId,
                    Role = seq == 1 ? ChatRole.User : ChatRole.Assistant,
                    AssistantName = "Claude",
                    Content = turn == 2 && seq == 1 ? "attachment carrier" : $"turn {turn} seq {seq}",
                    Created = new DateTime(2026, 10, 1, 12, turn, seq, DateTimeKind.Utc),
                    TurnIndex = turn,
                    MessageSequence = seq
                };
                if (turn == 2 && seq == 1)
                {
                    msg.Attachments.Add(new MessageAttachment
                    {
                        Id = Guid.NewGuid(),
                        MessageId = msg.Id,
                        RelativePath = $"Output/turn{turn}.md",
                        Type = AttachmentType.Created,
                        OrderIndex = 0,
                        Created = msg.Created
                    });
                }
                _db.NotebookConversationMessages.Add(msg);
            }

            _db.ConversationTurns.Add(new ConversationTurn
            {
                Id = Guid.NewGuid(),
                NotebookConversationId = multiTurnId,
                TurnIndex = turn,
                AssistantName = "Claude",
                Instructions = $"instruction {turn}",
                Created = new DateTime(2026, 10, 1, 12, turn, 0, DateTimeKind.Utc),
                Status = "completed",
                UsageJson = "{\"inputTokens\":1}",
                ChatRunOutputJson = "{\"status\":\"completed\"}"
            });
        }
        _db.SaveChanges();

        var dto = await _commands.CreateConversationAsync(_notebookId, "Multi-turn (Copy)", multiTurnId);

        var copy = await _db.NotebookConversations
            .Include(c => c.Messages).ThenInclude(m => m.Attachments)
            .Include(c => c.Turns)
            .AsNoTracking()
            .FirstAsync(c => c.Id == dto.Id);

        // Exact counts: a joined load would inflate these by the turn count.
        copy.Messages.Should().HaveCount(6);
        copy.Turns.Should().HaveCount(3);
        copy.Messages.Select(m => m.Content).Should().OnlyHaveUniqueItems();
        copy.Turns.Select(t => t.TurnIndex).Should().BeEquivalentTo(new[] { 1, 2, 3 });

        copy.Messages
            .Select(m => new { m.TurnIndex, m.MessageSequence, m.Content, m.Role })
            .OrderBy(x => x.TurnIndex).ThenBy(x => x.MessageSequence)
            .Should().Equal(
                new { TurnIndex = 1, MessageSequence = 1, Content = "turn 1 seq 1", Role = ChatRole.User },
                new { TurnIndex = 1, MessageSequence = 2, Content = "turn 1 seq 2", Role = ChatRole.Assistant },
                new { TurnIndex = 2, MessageSequence = 1, Content = "attachment carrier", Role = ChatRole.User },
                new { TurnIndex = 2, MessageSequence = 2, Content = "turn 2 seq 2", Role = ChatRole.Assistant },
                new { TurnIndex = 3, MessageSequence = 1, Content = "turn 3 seq 1", Role = ChatRole.User },
                new { TurnIndex = 3, MessageSequence = 2, Content = "turn 3 seq 2", Role = ChatRole.Assistant });

        var attached = copy.Messages.Single(m => m.Content == "attachment carrier");
        attached.Attachments.Should().ContainSingle(a => a.RelativePath == "Output/turn2.md");

        copy.Turns.Should().OnlyContain(t => t.UsageJson == null && t.ChatRunOutputJson == null);
    }

    [TestMethod]
    public async Task CreateConversation_WithSourceWhoseTimestampsBreakSequenceOrder_CopiesAnyway()
    {
        // Historical transcripts were written before the SaveChanges timestamp
        // validator existed, so some turns have a higher MessageSequence with an
        // EARLIER Created value. Copy inserts a whole transcript at once, and the
        // validator used to reject it ("existing messages with higher sequence
        // numbers"), which made such conversations uncopyable (HTTP 500 in the app).
        var dirtyId = Guid.NewGuid();
        var dirty = new NotebookConversation
        {
            Id = dirtyId,
            NotebookId = _notebookId,
            Title = "Dirty ordering"
        };
        _db.NotebookConversations.Add(dirty);

        // Same turn: sequence 56 carries a LATER timestamp than sequence 57's
        // predecessor - exactly the shape that triggered the 500.
        _db.NotebookConversationMessages.Add(new NotebookConversationMessage
        {
            Id = Guid.NewGuid(),
            NotebookConversationId = dirtyId,
            Role = ChatRole.Assistant,
            Content = "later timestamp, lower sequence",
            TurnIndex = 1,
            MessageSequence = 56,
            Created = new DateTime(2026, 10, 20, 14, 22, 23, DateTimeKind.Utc)
        });
        _db.NotebookConversationMessages.Add(new NotebookConversationMessage
        {
            Id = Guid.NewGuid(),
            NotebookConversationId = dirtyId,
            Role = ChatRole.Tool,
            Content = "earlier timestamp, higher sequence",
            TurnIndex = 1,
            MessageSequence = 57,
            Created = new DateTime(2026, 10, 20, 14, 22, 22, DateTimeKind.Utc)
        });
        _db.SaveChanges();

        var dto = await _commands.CreateConversationAsync(_notebookId, "Dirty ordering (Copy)", dirtyId);

        var copy = await _db.NotebookConversationMessages
            .AsNoTracking()
            .Where(m => m.NotebookConversationId == dto.Id)
            .OrderBy(m => m.MessageSequence)
            .ToListAsync();
        copy.Should().HaveCount(2);
        copy[0].MessageSequence.Should().Be(56);
        copy[1].MessageSequence.Should().Be(57);
    }

    [TestMethod]
    public async Task AppendIntoExistingConversation_WithOutOfOrderTimestamp_StillThrows()
    {
        // Regression guard for the validator exemption: it must only cover bulk
        // imports into a conversation created by the same SaveChanges. Appending a
        // message that lands ahead of a higher-sequence message already stored in an
        // existing conversation must still be rejected.
        var existingId = Guid.NewGuid();
        _db.NotebookConversations.Add(new NotebookConversation
        {
            Id = existingId,
            NotebookId = _notebookId,
            Title = "Existing"
        });
        _db.NotebookConversationMessages.Add(new NotebookConversationMessage
        {
            Id = Guid.NewGuid(),
            NotebookConversationId = existingId,
            Role = ChatRole.Assistant,
            Content = "stored at sequence 5",
            TurnIndex = 1,
            MessageSequence = 5,
            Created = new DateTime(2026, 10, 1, 12, 0, 5, DateTimeKind.Utc)
        });
        _db.SaveChanges();

        _db.NotebookConversationMessages.Add(new NotebookConversationMessage
        {
            Id = Guid.NewGuid(),
            NotebookConversationId = existingId,
            Role = ChatRole.Assistant,
            Content = "appended at sequence 3 with a newer timestamp",
            TurnIndex = 1,
            MessageSequence = 3,
            Created = new DateTime(2026, 10, 1, 12, 0, 30, DateTimeKind.Utc)
        });

        var act = () => _db.SaveChangesAsync();
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*higher sequence numbers*");
    }

    [TestMethod]
    public async Task CreateConversation_WithoutSource_CreatesEmptyConversation()
    {
        var dto = await _commands.CreateConversationAsync(_notebookId, "Plain");

        var copy = await _db.NotebookConversations
            .Include(c => c.Messages)
            .AsNoTracking()
            .FirstAsync(c => c.Id == dto.Id);
        copy.Messages.Should().BeEmpty();
        copy.Title.Should().Be("Plain");
    }
}
