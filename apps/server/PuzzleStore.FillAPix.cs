using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace KoboLogicPuzzles.Server;

public sealed partial class PuzzleStore
{
    private const string FillAPixColumns = "p.id, p.width, p.height, p.title, p.author, p.metadata";

    internal bool HasFillAPixPuzzles()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM fill_a_pix_puzzles WHERE active = 1 OR definition IS NOT NULL LIMIT 1";
        return command.ExecuteScalar() is not null;
    }

    internal CatalogRefreshState GetFillAPixRefreshState(FillAPixSource source)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT revision, completed_at FROM fill_a_pix_refresh WHERE source = $source";
        command.Parameters.AddWithValue("$source", source.Slug());
        using var reader = command.ExecuteReader();
        return reader.Read() ? new(reader.GetInt64(0), reader.IsDBNull(1) ? null :
            DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture)) : new(0, null);
    }

    internal int? ReplaceFillAPixCatalog(FillAPixSource source, IReadOnlyList<FillAPixCatalogEntry> entries,
        long revision, DateTimeOffset completedAt, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO fill_a_pix_refresh (source, revision, completed_at)
            SELECT $source, 1, $completedAt WHERE $revision = 0 OR EXISTS (
                SELECT 1 FROM fill_a_pix_refresh WHERE source = $source AND revision = $revision)
            ON CONFLICT(source) DO UPDATE SET revision = revision + 1, completed_at = excluded.completed_at
                WHERE revision = $revision
            """;
        command.Parameters.AddWithValue("$source", source.Slug());
        command.Parameters.AddWithValue("$revision", revision);
        command.Parameters.AddWithValue("$completedAt", completedAt.ToString("O", CultureInfo.InvariantCulture));
        if (command.ExecuteNonQuery() == 0)
            return null;

        command.Parameters.Clear();
        command.Parameters.AddWithValue("$source", source.Slug());
        command.CommandText = "UPDATE fill_a_pix_puzzles SET active = 0 WHERE source = $source AND active = 1";
        command.ExecuteNonQuery();
        WriteFillAPixEntries(command, source, entries, revision + 1, token);
        token.ThrowIfCancellationRequested();
        transaction.Commit();
        return entries.Count;
    }

    internal int? GetGrandGamesNextPage(long revision)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT next_page FROM fill_a_pix_refresh WHERE source = 'grandgames' AND revision = $revision";
        command.Parameters.AddWithValue("$revision", revision);
        return command.ExecuteScalar() is long page ? checked((int)page) : null;
    }

    internal int? SaveGrandGamesCatalogPage(IReadOnlyList<FillAPixCatalogEntry> entries, bool hasMore,
        int expectedPage, long revision, DateTimeOffset completedAt, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE fill_a_pix_refresh SET next_page = $nextPage,
                revision = CASE WHEN $more THEN revision ELSE revision + 1 END,
                completed_at = CASE WHEN $more THEN completed_at ELSE $completedAt END
            WHERE source = 'grandgames' AND next_page = $expectedPage AND revision = $revision
            """;
        command.Parameters.AddWithValue("$expectedPage", expectedPage);
        command.Parameters.AddWithValue("$nextPage", hasMore ? checked(expectedPage + 1) : 1);
        command.Parameters.AddWithValue("$revision", revision);
        command.Parameters.AddWithValue("$more", hasMore);
        command.Parameters.AddWithValue("$completedAt", completedAt.ToString("O", CultureInfo.InvariantCulture));
        if (command.ExecuteNonQuery() == 0)
            return null;

        command.Parameters.Clear();
        command.CommandText = """
            SELECT 1 FROM json_each($ids) requested WHERE NOT EXISTS (
                SELECT 1 FROM fill_a_pix_puzzles WHERE id = requested.value AND seen_revision = $seenRevision
            ) LIMIT 1
            """;
        command.Parameters.AddWithValue("$ids", JsonSerializer.Serialize(entries.Select(entry => entry.Id.Value), Json));
        command.Parameters.AddWithValue("$seenRevision", revision + 1);
        if (command.ExecuteScalar() is null)
            throw new InvalidDataException($"GrandGames repeated Mosaic results on page {expectedPage}.");

        WriteFillAPixEntries(command, FillAPixSource.GrandGames, entries, revision + 1, token);
        int? count = null;
        if (!hasMore)
        {
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$revision", revision + 1);
            command.CommandText = "UPDATE fill_a_pix_puzzles SET active = 0 WHERE source = 'grandgames' AND active = 1 AND seen_revision <> $revision";
            command.ExecuteNonQuery();
            command.Parameters.Clear();
            command.CommandText = "SELECT count(*) FROM fill_a_pix_puzzles WHERE source = 'grandgames' AND active = 1";
            count = checked((int)(long)command.ExecuteScalar()!);
        }
        token.ThrowIfCancellationRequested();
        transaction.Commit();
        return count;
    }

    private static void WriteFillAPixEntries(SqliteCommand command, FillAPixSource source, IReadOnlyList<FillAPixCatalogEntry> entries,
        long revision, CancellationToken token)
    {
        command.Parameters.Clear();
        command.Parameters.AddWithValue("$source", source.Slug());
        command.Parameters.AddWithValue("$seenRevision", revision);
        command.CommandText = """
            INSERT INTO fill_a_pix_puzzles (id, source, width, height, title, author, metadata, definition, active, seen_revision)
            VALUES ($id, $source, $width, $height, $title, $author, $metadata, $definition, 1, $seenRevision)
            ON CONFLICT(id) DO UPDATE SET active = 1, title = excluded.title, author = excluded.author,
                metadata = excluded.metadata, seen_revision = excluded.seen_revision,
                width = CASE WHEN definition IS NULL THEN excluded.width ELSE width END,
                height = CASE WHEN definition IS NULL THEN excluded.height ELSE height END,
                definition = COALESCE(definition, excluded.definition)
            """;
        var id = command.Parameters.Add("$id", SqliteType.Text);
        var width = command.Parameters.Add("$width", SqliteType.Integer);
        var height = command.Parameters.Add("$height", SqliteType.Integer);
        var title = command.Parameters.Add("$title", SqliteType.Text);
        var author = command.Parameters.Add("$author", SqliteType.Text);
        var metadata = command.Parameters.Add("$metadata", SqliteType.Text);
        var definition = command.Parameters.Add("$definition", SqliteType.Text);
        foreach (var entry in entries)
        {
            token.ThrowIfCancellationRequested();
            id.Value = entry.Id.Value;
            width.Value = entry.Width;
            height.Value = entry.Height;
            title.Value = entry.Title;
            author.Value = (object?)entry.Author ?? DBNull.Value;
            metadata.Value = JsonSerializer.Serialize(entry.Metadata, Json);
            definition.Value = entry.Definition is { } puzzle ? JsonSerializer.Serialize(puzzle, Json) : DBNull.Value;
            command.ExecuteNonQuery();
        }
    }

    internal Dictionary<PuzzleId, FillAPixCatalogEntry> ListCachedFillAPix(FillAPixSource source)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {FillAPixColumns} FROM fill_a_pix_puzzles p WHERE source = $source AND definition IS NOT NULL";
        command.Parameters.AddWithValue("$source", source.Slug());
        using var reader = command.ExecuteReader();
        var entries = new Dictionary<PuzzleId, FillAPixCatalogEntry>();
        while (reader.Read())
        {
            var entry = ReadFillAPixEntry(reader);
            entries.Add(entry.Id, entry);
        }
        return entries;
    }

    internal (FillAPixCatalogEntry Entry, bool Active)? FindFillAPix(PuzzleId id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {FillAPixColumns}, p.definition, p.active FROM fill_a_pix_puzzles p WHERE p.id = $id";
        command.Parameters.AddWithValue("$id", id.Value);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;
        var entry = ReadFillAPixEntry(reader);
        return (reader.IsDBNull(6) ? entry : entry with { Definition = ReadFillAPixDefinition(reader, entry) }, reader.GetBoolean(7));
    }

    internal FillAPixPuzzleDefinition? CacheFillAPix(PuzzleId id, FillAPixPuzzleDefinition definition)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE fill_a_pix_puzzles SET definition = $definition
            WHERE id = $id AND definition IS NULL AND width = $width AND height = $height
            """;
        command.Parameters.AddWithValue("$id", id.Value);
        command.Parameters.AddWithValue("$width", definition.Width);
        command.Parameters.AddWithValue("$height", definition.Height);
        command.Parameters.AddWithValue("$definition", JsonSerializer.Serialize(definition, Json));
        command.ExecuteNonQuery();
        return FindFillAPix(id)?.Entry.Definition;
    }

    internal IReadOnlyList<GamePuzzleSummary> ListFillAPixCatalog(PuzzleFilter filter, PuzzleId? before, int limit,
        bool unstartedOnly, PuzzleId? exactId = null)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {FillAPixColumns}, s.completed
            FROM fill_a_pix_puzzles p LEFT JOIN fill_a_pix_progress s ON p.id = s.puzzle_id
            WHERE p.active = 1 AND ($before IS NULL OR p.id < $before) AND ($id IS NULL OR p.id = $id)
                AND ($unstarted = 0 OR s.puzzle_id IS NULL)
                AND ($width IS NULL OR EXISTS (SELECT 1 FROM json_each($width) WHERE p.width BETWEEN value ->> 0 AND value ->> 1))
                AND ($height IS NULL OR EXISTS (SELECT 1 FROM json_each($height) WHERE p.height BETWEEN value ->> 0 AND value ->> 1))
            ORDER BY p.id DESC LIMIT $limit
            """;
        command.Parameters.AddWithValue("$before", (object?)before?.Value ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", (object?)exactId?.Value ?? DBNull.Value);
        command.Parameters.AddWithValue("$unstarted", unstartedOnly);
        command.Parameters.AddWithValue("$width", FilterRanges(filter.FillAPixWidths));
        command.Parameters.AddWithValue("$height", FilterRanges(filter.FillAPixHeights));
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var summaries = new List<GamePuzzleSummary>();
        while (reader.Read())
        {
            var entry = ReadFillAPixEntry(reader);
            summaries.Add(new(entry.Id.Value, FillAPixSources.Game, entry.Width, entry.Height,
                GamePuzzleSummary.PlayStatus(reader.IsDBNull(6) ? null : reader.GetBoolean(6)), entry.Metadata, entry.Title, entry.Author));
        }
        return summaries;
    }

    private static object FilterRanges(PuzzleFilter.DimensionRange[]? ranges) => ranges is null ? DBNull.Value :
        JsonSerializer.Serialize(ranges.Select(range => new[] { range.Min, range.Max }), Json);

    internal IReadOnlyDictionary<string, object> ListFillAPixMetadata(IReadOnlyList<PuzzleId> ids)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT p.id, p.metadata FROM json_each($ids) AS requested
            JOIN fill_a_pix_puzzles p ON p.id = requested.value
            WHERE p.active = 1 OR p.definition IS NOT NULL
            """;
        command.Parameters.AddWithValue("$ids", JsonSerializer.Serialize(ids.Select(id => id.Value), Json));
        using var reader = command.ExecuteReader();
        var metadata = new Dictionary<string, object>(StringComparer.Ordinal);
        while (reader.Read())
            metadata.Add(reader.GetString(0), JsonSerializer.Deserialize<FillAPixMetadata>(reader.GetString(1), Json)!);
        return metadata;
    }

    private static FillAPixCatalogEntry ReadFillAPixEntry(SqliteDataReader reader)
    {
        if (!PuzzleId.TryParse(reader.GetString(0), out var id) || id.Game != PuzzleGame.FillAPix)
            throw new InvalidDataException("The Fill-a-Pix catalog contains an invalid puzzle ID.");
        return new(id, reader.GetInt32(1), reader.GetInt32(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4),
            JsonSerializer.Deserialize<FillAPixMetadata>(reader.GetString(5), Json)!);
    }

    private static FillAPixPuzzleDefinition ReadFillAPixDefinition(SqliteDataReader reader, FillAPixCatalogEntry entry) =>
        JsonSerializer.Deserialize<FillAPixPuzzleDefinition>(reader.GetString(6), Json)! with
        { Title = entry.Title, Author = entry.Author, Metadata = entry.Metadata };

    private static void WriteFillAPixProgress(SqliteConnection connection, SqliteTransaction transaction,
        IReadOnlyList<FillAPixProgressUpdate> boards, DateTimeOffset backedUpAt)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO fill_a_pix_progress (puzzle_id, marks, completed, backed_up_at)
            VALUES ($id, $marks, $completed, $backedUpAt)
            ON CONFLICT(puzzle_id) DO UPDATE SET marks = excluded.marks, completed = excluded.completed, backed_up_at = excluded.backed_up_at
            """;
        var id = command.Parameters.Add("$id", SqliteType.Text);
        var marks = command.Parameters.Add("$marks", SqliteType.Text);
        var completed = command.Parameters.Add("$completed", SqliteType.Integer);
        command.Parameters.AddWithValue("$backedUpAt", backedUpAt.ToString("O", CultureInfo.InvariantCulture));
        foreach (var board in boards)
        {
            id.Value = board.Id.Value;
            marks.Value = board.Marks;
            completed.Value = board.Completed;
            command.ExecuteNonQuery();
        }
    }

    private static void RestoreFillAPixProgress(SqliteConnection connection, SqliteTransaction transaction, List<GameSavedBoard> boards)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT {FillAPixColumns}, p.definition, s.marks, s.completed, s.backed_up_at
            FROM fill_a_pix_progress s JOIN fill_a_pix_puzzles p ON p.id = s.puzzle_id ORDER BY p.id
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var definition = ReadFillAPixDefinition(reader, ReadFillAPixEntry(reader));
            boards.Add(ToSavedBoard(definition, reader.GetString(7), reader.GetBoolean(8),
                DateTimeOffset.Parse(reader.GetString(9), CultureInfo.InvariantCulture)));
        }
    }
}
