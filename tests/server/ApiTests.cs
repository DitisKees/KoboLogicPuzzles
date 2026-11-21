using System.Net;
using System.Net.Http.Json;
using KoboNonogram.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace KoboNonogram.Server.Tests;

public sealed class ApiTests
{
    [Fact]
    public async Task DownloadsAndBacksUpPartialBoardThenRestoresAfterRestart()
    {
        using var database = new TestDatabase();
        database.SeedCatalog([TestData.Summary(12), TestData.Summary(13)]);
        using var source = TestData.Source(uri =>
        {
            Assert.Equal("/export.cgi", uri.AbsolutePath);
            Assert.Contains("id=13", uri.Query);
            return TestData.Ok(TestData.Export(13));
        });

        await using (var app = CreateApp(database, source))
        {
            await app.StartAsync();
            using var http = app.GetTestClient();
            var firstPage = await http.GetFromJsonAsync<PuzzleSummary[]>("/api/v1/puzzles?limit=1");
            Assert.Equal(13, Assert.Single(firstPage!).Id);
            var nextPage = await http.GetFromJsonAsync<PuzzleSummary[]>("/api/v1/puzzles?beforeId=13");
            Assert.Equal(12, Assert.Single(nextPage!).Id);

            using var queue = await http.PostAsJsonAsync("/api/v1/offline-queue", new OfflineQueueRequest(1, []));
            queue.EnsureSuccessStatusCode();
            var response = await queue.Content.ReadFromJsonAsync<OfflineQueueResponse>();
            Assert.Equal(13, Assert.Single(response!.Puzzles).Id);

            using var backup = await http.PostAsJsonAsync("/api/v1/progress",
                new ProgressUpload([new ProgressUpdate(13, "#x....", false)]));
            backup.EnsureSuccessStatusCode();
            var receipt = await backup.Content.ReadFromJsonAsync<ProgressReceipt>();
            Assert.Equal(1, receipt!.SavedCount);
            Assert.NotNull(receipt.BackedUpAt);
        }

        database.SeedCatalog([]);
        using var unavailableSource = TestData.Source(_ => throw new InvalidOperationException("Unexpected upstream call."));
        await using var restarted = CreateApp(database, unavailableSource);
        await restarted.StartAsync();
        using var freshClient = restarted.GetTestClient();
        var restored = Assert.Single((await freshClient.GetFromJsonAsync<SavedBoard[]>("/api/v1/restore"))!);

        Assert.Equal(13, restored.Puzzle.Id);
        Assert.Equal("#x....", restored.Progress.Marks);
        Assert.False(restored.Progress.Completed);
        Assert.Equal(13, (await freshClient.GetFromJsonAsync<PuzzleDefinition>("/api/v1/puzzles/13"))?.Id);
    }

    [Fact]
    public async Task InvalidRequestsAndFailedDownloadsLeaveSavedBoardsIntact()
    {
        using var database = new TestDatabase();
        database.SeedCatalog([TestData.Summary(13)]);
        database.Store.CachePuzzle(TestData.Puzzle(12));
        new ProgressBackup(database.Store, TimeProvider.System).Save([new ProgressUpdate(12, "#x....", false)]);
        using var source = TestData.Source(_ => new HttpResponseMessage(HttpStatusCode.BadGateway));
        await using var app = CreateApp(database, source);
        await app.StartAsync();
        using var http = app.GetTestClient();

        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/api/v1/puzzles/99")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.GetAsync("/api/v1/puzzles?limit=0")).StatusCode);

        using var invalidQueue = await http.PostAsJsonAsync("/api/v1/offline-queue", new OfflineQueueRequest(101, []));
        Assert.Equal(HttpStatusCode.BadRequest, invalidQueue.StatusCode);

        using var invalidBackup = await http.PostAsJsonAsync("/api/v1/progress",
            new ProgressUpload([new ProgressUpdate(12, "#x....", true)]));
        Assert.Equal(HttpStatusCode.BadRequest, invalidBackup.StatusCode);

        using var failedDownload = await http.PostAsJsonAsync("/api/v1/offline-queue", new OfflineQueueRequest(1, []));
        Assert.Equal(HttpStatusCode.BadGateway, failedDownload.StatusCode);

        using var emptyBackup = await http.PostAsJsonAsync("/api/v1/progress", new ProgressUpload([]));
        emptyBackup.EnsureSuccessStatusCode();
        var receipt = await emptyBackup.Content.ReadFromJsonAsync<ProgressReceipt>();
        Assert.Null(receipt!.BackedUpAt);
        Assert.Equal("#x....", Assert.Single(database.Store.RestoreProgress()).Progress.Marks);
    }

    private static WebApplication CreateApp(TestDatabase database, WebPbnClient source)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration["Storage:DatabasePath"] = database.Path;
        builder.Configuration["WebPbn:MinArea"] = "1";
        builder.Configuration["CatalogRefresh:Enabled"] = "false";
        Server.AddServices(builder);
        builder.Services.AddSingleton(source);
        var app = builder.Build();
        app.Services.GetRequiredService<PuzzleStore>().Initialize();
        Server.MapEndpoints(app);
        return app;
    }
}
