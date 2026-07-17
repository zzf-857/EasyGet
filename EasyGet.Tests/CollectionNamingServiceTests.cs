using EasyGet.Services;
using Xunit;

namespace EasyGet.Tests;

public class CollectionNamingServiceTests
{
    private const string CollectionTitle =
        "【大模型RAG】2026年吃透B站最全最细的RAG知识库搭建系统教程，手把手教你训练RAG，从入门到实战全流程教学！全程干货！少走99%的弯路！";

    [Fact]
    public void BuildItemTitle_StripsBilibiliCollectionTitleAndPartMarker()
    {
        var resolvedTitle =
            $"{CollectionTitle} p01 00.【RAG知识库搭建学习指南】从理论导实战的完整路径";

        var title = CollectionNamingService.BuildItemTitle(
            resolvedTitle,
            CollectionTitle,
            oneBasedIndex: 1,
            itemCount: 85);

        Assert.Equal("00.【RAG知识库搭建学习指南】从理论导实战的完整路径", title);
    }

    [Fact]
    public void BuildItemTitle_PreservesSecondEntryNumberFromRealBilibiliMetadata()
    {
        var resolvedTitle = $"{CollectionTitle} p02 01.【RAG核心原理】RAG的使用场景";

        var title = CollectionNamingService.BuildItemTitle(
            resolvedTitle,
            CollectionTitle,
            oneBasedIndex: 2,
            itemCount: 85);

        Assert.Equal("01.【RAG核心原理】RAG的使用场景", title);
    }

    [Fact]
    public void BuildItemTitle_AddsStableSequenceWhenPlatformTitleHasNoNumber()
    {
        var title = CollectionNamingService.BuildItemTitle(
            "My Course - Installation",
            "My Course",
            oneBasedIndex: 2,
            itemCount: 12);

        Assert.Equal("02. Installation", title);
    }

    [Fact]
    public void TryExtractCollectionTitle_ReadsLegacyFullBilibiliTitle()
    {
        var resolvedTitle = $"{CollectionTitle} p85 84.【综合实战】RAG温度设置分析和讲解";

        var parsed = CollectionNamingService.TryExtractCollectionTitle(
            resolvedTitle,
            out var collectionTitle);

        Assert.True(parsed);
        Assert.Equal(CollectionTitle, collectionTitle);
    }
}
