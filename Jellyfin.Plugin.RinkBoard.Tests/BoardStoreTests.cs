using System.Text.Json.Nodes;
using Jellyfin.Plugin.RinkBoard.Board;

namespace Jellyfin.Plugin.RinkBoard.Tests;

public class BoardStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rinkboard-test-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public async Task FirstLoadCreatesStarterBoardWithLinkedActions()
    {
        using var store = new BoardStore(_dir);
        var board = await store.LoadAsync();
        var pages = (JsonArray)board["pages"]!;
        var actions = (JsonArray)board["actions"]!;
        Assert.Equal(1, (int)board["version"]!);
        Assert.Equal(9, pages.Count);
        Assert.Equal(7, actions.Count);
        var pageIds = pages.Select(p => (string)p!["id"]!).ToHashSet();
        Assert.All(actions, a => Assert.Contains((string)a!["pageId"]!, pageIds));
    }

    [Fact]
    public async Task SaveBumpsVersionAndRefusesStaleEdits()
    {
        using var store = new BoardStore(_dir);
        var board = await store.LoadAsync();
        board["baseVersion"] = 1;
        var saved = await store.SaveAsync(board);
        Assert.Equal(2, (int)saved["version"]!);

        var stale = JsonNode.Parse(saved.ToJsonString())!.AsObject();
        stale["baseVersion"] = 1;
        await Assert.ThrowsAsync<BoardConflictException>(() => store.SaveAsync(stale));
    }

    [Fact]
    public void SanitizeKeepsKnownFieldsAndFixesBadValues()
    {
        var input = JsonNode.Parse("""
        {
          "settings": { "duckLevel": 4, "clipLengthMs": 1000 },
          "pages": [{ "id": "p1", "name": "Goals", "type": "playlist", "order": "bogus", "extra": 1,
            "cues": [{ "id": "c1", "label": "", "kind": "music", "itemId": "abc", "color": "red",
                       "endMs": "", "volume": 3, "clip": true, "hotkey": "AB" }] }],
          "actions": [{ "label": "Home Goal", "pageId": "p1", "introCueId": "missing" },
                      { "label": "Nowhere", "pageId": "gone" }]
        }
        """)!.AsObject();
        var b = BoardStore.Sanitize(input, 5);
        var page = b["pages"]![0]!;
        var cue = page["cues"]![0]!;
        Assert.Equal(6, (int)b["version"]!);
        Assert.Equal(1.0, (double)b["settings"]!["duckLevel"]!);
        Assert.Equal(5000, (int)b["settings"]!["clipLengthMs"]!);
        Assert.Equal("playlist", (string)page["type"]!);
        Assert.Equal("inorder", (string)page["order"]!);
        Assert.Null(page["extra"]);
        Assert.Equal("Untitled", (string)cue["label"]!);
        Assert.Equal("#1f6feb", (string)cue["color"]!);
        Assert.Null(cue["endMs"]);
        Assert.Equal(1.0, (double)cue["volume"]!);
        Assert.True((bool)cue["clip"]!);
        Assert.Equal("a", (string)cue["hotkey"]!);
        Assert.Equal("p1", (string)b["actions"]![0]!["pageId"]!);
        Assert.Null(b["actions"]![0]!["introCueId"]);
        Assert.Equal(string.Empty, (string)b["actions"]![1]!["pageId"]!);
    }
}
