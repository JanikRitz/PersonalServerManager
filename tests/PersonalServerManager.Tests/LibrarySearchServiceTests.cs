using System.Collections.Concurrent;
using System.Net;
using Microsoft.Data.Sqlite;
using PersonalServerManager.Hydrus;
using PersonalServerManager.LocalLibraries;
using PersonalServerManager.Search;
using PersonalServerManager.Stash;
using Xunit;

namespace PersonalServerManager.Tests;

public sealed class LibrarySearchServiceTests
{
    [Fact]
    public void SortItems_UsesTitleOrPathAndCreationBeforeUpdateWithMissingDatesLast()
    {
        var old = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var recent = old.AddYears(2);
        var source = new LibraryGroup(Guid.NewGuid(), "Source", "Notes", "Notes", [], null, null);
        LibraryTypeItem[] items = [
            new(new LibraryItem("1", "Zulu", "", null, CreatedAt: old, UpdatedAt: recent), source),
            new(new LibraryItem("2", "", "", null, UpdatedAt: recent, Path: "alpha.txt"), source),
            new(new LibraryItem("3", "Beta", "", null), source)
        ];

        Assert.Equal(new[] { "2", "3", "1" }, LibrarySearchService.SortItems(items, LibrarySort.TitleAscending).Select(entry => entry.Item.Id));
        Assert.Equal(new[] { "1", "3", "2" }, LibrarySearchService.SortItems(items, LibrarySort.TitleDescending).Select(entry => entry.Item.Id));
        Assert.Equal(new[] { "2", "1", "3" }, LibrarySearchService.SortItems(items, LibrarySort.DateNewest).Select(entry => entry.Item.Id));
        Assert.Equal(new[] { "1", "2", "3" }, LibrarySearchService.SortItems(items, LibrarySort.DateOldest).Select(entry => entry.Item.Id));
        Assert.Equal(items.Select(entry => entry.Item.Id).Order(), LibrarySearchService.SortItems(items, LibrarySort.Random).Select(entry => entry.Item.Id).Order());
    }

    [Fact]
    public async Task SearchAsync_PreservesProviderDatesAndPaths()
    {
        var created = DateTimeOffset.Parse("2020-01-02T03:04:05Z");
        var updated = DateTimeOffset.Parse("2022-05-06T07:08:09Z");
        var stash = new FakeStashClient((instance, kind, _, _) =>
            Task.FromResult<IReadOnlyList<StashSearchResult>>([StashResult(instance, kind) with
                { CreatedAt = created, UpdatedAt = updated, Path = "stash/path" }]));
        var hydrus = new FakeHydrusClient((_, _, _) => Task.FromResult<IReadOnlyList<HydrusFile>>(
            [new HydrusFile(1, "File", "image/png") { CreatedAt = created, UpdatedAt = updated, Path = "hydrus/path" }]));

        var groups = await new LibrarySearchService(stash, hydrus)
            .SearchAsync([StashInstance("Stash")], [HydrusInstance("Hydrus")], "query");

        foreach (var group in groups.Where(group => group.Provider == "Stash" || group.Category == "Files"))
        {
            var item = Assert.Single(group.Items);
            Assert.Equal(created, item.CreatedAt);
            Assert.Equal(updated, item.UpdatedAt);
            Assert.Equal(group.Provider == "Stash" ? "stash/path" : "hydrus/path", item.Path);
        }
    }

    [Fact]
    public async Task SearchAsync_MapsAllEightStashCategories()
    {
        var stash = new FakeStashClient((instance, kind, _, _) =>
            Task.FromResult<IReadOnlyList<StashSearchResult>>([StashResult(instance, kind)]));

        var groups = await new LibrarySearchService(stash, new FakeHydrusClient())
            .SearchAsync([StashInstance("Stash")], [], "query");

        Assert.Equal(8, groups.Count);
        LibraryMediaType[] expected = [LibraryMediaType.Video, LibraryMediaType.Image, LibraryMediaType.Performer,
            LibraryMediaType.Studio, LibraryMediaType.Tag, LibraryMediaType.Marker,
            LibraryMediaType.Collection, LibraryMediaType.Collection];
        Assert.Equal(expected, groups.Select(group => Assert.Single(group.Items).MediaType));
    }

    [Fact]
    public void GroupByType_MergesProvidersAndInstancesInEnumOrderAndRetainsSources()
    {
        var image = new LibraryItem("image", "Image", "", null);
        var video = new LibraryItem("video", "Video", "", null, MediaType: LibraryMediaType.Video);
        var first = new LibraryGroup(Guid.NewGuid(), "First", "Stash", "Images", [image, image], null, null);
        var second = new LibraryGroup(Guid.NewGuid(), "Second", "Hydrus", "Files", [video, image], null, null);
        var empty = first with { Category = "Performers", Items = [] };
        var failed = first with { Category = "Scenes", Items = [], Error = "Search failed." };

        var groups = LibrarySearchService.GroupByType([second, first, empty, failed]);

        Assert.Equal(new[] { LibraryMediaType.Image, LibraryMediaType.Video }, groups.Select(group => group.Type));
        Assert.Equal(3, groups[0].Items.Count);
        Assert.Equal(new[] { second, first }, groups[0].Sources);
        Assert.Same(second, groups[0].Items[0].Source);
        Assert.Same(first, groups[0].Items[1].Source);
        Assert.Same(image, groups[0].Items[1].Item);
        Assert.Same(second, Assert.Single(groups[1].Sources));
        Assert.Empty(LibrarySearchService.GroupByType([empty, failed]));
        Assert.Empty(LibrarySearchService.GroupByType([]));
        Assert.NotNull(failed.Error);
    }

