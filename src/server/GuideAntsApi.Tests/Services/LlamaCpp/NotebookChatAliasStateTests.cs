using GuideAntsApi.Services.LlamaCpp;

namespace GuideAntsApi.Tests.Services.LlamaCpp;

[TestClass]
public sealed class NotebookChatAliasStateTests
{
    [TestMethod]
    public void SetActiveChatAliasForInstance_RecordsPerInstance_IndependentOfOthers()
    {
        var state = new NotebookChatAliasState();
        state.SetActiveChatAliasForInstance("http://192.0.2.1:8112", "max-alias");
        state.SetActiveChatAliasForInstance("http://local", "local-alias");

        Assert.AreEqual("max-alias", state.GetActiveChatAlias("http://192.0.2.1:8112"));
        Assert.AreEqual("local-alias", state.GetActiveChatAlias("http://local"));
        Assert.AreEqual(2, state.GetActiveChatAliases().Count);
        // Legacy accessor: most recent alias.
        Assert.AreEqual("local-alias", state.ActiveChatAlias);
    }

    [TestMethod]
    public void ClearInstance_OnlyClearsThatInstance()
    {
        var state = new NotebookChatAliasState();
        state.SetActiveChatAliasForInstance("http://192.0.2.1:8112", "max-alias");
        state.SetActiveChatAliasForInstance("http://local", "local-alias");

        state.ClearInstance("http://192.0.2.1:8112");

        Assert.IsNull(state.GetActiveChatAlias("http://192.0.2.1:8112"));
        Assert.AreEqual("local-alias", state.GetActiveChatAlias("http://local"));
        Assert.AreEqual(1, state.GetActiveChatAliases().Count);
    }

    [TestMethod]
    public void SetActiveChatAlias_LegacyPath_StaysReadableThroughLegacyAccessor()
    {
        var state = new NotebookChatAliasState();
        state.SetActiveChatAlias("some-alias");

        Assert.AreEqual("some-alias", state.ActiveChatAlias);
    }

    [TestMethod]
    public void ClearActiveChatAlias_ClearsEveryInstance()
    {
        var state = new NotebookChatAliasState();
        state.SetActiveChatAliasForInstance("http://192.0.2.1:8112", "max-alias");
        state.SetActiveChatAliasForInstance("http://local", "local-alias");

        state.ClearActiveChatAlias();

        Assert.AreEqual(0, state.GetActiveChatAliases().Count);
        Assert.IsNull(state.ActiveChatAlias);
    }
}
