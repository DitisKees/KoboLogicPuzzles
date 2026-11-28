using System.Globalization;
using System.Text.Json;
using KoboLogicPuzzles.Server.LinkAPix;
using Microsoft.Data.Sqlite;

namespace KoboLogicPuzzles.Server;

public sealed partial class PuzzleStore
{
    internal bool SaveLinkAPix(LinkAPixDefinition puzzle)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT definition FROM link_a_pix_puzzles WHERE id = $id";
        command.Parameters.AddWithValue("$id", puzzle.Id);
        var existing = command.ExecuteScalar() is string json ? ReadLinkAPix(json) : null;
        if (existing is not null)
        {
            puzzle = existing with
            {
                Metadata = existing.Metadata with
                {
                    Attributions = existing.Metadata.Attributions.Concat(puzzle.Metadata.Attributions)
                        .DistinctBy(attribution => (attribution.Source, attribution.SourceId)).ToArray(),
                },
            };
        }

        command.CommandText = """
            INSERT INTO link_a_pix_puzzles (id, width, height, difficulty, solvability, metadata, definition)
            VALUES ($id, $width, $height, $difficulty, $logic, $metadata, $definition)
            ON CONFLICT(id) DO UPDATE SET metadata = excluded.metadata, definition = excluded.definition
            """;
        command.Parameters.AddWithValue("$width", puzzle.Width);
        command.Parameters.AddWithValue("$height", puzzle.Height);
        command.Parameters.AddWithValue("$difficulty", puzzle.Metadata.Difficulty);
        command.Parameters.AddWithValue("$logic", puzzle.Metadata.Solvability);
        command.Parameters.AddWithValue("$metadata", JsonSerializer.Serialize(puzzle.Metadata, Json));
        command.Parameters.AddWithValue("$definition", JsonSerializer.Serialize(puzzle, Json));
        command.ExecuteNonQuery();
        transaction.Commit();
        return existing is null;
    }

    private static LinkAPixDefinition ReadLinkAPix(string value) => JsonSerializer.Deserialize<LinkAPixDefinition>(value, Json)!;

    internal LinkAPixDefinition? FindLinkAPix(string id, bool unstartedOnly = false)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT definition FROM link_a_pix_puzzles WHERE id = $id AND
                ($unstarted = 0 OR NOT EXISTS (SELECT 1 FROM link_a_pix_progress WHERE puzzle_id = $id))
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$unstarted", unstartedOnly);
        return command.ExecuteScalar() is string value ? ReadLinkAPix(value) : null;
    }

    internal IReadOnlyList<GamePuzzleSummary> ListLinkAPixCatalog(PuzzleFilter filter, PuzzleId? before, int limit, bool unstartedOnly)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT p.id, p.width, p.height, p.metadata, s.completed
            FROM link_a_pix_puzzles p LEFT JOIN link_a_pix_progress s ON s.puzzle_id = p.id
            WHERE ($before IS NULL OR (p.catalog_rank, p.id) < (coalesce(
                (SELECT catalog_rank FROM link_a_pix_puzzles WHERE id = $before), 3), $before))
                AND ($unstarted = 0 OR s.puzzle_id IS NULL)
                AND ($square = 0 OR p.width = p.height)
                AND ($width IS NULL OR p.width IN (SELECT value FROM json_each($width)))
                AND ($height IS NULL OR p.height IN (SELECT value FROM json_each($height)))
                AND ($difficulty IS NULL OR p.difficulty IN (SELECT value FROM json_each($difficulty)))
                AND ($logic IS NULL OR p.solvability IN (SELECT value FROM json_each($logic)))
                AND ($source IS NULL OR EXISTS (SELECT 1 FROM json_each(p.metadata, '$.attributions') AS a
                    WHERE json_extract(a.value, '$.source') IN (SELECT value FROM json_each($source))))
            ORDER BY p.catalog_rank DESC, p.id DESC LIMIT $limit
            """;
        command.Parameters.AddWithValue("$before", (object?)before?.Value ?? DBNull.Value);
        command.Parameters.AddWithValue("$unstarted", unstartedOnly);
        command.Parameters.AddWithValue("$square", filter.SquareOnly);
        command.Parameters.AddWithValue("$width", FilterValues(filter.Widths));
        command.Parameters.AddWithValue("$height", FilterValues(filter.Heights));
        command.Parameters.AddWithValue("$difficulty", FilterValues(filter.LinkDifficulties));
        command.Parameters.AddWithValue("$logic", FilterValues(filter.LinkSolvabilities));
        command.Parameters.AddWithValue("$source", FilterValues(filter.Sources));
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var result = new List<GamePuzzleSummary>();
        while (reader.Read())
        {
            var id = reader.GetString(0);
            var metadata = JsonSerializer.Deserialize<LinkMetadata>(reader.GetString(3), Json)!;
            result.Add(new(id, LinkSources.Game, reader.GetInt32(1), reader.GetInt32(2),
                GamePuzzleSummary.PlayStatus(reader.IsDBNull(4) ? null : reader.GetBoolean(4)),
                metadata, $"Link-a-Pix {id[^6..]}", metadata.Attributions[0].Author));
        }
        return result;
    }

    internal IReadOnlyDictionary<string, object> ListLinkAPixMetadata(IReadOnlyList<PuzzleId> ids)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT p.id, p.metadata FROM json_each($ids) AS requested
            JOIN link_a_pix_puzzles p ON p.id = requested.value
            """;
        command.Parameters.AddWithValue("$ids", JsonSerializer.Serialize(ids.Select(id => id.Value), Json));
        using var reader = command.ExecuteReader();
        var result = new Dictionary<string, object>(StringComparer.Ordinal);
        while (reader.Read())
            result.Add(reader.GetString(0), JsonSerializer.Deserialize<LinkMetadata>(reader.GetString(1), Json)!);
        return result;
    }

    private static void RestoreLinkAPixProgress(SqliteConnection connection, SqliteTransaction transaction, List<GameSavedBoard> boards)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT p.definition, s.marks, s.completed, s.backed_up_at
            FROM link_a_pix_progress s JOIN link_a_pix_puzzles p ON p.id = s.puzzle_id ORDER BY p.id
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
            boards.Add(ToSavedBoard(ReadLinkAPix(reader.GetString(0)).WithoutAnswer(), reader.GetString(1), reader.GetBoolean(2),
                DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture)));
    }

    internal (int Total, int Unstarted) CountLinkAPix()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT count(*), count(*) - count(s.puzzle_id)
            FROM link_a_pix_puzzles p LEFT JOIN link_a_pix_progress s ON s.puzzle_id = p.id
            """;
        using var reader = command.ExecuteReader();
        reader.Read();
        return (reader.GetInt32(0), reader.GetInt32(1));
    }

    internal LinkRefreshState GetLinkRefresh(string source)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT cursor, checked_at, rejected, inconclusive FROM link_a_pix_refresh WHERE source = $source";
        command.Parameters.AddWithValue("$source", source);
        using var reader = command.ExecuteReader();
        return reader.Read() ? new(reader.GetString(0), reader.GetInt64(1), reader.GetInt32(2), reader.GetInt32(3)) : new("", 0, 0, 0);
    }

    internal void SaveLinkRefresh(string source, LinkRefreshState state)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO link_a_pix_refresh VALUES ($source, $cursor, $time, $rejected, $inconclusive)
            ON CONFLICT(source) DO UPDATE SET cursor = excluded.cursor, checked_at = excluded.checked_at,
                rejected = excluded.rejected, inconclusive = excluded.inconclusive
            """;
        command.Parameters.AddWithValue("$source", source);
        command.Parameters.AddWithValue("$cursor", state.Cursor);
        command.Parameters.AddWithValue("$time", state.CheckedAt);
        command.Parameters.AddWithValue("$rejected", state.Rejected);
        command.Parameters.AddWithValue("$inconclusive", state.Inconclusive);
        command.ExecuteNonQuery();
    }

    internal FileStream? TryLockLinkRefresh(string source)
    {
        try
        {
            return File.Open(Path.GetFullPath(databasePath) + $".link-a-pix-{source}.lock",
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException exception) when ((exception.HResult & 0xffff) is 11 or 32 or 33)
        {
            return null;
        }
    }
}