    [Fact]
    public void GroupByType_InterleavesSourcesBeforeBothPreviewLimitsAndPreservesSourceRanking()
    {
        LibraryGroup Source(string name, string provider, int count) => new(Guid.NewGuid(), name, provider, "Images",
            Enumerable.Range(1, count).Select(index => new LibraryItem($"{name}-{index}", name, "", null)).ToArray(), null, null);
        var first = Source("First", "Stash", 25);
        var second = Source("Second", "Stash", 25);
        var third = Source("Third", "Hydrus", 2);

        var group = Assert.Single(LibrarySearchService.GroupByType([first, second, third]));

        Assert.Equal(new[] { "First-1", "Second-1", "Third-1", "First-2", "Second-2" },
            group.Items.Take(5).Select(entry => entry.Item.Id));
        Assert.Equal(new[] { "First-3", "Second-3" }, group.Items.Skip(6).Take(2).Select(entry => entry.Item.Id));
        Assert.Equal(12, group.Items.Take(25).Count(entry => entry.Source == first));
        Assert.Equal(11, group.Items.Take(25).Count(entry => entry.Source == second));
        Assert.Equal(2, group.Items.Take(25).Count(entry => entry.Source == third));
        Assert.Equal(first.Items, group.Items.Where(entry => entry.Source == first).Select(entry => entry.Item));
        Assert.Equal(52, group.Items.Count);
    }

    [Fact]
    public void GroupByType_MixesInstancesRatherThanWeightingOneInstanceByItsCategoryCount()
    {
        var firstId = Guid.NewGuid();
        var first = new LibraryGroup(firstId, "First", "Stash", "Galleries",
            [new("gallery", "Gallery", "", null, MediaType: LibraryMediaType.Collection)], null, null);
        var sameInstance = first with { Category = "Groups", Items = [new("group", "Group", "", null, MediaType: LibraryMediaType.Collection)] };
        var second = first with { InstanceId = Guid.NewGuid(), InstanceName = "Second", Items = [new("second", "Second", "", null, MediaType: LibraryMediaType.Collection)] };

        var group = Assert.Single(LibrarySearchService.GroupByType([first, sameInstance, second]));

        Assert.Equal(new[] { "gallery", "second", "group" }, group.Items.Select(entry => entry.Item.Id));
        Assert.Equal(3, group.Sources.Count);
    }

    [Fact]
    public async Task SearchAsync_SearchesEveryKindAndSourceAndClampsEachGroup()
    {
        StashInstance[] stashInstances = [StashInstance("First"), StashInstance("Second")];
        HydrusInstance[] hydrusInstances = [HydrusInstance("Third"), HydrusInstance("Fourth")];
        var stashCalls = new ConcurrentBag<(Guid Id, StashSearchKind Kind, string Query)>();
        var hydrusCalls = new ConcurrentBag<Guid>();
        var stash = new FakeStashClient((instance, kind, query, _) =>
        {
            stashCalls.Add((instance.Id, kind, query));
            return Task.FromResult<IReadOnlyList<StashSearchResult>>(Enumerable.Range(1, 8)
                .Select(index => StashResult(instance, kind, index)).ToArray());
        });
        var hydrus = new FakeHydrusClient((instance, tags, _) =>
        {
            hydrusCalls.Add(instance.Id);
            Assert.Equal(new[] { "creator:some person", "tag" }, tags);
            return Task.FromResult<IReadOnlyList<HydrusFile>>(Enumerable.Range(1, 9)
                .Select(index => new HydrusFile(6000000000L + index, $"File {index}", "image/png")).ToArray());
        });

        var groups = await new LibrarySearchService(stash, hydrus)
            .SearchAsync(stashInstances, hydrusInstances, "  creator:some person, tag  ");

        Assert.Equal(20, groups.Count);
        Assert.Equal(16, stashCalls.Count);
        Assert.Equal(hydrusInstances.Select(instance => instance.Id).Order(), hydrusCalls.Order());
        foreach (var instance in stashInstances)
        {
            foreach (var kind in Enum.GetValues<StashSearchKind>())
            {
                Assert.Single(stashCalls, call => call == (instance.Id, kind, "creator:some person, tag"));
                var group = Assert.Single(groups, group => group.InstanceId == instance.Id && group.Category == kind.ToString());
                Assert.Equal(instance.Name, group.InstanceName);
                Assert.Equal("Stash", group.Provider);
                Assert.Equal(new LibraryItem("1", "Result 1", kind.ToString(), new Uri("https://stash.example/items/1"),
                    PreviewService.CreateLink("stash", instance.Id, kind.ToString(), "1"), kind switch
                    {
                        StashSearchKind.Scenes => LibraryMediaType.Video,
                        StashSearchKind.Performers => LibraryMediaType.Performer,
                        StashSearchKind.Studios => LibraryMediaType.Studio,
                        StashSearchKind.Tags => LibraryMediaType.Tag,
                        StashSearchKind.Markers => LibraryMediaType.Marker,
                        StashSearchKind.Galleries or StashSearchKind.Groups => LibraryMediaType.Collection,
                        _ => LibraryMediaType.Image
                    }), group.Items[0]);
                Assert.Equal(new[] { "1", "2", "3", "4", "5" }, group.Items.Select(item => item.Id));
            }
        }

        foreach (var instance in hydrusInstances)
        {
            var group = Assert.Single(groups, group => group.InstanceId == instance.Id && group.Category == "Files");
            Assert.Equal(instance.Name, group.InstanceName);
            Assert.Equal("Hydrus", group.Provider);
            Assert.Equal("Files", group.Category);
            Assert.Null(group.SearchUrl);
            Assert.Equal(new LibraryItem("6000000001", "File 1", "image/png", null,
                new Uri($"/previews/hydrus/{instance.Id}/files/6000000001", UriKind.Relative)), group.Items[0]);
            Assert.Equal(new[] { "6000000001", "6000000002", "6000000003", "6000000004", "6000000005" }, group.Items.Select(item => item.Id));
        }

        Assert.All(groups, group =>
        {
            Assert.Equal(group.Provider == "Hydrus" && group.Category == "Tags" ? 0 : 5, group.Items.Count);
            Assert.Null(group.Error);
        });
    }

