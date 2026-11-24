using System.Globalization;
using System.Text.Json;
using KoboNonogram.Server.Hashi;
using Microsoft.Data.Sqlite;

namespace KoboNonogram.Server;

public sealed partial class PuzzleStore
{
    private static void InitializeHashiSchema(SqliteCommand command)
    {
        command.CommandText = """
            CREATE TABLE hashi_puzzles (
                content_hash TEXT NOT NULL PRIMARY KEY CHECK (length(content_hash) = 64),
                width INTEGER NOT NULL CHECK (width > 0),
                height INTEGER NOT NULL CHECK (height > 0),
                islands TEXT NOT NULL,
                solution BLOB NOT NULL,
                metadata TEXT NOT NULL
            );
            CREATE TABLE hashi_progress (
                content_hash TEXT NOT NULL PRIMARY KEY REFERENCES hashi_puzzles(content_hash) ON DELETE RESTRICT,
                marks TEXT NOT NULL,
                completed INTEGER NOT NULL CHECK (completed IN (0, 1)),
                backed_up_at TEXT NOT NULL
            );
            PRAGMA user_version = 1;
            """;
        command.ExecuteNonQuery();
    }

    public bool SaveHashiPuzzle(VerifiedHashiPuzzle verified)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO hashi_puzzles (content_hash, width, height, islands, solution, metadata)
            VALUES ($hash, $width, $height, $islands, $solution, $metadata)
            ON CONFLICT(content_hash) DO NOTHING
            """;
        command.Parameters.AddWithValue("$hash", verified.Puzzle.ContentHash);
        command.Parameters.AddWithValue("$width", verified.Puzzle.Width);
        command.Parameters.AddWithValue("$height", verified.Puzzle.Height);
        command.Parameters.AddWithValue("$islands", JsonSerializer.Serialize(verified.Puzzle.Islands, Json));
        command.Parameters.AddWithValue("$solution", verified.Solution.ToArray());
        command.Parameters.AddWithValue("$metadata", JsonSerializer.Serialize(verified.Metadata, Json));
        return command.ExecuteNonQuery() == 1;
    }

    internal bool HasHashiPuzzles()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM hashi_puzzles LIMIT 1";
        return command.ExecuteScalar() is not null;
    }

    internal IReadOnlyList<HashiStockCount> ReadHashiStock(int minIslands, int minDensity)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT p.width, json_extract(p.metadata, '$.rating.difficulty') AS difficulty,
                json_extract(p.metadata, '$.rating.solvability') AS solvability,
                count(*) AS total, sum(s.content_hash IS NULL) AS unstarted,
                sum(s.content_hash IS NULL AND json_extract(p.metadata, '$.islandCount') >= $minIslands
                    AND json_extract(p.metadata, '$.actualDensity') >= $minDensity) AS eligible
            FROM hashi_puzzles p LEFT JOIN hashi_progress s USING (content_hash)
            GROUP BY p.width, difficulty, solvability ORDER BY p.width, difficulty, solvability
            """;
        command.Parameters.AddWithValue("$minIslands", minIslands);
        command.Parameters.AddWithValue("$minDensity", minDensity);
        using var reader = command.ExecuteReader();
        var counts = new List<HashiStockCount>();
        while (reader.Read())
            counts.Add(new HashiStockCount(reader.GetInt32(0), reader.IsDBNull(1) ? null : (HashiDifficulty)reader.GetInt32(1),
                (HashiSolvability)reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4), reader.GetInt32(5)));
        return counts;
    }

    public StoredHashiPuzzle? FindHashiPuzzle(string contentHash)
    {
        using var connection = Open();
        return FindHashiPuzzle(connection, null, contentHash);
    }

    internal StoredHashiPuzzle? FindUnstartedHashi(string contentHash)
    {
        using var connection = Open();
        return FindHashiPuzzle(connection, null, contentHash, unstartedOnly: true);
    }

    public void SaveHashiProgress(IReadOnlyList<HashiProgressUpdate> boards, DateTimeOffset backedUpAt)
    {
        if (boards.Count > 100)
            throw new ArgumentException("Supply at most 100 changed boards.", nameof(boards));
        if (boards.Count == 0)
            return;

        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        WriteHashiProgress(connection, transaction, boards, backedUpAt);
        transaction.Commit();
    }

    private static void WriteHashiProgress(SqliteConnection connection, SqliteTransaction transaction,
        IReadOnlyList<HashiProgressUpdate> boards, DateTimeOffset backedUpAt)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO hashi_progress (content_hash, marks, completed, backed_up_at)
            VALUES ($hash, $marks, $completed, $backedUpAt)
            ON CONFLICT(content_hash) DO UPDATE SET marks = excluded.marks,
                completed = excluded.completed, backed_up_at = excluded.backed_up_at
            """;
        var hash = command.Parameters.Add("$hash", SqliteType.Text);
        var marks = command.Parameters.Add("$marks", SqliteType.Text);
        var completed = command.Parameters.Add("$completed", SqliteType.Integer);
        command.Parameters.AddWithValue("$backedUpAt", backedUpAt.ToString("O", CultureInfo.InvariantCulture));

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var board in boards)
        {
            if (board is null || !PuzzleId.IsContentHash(board.ContentHash) || !seen.Add(board.ContentHash))
                throw new ArgumentException("Each board must have a valid, unique Hashi content hash.", nameof(boards));
            var puzzle = FindHashiPuzzle(connection, transaction, board.ContentHash)?.Puzzle
                ?? throw new ArgumentException($"Hashi puzzle {board.ContentHash} is unavailable.", nameof(boards));
            var validatedMarks = ValidateHashiProgress(puzzle, board);
            hash.Value = board.ContentHash;
            marks.Value = JsonSerializer.Serialize(validatedMarks, Json);
            completed.Value = board.Completed;
            command.ExecuteNonQuery();
        }

    }

    public IReadOnlyList<SavedHashiBoard> RestoreHashiProgress()
    {
        using var connection = Open();
        return ReadHashiProgress(connection, null);
    }

    private static IReadOnlyList<SavedHashiBoard> ReadHashiProgress(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT p.content_hash, p.width, p.height, p.islands, p.solution, p.metadata, s.marks, s.completed, s.backed_up_at
            FROM hashi_progress s JOIN hashi_puzzles p USING (content_hash) ORDER BY p.content_hash
            """;
        using var reader = command.ExecuteReader();
        var boards = new List<SavedHashiBoard>();
        while (reader.Read())
        {
            var puzzle = ReadHashiPuzzle(reader);
            var marks = JsonSerializer.Deserialize<HashiEdgeMark[]>(reader.GetString(6), Json)
                ?? throw new InvalidOperationException("Missing saved Hashi marks.");
            boards.Add(new SavedHashiBoard(puzzle, new SavedHashiProgress(puzzle.Puzzle.ContentHash,
                Array.AsReadOnly(marks), reader.GetBoolean(7),
                DateTimeOffset.ParseExact(reader.GetString(8), "O", CultureInfo.InvariantCulture))));
        }
        return boards;
    }

    private static StoredHashiPuzzle? FindHashiPuzzle(SqliteConnection connection, SqliteTransaction? transaction,
        string contentHash, bool unstartedOnly = false)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT content_hash, width, height, islands, solution, metadata FROM hashi_puzzles
            WHERE content_hash = $hash AND ($unstarted = 0 OR NOT EXISTS (SELECT 1 FROM hashi_progress WHERE content_hash = $hash))
            """;
        command.Parameters.AddWithValue("$hash", contentHash);
        command.Parameters.AddWithValue("$unstarted", unstartedOnly);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadHashiPuzzle(reader) : null;
    }

    private static StoredHashiPuzzle ReadHashiPuzzle(SqliteDataReader reader)
    {
        var islands = JsonSerializer.Deserialize<HashiIsland[]>(reader.GetString(3), Json)
            ?? throw new InvalidOperationException("Missing cached Hashi islands.");
        var puzzle = HashiPuzzle.Create(reader.GetInt32(1), reader.GetInt32(2), islands);
        var solution = (byte[])reader.GetValue(4);
        if (puzzle.ContentHash != reader.GetString(0) || !puzzle.IsSolution(solution))
            throw new InvalidOperationException("The stored Hashi definition or solution is invalid.");
        return new StoredHashiPuzzle(puzzle, Array.AsReadOnly(solution), ReadHashiMetadata(reader.GetString(5)));
    }

    private static HashiPuzzleMetadata ReadHashiMetadata(string json) =>
        JsonSerializer.Deserialize<HashiPuzzleMetadata>(json, Json)
        ?? throw new InvalidOperationException("Missing stored Hashi metadata.");

    private static HashiEdgeMark[] ValidateHashiProgress(HashiPuzzle puzzle, HashiProgressUpdate board)
    {
        if (board.Marks is null || board.Marks.Count > puzzle.Edges.Count)
            throw new ArgumentException("Supply at most one mark per Hashi edge.");
        var marks = board.Marks.ToArray();
        var seen = new bool[puzzle.Edges.Count];
        var counts = new byte[puzzle.Edges.Count];
        foreach (var mark in marks)
        {
            if (mark.Edge < 0 || mark.Edge >= counts.Length || seen[mark.Edge] || mark.Count is < 0 or > 2 ||
                mark.NoBridge != (mark.Count == 0))
                throw new ArgumentException("Each Hashi mark must specify a known, unique edge and a bridge count or no-bridge mark.");
            seen[mark.Edge] = true;
            counts[mark.Edge] = (byte)mark.Count;
        }

        foreach (var mark in marks)
            if (mark.Count > 0 && puzzle.Crossings[mark.Edge].Any(edge => counts[edge] > 0))
                throw new ArgumentException("Hashi bridges cannot cross.");
        if (board.Completed && !puzzle.IsSolution(counts))
            throw new ArgumentException("Completed Hashi boards must contain a valid connected solution.");

        Array.Sort(marks, (first, second) => first.Edge.CompareTo(second.Edge));
        return marks;
    }
}
