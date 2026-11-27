using System.Text.Json;

namespace KoboLogicPuzzles.Server;

public sealed partial class PuzzleStore
{
    internal IReadOnlyList<GamePuzzleSummary> ListNonogramCatalog(PuzzleFilter filter, int? before, int limit, bool unstartedOnly)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.id, c.title, c.author, c.width, c.height, m.metadata,
                json_extract(p.definition, '$.metadata'), s.completed
            FROM catalog c
            LEFT JOIN puzzle_metadata m ON m.id = c.id
            LEFT JOIN puzzles p ON p.id = c.id
            LEFT JOIN progress s ON s.puzzle_id = c.id
            WHERE ($before IS NULL OR c.id < $before)
                AND ($width IS NULL OR c.width IN (SELECT value FROM json_each($width)))
                AND ($height IS NULL OR c.height IN (SELECT value FROM json_each($height)))
                AND ($unstarted = 0 OR s.puzzle_id IS NULL)
                AND ($difficulty IS NULL OR CAST(CASE WHEN m.metadata IS NULL
                    THEN json_extract(p.definition, '$.metadata.difficulty') ELSE json_extract(m.metadata, '$.difficulty') END AS REAL)
                    * 4 IN (SELECT value FROM json_each($difficulty)))
                AND ($solvability IS NULL OR coalesce(json_extract(m.metadata, '$.solvability'),
                    json_extract(p.definition, '$.metadata.solvability')) IN (SELECT value FROM json_each($solvability)))
            ORDER BY c.id DESC LIMIT $limit
            """;
        command.Parameters.AddWithValue("$before", (object?)before ?? DBNull.Value);
        command.Parameters.AddWithValue("$width", FilterValues(filter.Widths));
        command.Parameters.AddWithValue("$height", FilterValues(filter.Heights));
        command.Parameters.AddWithValue("$unstarted", unstartedOnly);
        command.Parameters.AddWithValue("$difficulty", FilterValues(filter.Difficulties));
        command.Parameters.AddWithValue("$solvability", FilterValues(filter.Solvabilities));
        command.Parameters.AddWithValue("$limit", limit);

        using var reader = command.ExecuteReader();
        var summaries = new List<GamePuzzleSummary>();
        while (reader.Read())
        {
            var metadata = MergeMetadata(ReadMetadata(reader, 5), ReadMetadata(reader, 6));
            summaries.Add(new GamePuzzleSummary(PuzzleId.FromWebPbn(reader.GetInt32(0)).Value, "nonogram",
                reader.GetInt32(3), reader.GetInt32(4), GamePuzzleSummary.PlayStatus(reader.IsDBNull(7) ? null : reader.GetBoolean(7)),
                metadata, reader.GetString(1), reader.GetString(2)));
        }
        return summaries;
    }

    internal PuzzleDefinition? FindUnstartedNonogram(int id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT definition, metadata FROM puzzles LEFT JOIN puzzle_metadata USING (id)
            WHERE id = $id AND NOT EXISTS (SELECT 1 FROM progress WHERE puzzle_id = $id)
            """;
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadDefinition(reader, 0, 1) : null;
    }

    internal IReadOnlyList<GamePuzzleSummary> ListHashiCatalog(PuzzleFilter filter, string? before, int limit, bool unstartedOnly)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT p.content_hash, p.width, p.height, p.metadata, s.completed
            FROM hashi_puzzles p LEFT JOIN hashi_progress s USING (content_hash)
            WHERE ($before IS NULL OR p.content_hash < $before)
                AND ($square = 0 OR p.width = p.height)
                AND ($width IS NULL OR p.width IN (SELECT value FROM json_each($width)))
                AND ($height IS NULL OR p.height IN (SELECT value FROM json_each($height)))
                AND ($unstarted = 0 OR s.content_hash IS NULL)
                AND ($source IS NULL OR coalesce(json_extract(p.metadata, '$.attribution.source'), 'bridges') IN (SELECT value FROM json_each($source)))
                AND ($difficulty IS NULL OR json_extract(p.metadata, '$.rating.difficulty') IN (SELECT value FROM json_each($difficulty)))
                AND ($solvability IS NULL OR json_extract(p.metadata, '$.rating.solvability') IN (SELECT value FROM json_each($solvability)))
            ORDER BY p.content_hash DESC LIMIT $limit
            """;
        command.Parameters.AddWithValue("$before", (object?)before ?? DBNull.Value);
        command.Parameters.AddWithValue("$width", FilterValues(filter.Widths));
        command.Parameters.AddWithValue("$height", FilterValues(filter.Heights));
        command.Parameters.AddWithValue("$unstarted", unstartedOnly);
        command.Parameters.AddWithValue("$square", filter.SquareOnly);
        command.Parameters.AddWithValue("$source", FilterValues(filter.Sources));
        command.Parameters.AddWithValue("$difficulty", FilterValues(filter.Difficulties));
        command.Parameters.AddWithValue("$solvability", FilterValues(filter.Solvabilities));
        command.Parameters.AddWithValue("$limit", limit);

        using var reader = command.ExecuteReader();
        var summaries = new List<GamePuzzleSummary>();
        while (reader.Read())
        {
            var stored = ReadHashiMetadata(reader.GetString(3));
            summaries.Add(new GamePuzzleSummary(PuzzleId.FromHashi(reader.GetString(0)).Value, "hashi",
                reader.GetInt32(1), reader.GetInt32(2), GamePuzzleSummary.PlayStatus(reader.IsDBNull(4) ? null : reader.GetBoolean(4)),
                LocalHashiPuzzleProvider.ToMetadata(stored), stored.Attribution?.Title, stored.Attribution?.Author));
        }
        return summaries;
    }

    private static object FilterValues<T>(T[]? values) => values is null ? DBNull.Value : JsonSerializer.Serialize(values, Json);

    internal IReadOnlyDictionary<string, object> ListHashiMetadata(string[] hashes)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT p.content_hash, p.metadata FROM json_each($hashes) AS requested
            JOIN hashi_puzzles p ON p.content_hash = requested.value
            """;
        command.Parameters.AddWithValue("$hashes", JsonSerializer.Serialize(hashes, Json));
        using var reader = command.ExecuteReader();
        var result = new Dictionary<string, object>(StringComparer.Ordinal);
        while (reader.Read())
            result.Add(PuzzleId.FromHashi(reader.GetString(0)).Value,
                LocalHashiPuzzleProvider.ToMetadata(ReadHashiMetadata(reader.GetString(1))));
        return result;
    }
}
