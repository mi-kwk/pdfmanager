using PdfManager.Core.Catalog;
using PdfManager.Core.Search;

namespace PdfManager.Core.Tests;

// 決定ログ BC-2: タグの付け漏れを探す(利用者の例:メーカー・業種が順位 1、商品ジャンルが順位 2)
public sealed class TagGapTests
{
    private const long Maker = 1, Industry = 2, Genre = 3, Free = 4;
    private const long Iodata = 10, Sony = 11, Pc = 20, Game = 21, UsbMemory = 30, Display = 31, Controller = 32, Important = 40;

    private static readonly TagGroup[] Groups =
    [
        new(Maker, "メーカー", 1, true, 0, 0),
        new(Industry, "業種", 1, true, 1, 0),
        new(Genre, "商品ジャンル", 2, false, 0, 0),
    ];

    private static readonly TagSummary[] Tags =
    [
        new(Iodata, "IODATA", 0, Maker, "メーカー", [], []),
        new(Sony, "SONY", 0, Maker, "メーカー", [], []),
        new(Pc, "PC", 0, Industry, "業種", [], []),
        new(Game, "ゲーム", 0, Industry, "業種", [], []),
        new(UsbMemory, "USBメモリ", 0, Genre, "商品ジャンル", [Iodata, Pc], ["IODATA", "PC"]),
        new(Display, "ディスプレイ", 0, Genre, "商品ジャンル", [Iodata], ["IODATA"]),
        new(Controller, "コントローラー", 0, Genre, "商品ジャンル", [Sony, Game], ["SONY", "ゲーム"]),
        new(Important, "重要", 0, null, null, [], []),
    ];

    private static readonly TagGapFinder Finder = new(Groups, Tags);

    private static DocumentEntry Doc(string id, params long[] tagIds) =>
        new(id, id, id + ".pdf", DateTimeOffset.UnixEpoch, 1, tagIds, [], []);

    private static readonly DocumentEntry[] Docs =
    [
        Doc("全部あり", Iodata, Pc, UsbMemory),
        Doc("業種なし", Iodata, UsbMemory),
        Doc("順位1なし", UsbMemory, Important),
        Doc("タグなし"),
        Doc("ジャンルなし", Iodata, Pc),
        Doc("紐づかないジャンル", Iodata, Pc, Controller),
        Doc("SONYだけ", Sony),
        Doc("旧版", Iodata) with { IsLatest = false },
    ];

    private static List<string> Ids(TagGapQuery query) => Finder.Find(Docs, query).Select(g => g.Entry.Id).ToList();

    [Fact]
    public void MissingGroup_AllRankOneGroups()
    {
        Assert.Equal(["順位1なし", "タグなし"], Ids(new TagGapQuery(TagGapKind.MissingGroup)));
    }

    [Fact]
    public void MissingGroup_OneGroup_FindsPdfsWithoutIndustry()
    {
        Assert.Equal(["業種なし", "順位1なし", "タグなし", "SONYだけ"], Ids(new TagGapQuery(TagGapKind.MissingGroup, GroupId: Industry)));
        // 絞り込み:IODATA が付いているものだけ
        Assert.Equal(["業種なし"], Ids(new TagGapQuery(TagGapKind.MissingGroup, GroupId: Industry, FilterTagId: Iodata)));
        Assert.Equal("業種のタグなし", Finder.Find(Docs, new TagGapQuery(TagGapKind.MissingGroup, GroupId: Industry))[0].Description);
    }

    [Fact]
    public void MissingChild_FindsUpperTagsWithoutLinkedLowerTag()
    {
        // ジャンルなし:IODATA・PC に紐づくジャンルがない。紐づかないジャンル:コントローラーは IODATA・PC に紐づかない。
        // SONYだけ:SONY に紐づくジャンル(コントローラー)がない。旧版は調べない
        Assert.Equal(["ジャンルなし", "紐づかないジャンル", "SONYだけ"], Ids(new TagGapQuery(TagGapKind.MissingChild)));
        Assert.Equal(["ジャンルなし", "紐づかないジャンル"], Ids(new TagGapQuery(TagGapKind.MissingChild, FilterTagId: Iodata)));
        Assert.Equal("IODATA、PC に紐づく順位 2 のタグなし", Finder.Find(Docs, new TagGapQuery(TagGapKind.MissingChild))[0].Description);
    }

    [Fact]
    public void MissingParent_FindsLowerTagsWithoutTheirUpperTag()
    {
        // 順位1なし:USBメモリの紐づき先(IODATA・PC)がない。紐づかないジャンル:コントローラーの紐づき先(SONY・ゲーム)がない
        Assert.Equal(["順位1なし", "紐づかないジャンル"], Ids(new TagGapQuery(TagGapKind.MissingParent)));
        Assert.Equal(["紐づかないジャンル"], Ids(new TagGapQuery(TagGapKind.MissingParent, FilterTagId: Sony)));
        Assert.Equal("USBメモリ(IODATA・PC) の紐づき先なし", Finder.Find(Docs, new TagGapQuery(TagGapKind.MissingParent))[0].Description);
    }

    [Fact]
    public void Count_MatchesFind()
    {
        var query = new TagGapQuery(TagGapKind.MissingChild);
        Assert.Equal(Finder.Find(Docs, query).Count, Finder.Count(Docs, query));
    }
}
