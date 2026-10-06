using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Server.Implementations.Tests.Item;
using Jellyfin.Server.Implementations.Trickplay;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.IO;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Trickplay;

/// <summary>
/// Tests for the cleanup path in <see cref="TrickplayManager.RefreshTrickplayDataAsync"/>. The
/// server uses that path when a library has trickplay extraction disabled. The scheduled task calls
/// the method one time for each video in the library. So the path must not write to the database
/// when a video has no data to clean up.
/// </summary>
public sealed class TrickplayManagerTests : SqliteDbTestFixture
{
    private const string MissingTrickplayDirectory = "/var/lib/jellyfin/trickplay/does-not-exist";

    private static readonly LibraryOptions _extractionDisabled = new()
    {
        EnableTrickplayImageExtraction = false,
        SaveTrickplayWithMedia = false
    };

    private readonly SqlCommandRecorder _commands;

    /// <summary>
    /// Initializes a new instance of the <see cref="TrickplayManagerTests"/> class.
    /// </summary>
    public TrickplayManagerTests()
        : this(new SqlCommandRecorder())
    {
    }

    private TrickplayManagerTests(SqlCommandRecorder commands)
        : base(commands)
    {
        _commands = commands;
    }

    [Fact]
    public async Task RefreshTrickplayDataAsync_ExtractionDisabledWithExistingData_RemovesRows()
    {
        var video = new Video { Id = Guid.NewGuid() };
        Seed(video.Id, 320);
        Seed(video.Id, 640);

        var manager = CreateTrickplayManager();
        _commands.Clear();
        await manager.RefreshTrickplayDataAsync(video, false, _extractionDisabled, CancellationToken.None);

        Assert.Empty(GetWidths(video.Id));
        Assert.Single(_commands.Deletes);
    }

    [Fact]
    public async Task RefreshTrickplayDataAsync_ExtractionDisabled_LeavesOtherItemsAlone()
    {
        var video = new Video { Id = Guid.NewGuid() };
        var otherItemId = Guid.NewGuid();
        Seed(video.Id, 320);
        Seed(otherItemId, 320);

        var manager = CreateTrickplayManager();
        await manager.RefreshTrickplayDataAsync(video, false, _extractionDisabled, CancellationToken.None);

        Assert.Empty(GetWidths(video.Id));
        Assert.Equal([320], GetWidths(otherItemId));
    }

    [Fact]
    public async Task RefreshTrickplayDataAsync_ExtractionDisabledWithNothingToClean_ReadsButDoesNotWrite()
    {
        // This is the common case, and it is the reason for the fix. The video has no rows and no
        // directory. The scheduled task calls this path one time for each video in the library. So
        // the path must send one read and no write.
        var video = new Video { Id = Guid.NewGuid() };
        var otherItemId = Guid.NewGuid();
        Seed(otherItemId, 320);

        var manager = CreateTrickplayManager();
        _commands.Clear();
        await manager.RefreshTrickplayDataAsync(video, false, _extractionDisabled, CancellationToken.None);

        var statement = Assert.Single(_commands.Statements);
        Assert.StartsWith("SELECT", statement, StringComparison.Ordinal);
        Assert.Empty(_commands.Deletes);
        Assert.Equal([320], GetWidths(otherItemId));
    }

    [Fact]
    public async Task RefreshTrickplayDataAsync_ExtractionDisabledSaveWithMedia_KeepsRows()
    {
        // Trickplay saved next to the media is user-managed. The cleanup path catalogs it but must
        // not remove it. The generate path would prune the folder that has no matching row.
        var trickplayDirectory = Path.Combine(Path.GetTempPath(), "jellyfin-trickplay-" + Guid.NewGuid());
        var tileDirectory = Path.Combine(trickplayDirectory, "480 - 10x10");
        var userDirectory = Path.Combine(trickplayDirectory, "user-managed");
        Directory.CreateDirectory(tileDirectory);
        Directory.CreateDirectory(userDirectory);
        File.WriteAllBytes(Path.Combine(tileDirectory, "0.jpg"), [0]);
        try
        {
            var video = CreateGeneratableVideo();
            Seed(video.Id, 480);
            var options = new LibraryOptions
            {
                EnableTrickplayImageExtraction = false,
                SaveTrickplayWithMedia = true
            };

            var manager = CreateTrickplayManager(trickplayDirectory);
            _commands.Clear();
            await manager.RefreshTrickplayDataAsync(video, false, options, CancellationToken.None);

            Assert.Equal([480], GetWidths(video.Id));
            Assert.Empty(_commands.Deletes);
            Assert.True(File.Exists(Path.Combine(tileDirectory, "0.jpg")));
            Assert.True(Directory.Exists(userDirectory));
        }
        finally
        {
            Directory.Delete(trickplayDirectory, true);
        }
    }