    [Fact]
    public async Task SearchAsync_UsesLocalPreviewLinksEvenWhenStashReturnsAnUntrustedThumbnail()
    {
        var instance = StashInstance("Stash") with { ApiKey = "private-key" };
        var stash = new FakeStashClient((source, kind, _, _) => Task.FromResult<IReadOnlyList<StashSearchResult>>(
            [StashResult(source, kind) with { ThumbnailUrl = new Uri("https://evil.example/thumbnail?apikey=private-key") }]));

        var groups = await new LibrarySearchService(stash, new FakeHydrusClient()).SearchAsync([instance], [], "query");

        Assert.All(groups, group =>
        {
            var item = Assert.Single(group.Items);
            var link = item.PreviewUrl!.OriginalString;
            Assert.Equal($"/previews/stash/{instance.Id}/{group.Category.ToLowerInvariant()}/1", link);
            Assert.DoesNotContain("private-key", link);
            Assert.DoesNotContain("evil.example", link);
        });
    }

    [Theory]
    [InlineData(false, 5)]
    [InlineData(true, 5)]
    [InlineData(false, 25)]
    [InlineData(true, 25)]
    public async Task SearchAsync_IsolatesFailuresWithoutExposingProviderErrors(bool providerCancellation, int limit)
    {
        var failingStash = StashInstance("Failing category");
        var healthyStash = StashInstance("Healthy instance");
        var failingHydrus = HydrusInstance("Failing files");
        var healthyHydrus = HydrusInstance("Healthy files");
        Exception failure = providerCancellation
            ? new OperationCanceledException("private-key provider timeout")
            : new HttpRequestException("private-key provider response", null, HttpStatusCode.BadRequest);
        var stash = new FakeStashClient((instance, kind, _, _) =>
            instance.Id == failingStash.Id && kind == StashSearchKind.Images
                ? Task.FromException<IReadOnlyList<StashSearchResult>>(failure)
                : Task.FromResult<IReadOnlyList<StashSearchResult>>([StashResult(instance, kind)]));
        var hydrus = new FakeHydrusClient((instance, _, _) => instance.Id == failingHydrus.Id
            ? Task.FromException<IReadOnlyList<HydrusFile>>(failure)
            : Task.FromResult<IReadOnlyList<HydrusFile>>([new HydrusFile(1, "File", "image/png")]));

        var groups = await new LibrarySearchService(stash, hydrus)
            .SearchAsync([failingStash, healthyStash], [failingHydrus, healthyHydrus], "query", limit: limit);

        Assert.Equal(20, groups.Count);
        Assert.Equal(18, groups.Count(group => group.Error is null));
        var failedGroups = groups.Where(group => group.Error is not null).ToArray();
        Assert.Equal(2, failedGroups.Length);
        Assert.Contains(failedGroups, group => group.InstanceId == failingStash.Id && group.Category == "Images" && group.SearchUrl is not null);
        Assert.Contains(failedGroups, group => group.InstanceId == failingHydrus.Id && group.Category == "Files" && group.SearchUrl is null);
        Assert.All(failedGroups, group =>
        {
            Assert.Empty(group.Items);
            Assert.Equal("Search failed. Check the instance connection and try again.", group.Error);
        });
        Assert.All(groups.Where(group => group.Error is null && !(group.Provider == "Hydrus" && group.Category == "Tags")),
            group => Assert.Single(group.Items));
        Assert.All(LibrarySearchService.GroupByType(groups).SelectMany(group => group.Sources), source => Assert.Null(source.Error));
        Assert.Equal(2, groups.Count(group => group.Error is not null));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(HttpStatusCode.BadGateway, false)]
    [InlineData(null, true)]
    public async Task SearchAsync_ReportsAnOutageOnceAndCancelsOnlyThatBackendsWork(HttpStatusCode? status, bool timedOut)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var offline = StashInstance("Offline Stash");
        var healthy = StashInstance("Healthy Stash");
        var sixStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var canceled = 0;
        var stash = new FakeStashClient(async (instance, kind, _, token) =>
        {
            if (instance.Id == healthy.Id)
            {
                return [StashResult(instance, kind)];
            }

            if (Interlocked.Increment(ref calls) == 6)
            {
                sixStarted.SetResult();
            }
            if (kind == StashSearchKind.Scenes)
            {
                await sixStarted.Task.WaitAsync(token);
                throw timedOut ? new TaskCanceledException("private timeout details")
                    : new HttpRequestException("private connection details", null, status);
            }

            try
            {
                await Task.Delay(Timeout.Infinite, token);
                return [];
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                Interlocked.Increment(ref canceled);
                throw;
            }
        });

