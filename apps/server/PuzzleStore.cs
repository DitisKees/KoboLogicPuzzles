using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace KoboNonogram.Server;

public sealed class PuzzleStore(string databasePath)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = Path.GetFullPath(databasePath),
        ForeignKeys = true,
    }.ToString();

    public void Initialize()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS catalog (
                id INTEGER PRIMARY KEY,
                title TEXT NOT NULL,
                author TEXT NOT NULL,
                width INTEGER NOT NULL,
                height INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS puzzles (
                id INTEGER PRIMARY KEY,
                definition TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS progress (
                puzzle_id INTEGER PRIMARY KEY REFERENCES puzzles(id),
                marks TEXT NOT NULL,
                completed INTEGER NOT NULL,
                backed_up_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS catalog_refresh (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                revision INTEGER NOT NULL,
                completed_at TEXT
            );
            INSERT OR IGNORE INTO catalog_refresh (id, revision) VALUES (1, 0);
            """;
        command.ExecuteNonQuery();
    }

    public CatalogRefreshState GetCatalogRefreshState()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT revision, completed_at FROM catalog_refresh WHERE id = 1";
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            throw new InvalidOperationException("The catalog refresh state is missing.");

        return new CatalogRefreshState(reader.GetInt64(0), reader.IsDBNull(1) ? null :
            DateTimeOffset.ParseExact(reader.GetString(1), "O", CultureInfo.InvariantCulture));
    }

    public bool TryReplaceCatalog(IEnumerable<PuzzleSummary> puzzles, long expectedRevision, DateTimeOffset completedAt)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE catalog_refresh SET revision = revision + 1, completed_at = $completedAt
            WHERE id = 1 AND revision = $expectedRevision
            """;
        command.Parameters.AddWithValue("$completedAt", completedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$expectedRevision", expectedRevision);
        if (command.ExecuteNonQuery() != 1)
            return false;

        command.Parameters.Clear();
        command.CommandText = "DELETE FROM catalog";
        command.ExecuteNonQuery();

        command.CommandText = """
            INSERT INTO catalog (id, title, author, width, height) VALUES ($id, $title, $author, $width, $height)
            """;
        var id = command.Parameters.Add("$id", SqliteType.Integer);
        var title = command.Parameters.Add("$title", SqliteType.Text);
        var author = command.Parameters.Add("$author", SqliteType.Text);
        var width = command.Parameters.Add("$width", SqliteType.Integer);
        var height = command.Parameters.Add("$height", SqliteType.Integer);
        foreach (var puzzle in puzzles)
        {
            id.Value = puzzle.Id;
            title.Value = puzzle.Title;
            author.Value = puzzle.Author;
            width.Value = puzzle.Width;
            height.Value = puzzle.Height;
            command.ExecuteNonQuery();
        }

        transaction.Commit();
        return true;
    }

    public IReadOnlyList<PuzzleSummary> ListCatalog(int? beforeId, int limit)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, title, author, width, height FROM catalog
            WHERE $before IS NULL OR id < $before ORDER BY id DESC LIMIT $limit
            """;
        command.Parameters.AddWithValue("$before", beforeId.HasValue ? beforeId.Value : DBNull.Value);
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var puzzles = new List<PuzzleSummary>();
        while (reader.Read())
        {
            puzzles.Add(new PuzzleSummary(reader.GetInt32(0), reader.GetString(1), reader.GetString(2),
                reader.GetInt32(3), reader.GetInt32(4)));
        }

        return puzzles;
    }

    public bool IsInCatalog(int id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM catalog WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() is not null;
    }

    public IReadOnlyList<int> ListUnstartedIds()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id FROM catalog WHERE NOT EXISTS (SELECT 1 FROM progress WHERE puzzle_id = catalog.id)
            ORDER BY id DESC
            """;
        using var reader = command.ExecuteReader();
        var ids = new List<int>();
        while (reader.Read())
            ids.Add(reader.GetInt32(0));

        return ids;
    }

    public PuzzleDefinition? FindPuzzle(int id)
    {
        using var connection = Open();
        return FindPuzzle(connection, id);
    }

    public PuzzleDefinition CachePuzzle(PuzzleDefinition puzzle)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO puzzles (id, definition) VALUES ($id, $definition) ON CONFLICT(id) DO NOTHING
            """;
        command.Parameters.AddWithValue("$id", puzzle.Id);
        command.Parameters.AddWithValue("$definition", JsonSerializer.Serialize(puzzle, Json));
        return command.ExecuteNonQuery() == 1 ? puzzle : FindPuzzle(connection, puzzle.Id)!;
    }

    public PuzzleDefinition CacheSolution(PuzzleDefinition puzzle, string solution)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE puzzles SET definition = json_set(definition, '$.solution', $solution)
            WHERE id = $id AND json_extract(definition, '$.solution') IS NULL
            """;
        command.Parameters.AddWithValue("$id", puzzle.Id);
        command.Parameters.AddWithValue("$solution", solution);
        return command.ExecuteNonQuery() == 1 ? puzzle with { Solution = solution } : FindPuzzle(connection, puzzle.Id)!;
    }

    public void SaveProgress(IEnumerable<ProgressUpdate> boards, DateTimeOffset backedUpAt)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO progress (puzzle_id, marks, completed, backed_up_at)
            VALUES ($id, $marks, $completed, $backedUpAt)
            ON CONFLICT(puzzle_id) DO UPDATE SET marks = excluded.marks,
                completed = excluded.completed, backed_up_at = excluded.backed_up_at
            """;
        var id = command.Parameters.Add("$id", SqliteType.Integer);
        var marks = command.Parameters.Add("$marks", SqliteType.Text);
        var completed = command.Parameters.Add("$completed", SqliteType.Integer);
        command.Parameters.AddWithValue("$backedUpAt", backedUpAt.ToString("O", CultureInfo.InvariantCulture));
        foreach (var board in boards)
        {
            id.Value = board.PuzzleId;
            marks.Value = board.Marks;
            completed.Value = board.Completed;
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public IReadOnlyList<SavedBoard> RestoreProgress()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT definition, puzzle_id, marks, completed, backed_up_at
            FROM progress JOIN puzzles ON puzzles.id = progress.puzzle_id ORDER BY puzzle_id
            """;
        using var reader = command.ExecuteReader();
        var boards = new List<SavedBoard>();
        while (reader.Read())
        {
            boards.Add(new SavedBoard(ReadDefinition(reader.GetString(0)), new SavedProgress(
                reader.GetInt32(1), reader.GetString(2), reader.GetBoolean(3),
                DateTimeOffset.ParseExact(reader.GetString(4), "O", CultureInfo.InvariantCulture))));
        }

        return boards;
    }

    private static PuzzleDefinition? FindPuzzle(SqliteConnection connection, int id)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT definition FROM puzzles WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() is string definition ? ReadDefinition(definition) : null;
    }

    private static PuzzleDefinition ReadDefinition(string json) =>
        JsonSerializer.Deserialize<PuzzleDefinition>(json, Json) ?? throw new InvalidOperationException("Missing cached definition.");

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(connectionString);
        try
        {
            connection.Open();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }
}