    [Fact]
    public async Task RefreshTrickplayDataAsync_NullLibraryOptions_IsANoOp()
    {
        var video = new Video { Id = Guid.NewGuid() };
        Seed(video.Id, 320);

        var manager = CreateTrickplayManager();
        await manager.RefreshTrickplayDataAsync(video, false, null!, CancellationToken.None);

        Assert.Equal([320], GetWidths(video.Id));
    }

    [Fact]
    public async Task DeleteTrickplayDataAsync_DisposesDbContext()
    {
        var contexts = new List<JellyfinDbContext>();
        var factory = new Mock<IDbContextFactory<JellyfinDbContext>>();
        factory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                var context = CreateDbContext();
                contexts.Add(context);
                return context;
            });

        // Only the database context factory is used when deleting trickplay data.
        var trickplayManager = new TrickplayManager(
            NullLogger<TrickplayManager>.Instance,
            null!,
            null!,
            null!,
            null!,
            null!,
            factory.Object,
            null!,
            null!);

        await trickplayManager.DeleteTrickplayDataAsync(Guid.NewGuid(), CancellationToken.None);

        var context = Assert.Single(contexts);
        Assert.Throws<ObjectDisposedException>(() => context.Model);
    }

    private void Seed(Guid itemId, int width)
    {
        using var context = CreateDbContext();
        context.TrickplayInfos.Add(new TrickplayInfo
        {
            ItemId = itemId,
            Width = width,
            Height = 180,
            TileWidth = 10,
            TileHeight = 10,
            ThumbnailCount = 100,
            Interval = 10000,
            Bandwidth = 1
        });
        context.SaveChanges();
    }

    private int[] GetWidths(Guid itemId)
    {
        using var context = CreateDbContext();
        return context.TrickplayInfos
            .Where(i => i.ItemId.Equals(itemId))
            .Select(i => i.Width)
            .OrderBy(w => w)
            .ToArray();
    }

    /// <summary>
    /// Creates a video that passes the trickplay generation checks without the static library
    /// managers. A mock is used because a BaseItem subclass in this assembly would break
    /// BaseItemKindTests.
    /// </summary>
    private static Video CreateGeneratableVideo()
    {
        var video = new Mock<Video> { CallBase = true };
        video.Setup(v => v.GetMediaStreams()).Returns([new MediaStream { Type = MediaStreamType.Video }]);
        video.Protected().Setup<bool>("IsActiveRecording").Returns(false);
        video.Object.Id = Guid.NewGuid();
        video.Object.RunTimeTicks = TimeSpan.FromHours(1).Ticks;
        return video.Object;
    }

    private TrickplayManager CreateTrickplayManager(string trickplayDirectory = MissingTrickplayDirectory)
    {
        var config = new Mock<IServerConfigurationManager>();
        config.Setup(c => c.Configuration).Returns(new ServerConfiguration());

        // By default use a path that does not exist, so the cleanup only uses the database branch.
        var pathManager = new Mock<IPathManager>();
        pathManager.Setup(p => p.GetTrickplayDirectory(It.IsAny<BaseItem>(), It.IsAny<bool>()))
            .Returns(trickplayDirectory);

        // The cleanup path only uses the logger, the config, the path manager and the database
        // factory. EncodingHelper belongs to the generate path, so this test does not need it.
        return new TrickplayManager(
            NullLogger<TrickplayManager>.Instance,
            Mock.Of<IMediaEncoder>(),
            Mock.Of<IFileSystem>(),
            null!,
            config.Object,
            Mock.Of<IImageEncoder>(),
            CreateDbContextFactory(),
            ApplicationPaths,
            pathManager.Object);
    }

    /// <summary>
    /// Records the SQL statements that EF Core sends to the database.
    /// </summary>
    private sealed class SqlCommandRecorder : DbCommandInterceptor
    {
        private readonly List<string> _statements = [];

        public IReadOnlyList<string> Statements => _statements;

        public IEnumerable<string> Deletes
            => _statements.Where(s => s.StartsWith("DELETE", StringComparison.OrdinalIgnoreCase));

        public void Clear() => _statements.Clear();

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            _statements.Add(command.CommandText);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            _statements.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            _statements.Add(command.CommandText);
            return base.NonQueryExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            _statements.Add(command.CommandText);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
        {
            _statements.Add(command.CommandText);
            return base.ScalarExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
        {
            _statements.Add(command.CommandText);
            return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