internal sealed class LinkAPixPuzzleProvider(PuzzleStore store) : IPuzzleProvider
{
    public PuzzleGame Game => PuzzleGame.LinkAPix;

    public ValueTask<GamePuzzleDefinition?> GetDefinitionAsync(PuzzleId id, bool includeSolution, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var puzzle = store.FindLinkAPix(id.Value);
        return ValueTask.FromResult<GamePuzzleDefinition?>(includeSolution ? puzzle : puzzle?.WithoutAnswer());
    }

    public ValueTask<GamePuzzleDefinition?> GetOfflineDefinitionAsync(PuzzleId id, CancellationToken cancellationToken) =>
        GetDefinitionAsync(id, false, cancellationToken);

    public IReadOnlyList<GamePuzzleSummary> ListCatalog(PuzzleFilter filter, PuzzleId? before, int limit, bool unstartedOnly) =>
        store.ListLinkAPixCatalog(filter, before, limit, unstartedOnly);

    public bool IsMatchingUnstartedCache(PuzzleId id, PuzzleFilter filter) =>
        store.FindLinkAPix(id.Value, unstartedOnly: true) is { } puzzle && filter.Matches(puzzle);

    public IReadOnlyDictionary<string, object> ListMetadata(IReadOnlyList<PuzzleId> ids) => store.ListLinkAPixMetadata(ids);
}