        var groups = await new LibrarySearchService(stash, new FakeHydrusClient())
            .SearchAsync([offline, healthy], [], "query", cancellation.Token);

        Assert.Equal(6, calls);
        Assert.Equal(5, canceled);
        var warning = Assert.Single(groups, group => group.Error is not null);
        Assert.Equal(offline.Id, warning.InstanceId);
        Assert.Equal("Backend is not reachable.", warning.Error);
        Assert.All(groups.Where(group => group.InstanceId == offline.Id), group => Assert.True(group.BackendUnreachable));
        Assert.Equal(8, groups.Count(group => group.InstanceId == healthy.Id && group.Items.Count == 1));
    }

    [Fact]
    public async Task SearchAsync_EncodesTrimmedQueryForEveryStashCategory()
    {
        var instance = StashInstance("Nested UI") with { WebUiUrl = new Uri("https://stash.example/library/") };
        const string query = "  name & value/#?+=, non-ascii \u00e9  ";
        var seenQueries = new ConcurrentBag<string>();
        var stash = new FakeStashClient((_, _, capturedQuery, _) =>
        {
            seenQueries.Add(capturedQuery);
            return Task.FromResult<IReadOnlyList<StashSearchResult>>([]);
        });

        var groups = await new LibrarySearchService(stash, new FakeHydrusClient()).SearchAsync([instance], [], query);

        Assert.Equal(8, seenQueries.Count);
        Assert.All(seenQueries, capturedQuery => Assert.Equal(query.Trim(), capturedQuery));
        foreach (var group in groups)
        {
            var route = group.Category == "Markers" ? "scenes/markers" : group.Category.ToLowerInvariant();
            Assert.Equal($"https://stash.example/library/{route}?q=name%20%26%20value%2F%23%3F%2B%3D%2C%20non-ascii%20%C3%A9",
                group.SearchUrl!.AbsoluteUri);
            Assert.Empty(group.Items);
            Assert.Null(group.Error);
        }
    }

    [Theory]
    [InlineData(" creator:some person, , tag with spaces ,, -excluded ", new[] { "creator:some person", "tag with spaces", "-excluded" })]
    [InlineData("one tag", new[] { "one tag" })]
    [InlineData(" , , \t ", new string[0])]
    [InlineData("", new string[0])]
    public async Task ParseHydrusTags_UsesTheSameCommaSeparatedTagsAsSearch(string query, string[] expected)
    {
        Assert.Equal(expected, LibrarySearchService.ParseHydrusTags(query));
        var hydrus = new FakeHydrusClient((_, tags, _) =>
        {
            Assert.Equal(expected, tags);
            return Task.FromResult<IReadOnlyList<HydrusFile>>([]);
        });

        var groups = await new LibrarySearchService(new FakeStashClient(), hydrus)
            .SearchAsync([], [HydrusInstance("Files")], query);

        Assert.Equal(new[] { "Files", "Tags" }, groups.Select(group => group.Category));
        Assert.All(groups, group =>
        {
            Assert.Null(group.Error);
            Assert.Null(group.SearchUrl);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SearchAsync_PreservesPreCanceledCallerEvenWithoutInstances(bool includeInstances)
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var service = new LibrarySearchService(new FakeStashClient(), new FakeHydrusClient());

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SearchAsync(
            includeInstances ? [StashInstance("Stash")] : [],
            includeInstances ? [HydrusInstance("Hydrus")] : [], "query", cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(25)]
    public async Task SearchAsync_CancelsActiveAndQueuedWork(int limit)
    {
        using var cancellation = new CancellationTokenSource();
        var sixStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        async Task WaitForCancellation(CancellationToken token)
        {
            Assert.True(token.CanBeCanceled);
            if (Interlocked.Increment(ref calls) == 6)
            {
                sixStarted.SetResult();
            }
            await Task.Delay(Timeout.Infinite, token);
        }
        var stash = new FakeStashClient(async (_, _, _, token) =>
        {
            await WaitForCancellation(token);
            return [];
        });
        var hydrus = new FakeHydrusClient(async (_, _, token) =>
        {
            await WaitForCancellation(token);
            return [];
        });
        var search = new LibrarySearchService(stash, hydrus).SearchAsync(
            [StashInstance("First"), StashInstance("Second"), StashInstance("Queued")],
            [HydrusInstance("Queued files")], "query", cancellation.Token, limit,
            [new LocalLibraryInstance(Guid.NewGuid(), "Queued notes", Path.GetTempPath(), LocalLibraryKind.Notes)]);

        try
        {
            await sixStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => search.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(6, calls);
        }
        finally
        {
            cancellation.Cancel();
        }
    }

    [Theory]
    [InlineData(5)]
    [InlineData(25)]
    public async Task SearchAsync_LimitsConcurrencyAcrossProvidersAndCapturesQueryPerCall(int limit)
    {
        var sixStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var calls = 0;
        async Task Block()
        {
            var current = Interlocked.Increment(ref active);
            Assert.InRange(current, 1, 6);
            if (Interlocked.Increment(ref calls) == 6)
            {
                sixStarted.TrySetResult();
            }
            await release.Task;
            Interlocked.Decrement(ref active);
        }
        var stash = new FakeStashClient(async (_, _, query, _) =>
        {
            Assert.Equal("original tag, second", query);
            await Block();
            return [];
        });
        var hydrus = new FakeHydrusClient(async (_, tags, _) =>
        {
            Assert.Equal(new[] { "original tag", "second" }, tags);
            await Block();
            return [];
        }, async (_, capturedQuery, selectedLimit, _) =>
        {
            Assert.Equal("original tag, second", capturedQuery);
            Assert.Equal(limit, selectedLimit);
            await Block();
            return [];
        });
        var query = "  original tag, second  ";
        var search = new LibrarySearchService(stash, hydrus).SearchAsync(
            [StashInstance("Stash")], Enumerable.Range(1, 7).Select(index => HydrusInstance($"Hydrus {index}")).ToArray(), query, limit: limit);
        query = "edited query";

        try
        {
            await sixStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(6, Volatile.Read(ref calls));
            Assert.False(search.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
        }

        var groups = await search.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(22, calls);
        Assert.Equal(22, groups.Count);
        Assert.All(groups, group => Assert.Null(group.Error));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(25)]
    public async Task SearchAsync_PropagatesLimitClampsAndCombinesMixedTypesAcrossSources(int limit)
    {
        StashInstance[] stashInstances = [StashInstance("First"), StashInstance("Second")];
        HydrusInstance[] hydrusInstances = [HydrusInstance("Third"), HydrusInstance("Fourth")];
        using var cancellation = new CancellationTokenSource();
        var stash = new LimitAwareStashClient((instance, kind, query, selectedLimit, token) =>
        {
            Assert.Equal(limit, selectedLimit);
            Assert.True(token.CanBeCanceled);
            Assert.Equal("query", query);
            return Task.FromResult<IReadOnlyList<StashSearchResult>>(Enumerable.Range(1, 30)
                .Select(index => StashResult(instance, kind, index)).ToArray());
        });
        var hydrus = new LimitAwareHydrusClient((_, tags, selectedLimit, token) =>
        {
            Assert.Equal(limit, selectedLimit);
            Assert.True(token.CanBeCanceled);
            Assert.Equal(new[] { "query" }, tags);
            string[] mimes = ["image/png", "video/webm", "application/pdf", "application/x-cbz"];
            return Task.FromResult<IReadOnlyList<HydrusFile>>(Enumerable.Range(1, 32)
                .Select(index => new HydrusFile(index, $"File {index}", mimes[(index - 1) % mimes.Length])).ToArray());
        });

        var service = new LibrarySearchService(stash, hydrus);
        var groups = limit == 5
            ? await service.SearchAsync(stashInstances, hydrusInstances, "query", cancellation.Token)
            : await service.SearchAsync(stashInstances, hydrusInstances, "query", cancellation.Token, limit);
        var typed = LibrarySearchService.GroupByType(groups);

        Assert.Equal(20, groups.Count);
        Assert.All(groups, group =>
        {
            Assert.Null(group.Error);
            Assert.Equal(group.Provider == "Hydrus" && group.Category == "Tags" ? 0 : limit, group.Items.Count);
            Assert.All(group.Items, item => Assert.Equal(
                PreviewService.CreateLink(group.Provider, group.InstanceId, group.Category, item.Id), item.PreviewUrl));
        });
        Assert.Equal(new[] { LibraryMediaType.Image, LibraryMediaType.Video, LibraryMediaType.Performer, LibraryMediaType.Collection,
            LibraryMediaType.TextEbook, LibraryMediaType.Comic, LibraryMediaType.Studio, LibraryMediaType.Tag, LibraryMediaType.Marker },
            typed.Select(group => group.Type));
        Assert.Equal(4, typed[0].Sources.Count);
        Assert.Equal(4, typed[1].Sources.Count);
        Assert.Equal(4, typed[3].Sources.Count);
        Assert.All(typed.Where(group => group.Type is not (LibraryMediaType.Image or LibraryMediaType.Video or LibraryMediaType.Collection)),
            group => Assert.Equal(2, group.Sources.Count));
        Assert.All(typed.SelectMany(group => group.Items), entry =>
        {
            Assert.Contains(groups, source => ReferenceEquals(source, entry.Source));
            Assert.Contains(entry.Source.Items, item => ReferenceEquals(item, entry.Item));
        });
    }

    [Fact]
    public async Task SearchAsync_ExpandedLimitSupportsLegacyClientImplementations()
    {
        var stash = new FakeStashClient((instance, kind, _, _) =>
            Task.FromResult<IReadOnlyList<StashSearchResult>>(Enumerable.Range(1, 30).Select(index => StashResult(instance, kind, index)).ToArray()));
        var hydrus = new FakeHydrusClient((_, _, _) =>
            Task.FromResult<IReadOnlyList<HydrusFile>>(Enumerable.Range(1, 30).Select(index => new HydrusFile(index, "File", "image/png")).ToArray()));

        var groups = await new LibrarySearchService(stash, hydrus)
            .SearchAsync([StashInstance("Legacy Stash")], [HydrusInstance("Legacy Hydrus")], "query", limit: 25);

        Assert.Equal(10, groups.Count);
        Assert.All(groups, group =>
        {
            Assert.Null(group.Error);
            Assert.Equal(group.Provider == "Hydrus" && group.Category == "Tags" ? 0 : 25, group.Items.Count);
        });
    }

    [Theory]
    [InlineData("image/jpeg", LibraryMediaType.Image)]
    [InlineData("video/mp4", LibraryMediaType.Video)]
    [InlineData("application/pdf", LibraryMediaType.TextEbook)]
    [InlineData("application/epub+zip", LibraryMediaType.TextEbook)]
    [InlineData("text/plain", LibraryMediaType.TextEbook)]
    [InlineData("TEXT/HTML; charset=utf-8", LibraryMediaType.TextEbook)]
    [InlineData("application/vnd.comicbook+zip", LibraryMediaType.Comic)]
    [InlineData("application/vnd.comicbook+rar", LibraryMediaType.Comic)]
    [InlineData("application/vnd.comicbook-rar", LibraryMediaType.Comic)]
    [InlineData("application/x-cbz", LibraryMediaType.Comic)]
    [InlineData("application/x-cbr", LibraryMediaType.Comic)]
    [InlineData("application/x-cb7", LibraryMediaType.Comic)]
    [InlineData("application/x-cbt", LibraryMediaType.Comic)]
    [InlineData("application/x-cba", LibraryMediaType.Comic)]
    [InlineData("application/octet-stream", null)]
    [InlineData("application/zip", null)]
    [InlineData("audio/mpeg", null)]
    public async Task SearchAsync_MapsSupportedHydrusMimeTypesWithoutFabricatingImages(string mime, LibraryMediaType? expected)
    {
        var hydrus = new FakeHydrusClient((_, _, _) =>
            Task.FromResult<IReadOnlyList<HydrusFile>>([new HydrusFile(1, "File", mime)]));

        var source = Assert.Single(await new LibrarySearchService(new FakeStashClient(), hydrus)
            .SearchAsync([], [HydrusInstance("Hydrus")], "query"), group => group.Category == "Files");

        Assert.Null(source.Error);
        if (expected is { } type)
        {
            Assert.Equal(type, Assert.Single(source.Items).MediaType);
            Assert.Same(source, Assert.Single(LibrarySearchService.GroupByType([source])).Items[0].Source);
        }
        else
        {
            Assert.Empty(source.Items);
            Assert.Empty(LibrarySearchService.GroupByType([source]));
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(26)]
    public async Task SearchAsync_RejectsUnsupportedLimitsEvenWithoutInstances(int limit)
    {
        var service = new LibrarySearchService(new FakeStashClient(), new FakeHydrusClient());

        var exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.SearchAsync([], [], "query", limit: limit));

        Assert.Equal("limit", exception.ParamName);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(25)]
    public async Task SearchAsync_HydrusTagsPreserveUnicodeCountsAndSafeAppLinks(int limit)
    {
        const string name = "creator:\u96ea & caf\u00e9/#?+=,tag";
        using var cancellation = new CancellationTokenSource();
        var hydrus = new FakeHydrusClient(searchTags: (_, query, selectedLimit, token) =>
        {
            Assert.Equal("original, query", query);
            Assert.Equal(limit, selectedLimit);
            Assert.True(token.CanBeCanceled);
            return Task.FromResult<IReadOnlyList<HydrusTag>>(Enumerable.Range(0, 30)
                .Select(index => new HydrusTag(index == 0 ? name : $"tag:{index}", 6000000000L + index)).ToArray());
        });

        var groups = await new LibrarySearchService(new FakeStashClient(), hydrus)
            .SearchAsync([], [HydrusInstance("Tags")], "  original, query  ", cancellation.Token, limit);

        var group = Assert.Single(groups, group => group.Category == "Tags");
        Assert.Null(group.SearchUrl);
        Assert.Null(group.Error);
        Assert.Equal(limit, group.Items.Count);
        Assert.Equal(new LibraryItem(name, name, "6000000000",
            new Uri($"/?q={Uri.EscapeDataString(name)}", UriKind.Relative), MediaType: LibraryMediaType.Tag), group.Items[0]);
        Assert.All(group.Items, item => Assert.Null(item.PreviewUrl));
        Assert.Equal(LibraryMediaType.Tag, Assert.Single(LibrarySearchService.GroupByType(groups)).Type);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SearchAsync_HydrusFilesAndTagsFailIndependently(bool failTags, bool providerCancellation)
    {
        Exception failure = providerCancellation ? new OperationCanceledException("private-key")
            : new HttpRequestException("private-key", null, HttpStatusCode.BadRequest);
        var hydrus = new FakeHydrusClient(
            (_, _, _) => !failTags ? Task.FromException<IReadOnlyList<HydrusFile>>(failure)
                : Task.FromResult<IReadOnlyList<HydrusFile>>([new HydrusFile(1, "File", "image/png")]),
            (_, _, _, _) => failTags ? Task.FromException<IReadOnlyList<HydrusTag>>(failure)
                : Task.FromResult<IReadOnlyList<HydrusTag>>([new HydrusTag("tag", 42)]));

        var groups = await new LibrarySearchService(new FakeStashClient(), hydrus)
            .SearchAsync([], [HydrusInstance("Hydrus")], "query");

        var failed = Assert.Single(groups, group => group.Error is not null);
        Assert.Equal(failTags ? "Tags" : "Files", failed.Category);
        Assert.Equal("Search failed. Check the instance connection and try again.", failed.Error);
        Assert.Empty(failed.Items);
        Assert.Null(failed.SearchUrl);
        Assert.Single(Assert.Single(groups, group => group.Error is null).Items);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(25)]
    public async Task SearchAsync_HydrusTagWorkHonorsCallerCancellation(int limit)
    {
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hydrus = new FakeHydrusClient(searchTags: async (_, _, selectedLimit, token) =>
        {
            Assert.Equal(limit, selectedLimit);
            Assert.True(token.CanBeCanceled);
            started.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return [];
        });
        var search = new LibrarySearchService(new FakeStashClient(), hydrus)
            .SearchAsync([], [HydrusInstance("Hydrus")], "query", cancellation.Token, limit);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => search.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(cancellation.Token, exception.CancellationToken);
        }
        finally
        {
            cancellation.Cancel();
        }
    }

    [Theory]
    [InlineData(5)]
    [InlineData(25)]
    public async Task SearchAsync_AggregatesTemporaryNotesAndCalibreWithLocalQueryRoutes(int limit)
    {
        var root = Directory.CreateTempSubdirectory("psm-aggregate-").FullName;
        try
        {
            const string query = "needle & \u96ea";
            for (var index = 0; index < 30; index++)
                await File.WriteAllTextAsync(Path.Combine(root, $"note-{index}.md"), query);
            var bookDirectory = Directory.CreateDirectory(Path.Combine(root, "Book")).FullName;
            await File.WriteAllTextAsync(Path.Combine(bookDirectory, "book.epub"), "fixture");
            await File.WriteAllTextAsync(Path.Combine(bookDirectory, "book.cbz"), "fixture");
            await using (var connection = new SqliteConnection($"Data Source={Path.Combine(root, "metadata.db")};Pooling=False"))
            {
                await connection.OpenAsync();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE books (id INTEGER PRIMARY KEY, title TEXT, path TEXT);
                    CREATE TABLE data (book INTEGER, name TEXT, format TEXT);
                    CREATE TABLE authors (id INTEGER PRIMARY KEY, name TEXT);
                    CREATE TABLE books_authors_link (book INTEGER, author INTEGER);
                    CREATE TABLE tags (id INTEGER PRIMARY KEY, name TEXT);
                    CREATE TABLE books_tags_link (book INTEGER, tag INTEGER);
                    CREATE TABLE comments (book INTEGER, text TEXT);
                    INSERT INTO books VALUES (1, $query, 'Book');
                    INSERT INTO data VALUES (1, 'book', 'EPUB'), (1, 'book', 'CBZ');
                    """;
                command.Parameters.AddWithValue("$query", query);
                await command.ExecuteNonQueryAsync();
            }
            LocalLibraryInstance[] libraries = [
                new(Guid.NewGuid(), "Notes fixture", root, LocalLibraryKind.Notes),
                new(Guid.NewGuid(), "Books fixture", root, LocalLibraryKind.CalibreText),
                new(Guid.NewGuid(), "Comics fixture", root, LocalLibraryKind.CalibreComic)];
            var groups = await new LibrarySearchService(new FakeStashClient(), new FakeHydrusClient(), new LocalLibraryClient())
                .SearchAsync([], [], $"  {query}  ", limit: limit, localLibraries: libraries);

            Assert.Equal(new[] { "Notes", "Calibre", "Calibre" }, groups.Select(group => group.Provider));
            Assert.Equal(new[] { "Notes", "Ebooks", "Comics" }, groups.Select(group => group.Category));
            Assert.Equal(new[] { LibraryMediaType.Note, LibraryMediaType.TextEbook, LibraryMediaType.Comic },
                groups.Select(group => group.Items[0].MediaType));
            Assert.Equal(limit, groups[0].Items.Count);
            Assert.All(groups.Skip(1), group => Assert.Single(group.Items));
            foreach (var group in groups)
            {
                Assert.Null(group.Error);
                Assert.Equal(libraries.Single(instance => instance.Id == group.InstanceId).Name, group.InstanceName);
                Assert.Equal($"/libraries/{group.InstanceId:D}?q={Uri.EscapeDataString(query)}", group.SearchUrl!.OriginalString);
                Assert.All(group.Items, item => Assert.StartsWith($"/libraries/{group.InstanceId:D}/items/", item.ItemUrl!.OriginalString));
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task SearchAsync_QueuesLocalWorkBehindTheSharedSixSlotGate()
    {
        var root = Directory.CreateTempSubdirectory("psm-aggregate-gate-").FullName;
        var sixStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var cancellation = new CancellationTokenSource();
        var stash = new FakeStashClient(async (_, _, _, token) =>
        {
            if (Interlocked.Increment(ref calls) == 6) sixStarted.SetResult();
            await release.Task.WaitAsync(token);
            return [];
        });
        var instance = new LocalLibraryInstance(Guid.NewGuid(), "Queued notes", root, LocalLibraryKind.Notes);
        var search = new LibrarySearchService(stash, new FakeHydrusClient()).SearchAsync(
            [StashInstance("Stash")], [], "needle", cancellation.Token, localLibraries: [instance]);
        try
        {
            await sixStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(6, Volatile.Read(ref calls));
            await File.WriteAllTextAsync(Path.Combine(root, "late.md"), "needle");
            release.SetResult();
            var groups = await search.WaitAsync(TimeSpan.FromSeconds(5));
            var local = Assert.Single(groups, group => group.Provider == "Notes");
            Assert.Null(local.Error);
            Assert.Equal("late.md", Assert.Single(local.Items).Title);
        }
        finally
        {
            cancellation.Cancel();
            release.TrySetResult();
            try { await search; }
            catch (OperationCanceledException) { }
            Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData(LocalLibraryKind.Notes)]
    [InlineData(LocalLibraryKind.CalibreText)]
    [InlineData(LocalLibraryKind.CalibreComic)]
    public async Task SearchAsync_LocalLibrariesHonorPreCancellationAndIsolateFailures(LocalLibraryKind kind)
    {
        var root = Directory.CreateTempSubdirectory("psm-aggregate-failure-").FullName;
        try
        {
            var instance = new LocalLibraryInstance(Guid.NewGuid(), "Fixture", Path.Combine(root, "missing-private-path"), kind);
            var service = new LibrarySearchService(new FakeStashClient(), new FakeHydrusClient());
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                service.SearchAsync([], [], "query", cancellation.Token, localLibraries: [instance]));
            Assert.Equal(cancellation.Token, exception.CancellationToken);

            var groups = await service.SearchAsync([], [HydrusInstance("Healthy")], "query", localLibraries: [instance]);
            var failed = Assert.Single(groups, group => group.Provider != "Hydrus");
            Assert.Equal("Search failed. Check the instance connection and try again.", failed.Error);
            Assert.Empty(failed.Items);
            Assert.NotNull(failed.SearchUrl);
            Assert.DoesNotContain("private-path", failed.SearchUrl.OriginalString);
            Assert.All(groups.Where(group => group.Provider == "Hydrus"), group => Assert.Null(group.Error));
            Assert.Empty(LibrarySearchService.GroupByType(groups));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private sealed class LimitAwareStashClient(
        Func<StashInstance, StashSearchKind, string, int, CancellationToken, Task<IReadOnlyList<StashSearchResult>>> search) : IStashGraphQlClient
    {
        public Task<IReadOnlyList<StashSearchResult>> SearchAsync(StashInstance instance, StashSearchKind kind, string query, int limit, CancellationToken cancellationToken = default) =>
            search(instance, kind, query, limit, cancellationToken);

        public Task<IReadOnlyList<StashSearchResult>> SearchAsync(StashInstance instance, StashSearchKind kind, string query, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Service must use the limit-aware overload.");

        public Task<StashObservation> ObserveAsync(StashInstance instance, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class LimitAwareHydrusClient(
        Func<HydrusInstance, IReadOnlyList<string>, int, CancellationToken, Task<IReadOnlyList<HydrusFile>>> search) : IHydrusClient
    {
        public Task<IReadOnlyList<HydrusFile>> SearchAsync(HydrusInstance instance, IReadOnlyList<string> tags, int limit, CancellationToken cancellationToken = default) =>
            search(instance, tags, limit, cancellationToken);

        public Task<IReadOnlyList<HydrusFile>> SearchAsync(HydrusInstance instance, IReadOnlyList<string> tags, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Service must use the limit-aware overload.");

        public Task<HydrusObservation> ObserveAsync(HydrusInstance instance, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<string> OpenSearchAsync(HydrusInstance instance, IReadOnlyList<string> tags, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private static StashInstance StashInstance(string name) =>
        new(Guid.NewGuid(), name, new Uri("https://stash.example/graphql"), new Uri("https://stash.example/"), null);

    private static HydrusInstance HydrusInstance(string name) =>
        new(Guid.NewGuid(), name, new Uri("https://hydrus.example/"), null);

    private static StashSearchResult StashResult(StashInstance instance, StashSearchKind kind, int index = 1) =>
        new(index.ToString(), $"Result {index}", kind, null, new Uri($"https://stash.example/items/{index}"), instance.Id, instance.Name);

    private sealed class FakeStashClient(
        Func<StashInstance, StashSearchKind, string, CancellationToken, Task<IReadOnlyList<StashSearchResult>>>? search = null) : IStashGraphQlClient
    {
        public Task<IReadOnlyList<StashSearchResult>> SearchAsync(StashInstance instance, StashSearchKind kind, string query, CancellationToken cancellationToken = default) =>
            search?.Invoke(instance, kind, query, cancellationToken) ?? Task.FromResult<IReadOnlyList<StashSearchResult>>([]);

        public Task<StashObservation> ObserveAsync(StashInstance instance, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeHydrusClient(
        Func<HydrusInstance, IReadOnlyList<string>, CancellationToken, Task<IReadOnlyList<HydrusFile>>>? search = null,
        Func<HydrusInstance, string, int, CancellationToken, Task<IReadOnlyList<HydrusTag>>>? searchTags = null) : IHydrusClient
    {
        public Task<IReadOnlyList<HydrusTag>> SearchTagsAsync(HydrusInstance instance, string query, int limit, CancellationToken cancellationToken = default) =>
            searchTags?.Invoke(instance, query, limit, cancellationToken) ?? Task.FromResult<IReadOnlyList<HydrusTag>>([]);

        public Task<IReadOnlyList<HydrusFile>> SearchAsync(HydrusInstance instance, IReadOnlyList<string> tags, CancellationToken cancellationToken = default) =>
            search?.Invoke(instance, tags, cancellationToken) ?? Task.FromResult<IReadOnlyList<HydrusFile>>([]);

        public Task<HydrusObservation> ObserveAsync(HydrusInstance instance, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<string> OpenSearchAsync(HydrusInstance instance, IReadOnlyList<string> tags, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}