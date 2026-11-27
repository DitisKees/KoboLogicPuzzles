using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace KoboLogicPuzzles.Server.Placement;

public sealed record PlacementMetadata(string Source, string SourceName, string SourceKind, string SourceId,
    string? SourceUrl, string? License, string? Seed, string? GeneratorVersion, string Difficulty, string Solvability,
    string ContentHash, string? OriginalRating = null, string? LicenseUrl = null)
{
    public bool Unique => true;
}

public abstract record PlacementDefinition(string Id, int Width, int Height, int[][] RowClues, int[][] ColumnClues,
    string Givens, string Title, string? Author, PlacementMetadata Metadata,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Solution)
    : GamePuzzleDefinition(Id, Width, Height)
{
    internal abstract PlacementBoard ToBoard();
}

public sealed record TentsDefinition(string Id, int Width, int Height, int[][] RowClues, int[][] ColumnClues,
    string Givens, string Title, string? Author, PlacementMetadata Metadata, string? Solution)
    : PlacementDefinition(Id, Width, Height, RowClues, ColumnClues, Givens, Title, Author, Metadata, Solution)
{
    internal override PlacementBoard ToBoard() => new(PuzzleGame.Tents, Width, Height,
        RowClues.Select(row => row.Single()).ToArray(), ColumnClues.Select(column => column.Single()).ToArray(), Givens, []);
}

public sealed record BattleshipsDefinition(string Id, int Width, int Height, int[][] RowClues, int[][] ColumnClues,
    string Givens, int[] Fleet, string Title, string? Author, PlacementMetadata Metadata, string? Solution)
    : PlacementDefinition(Id, Width, Height, RowClues, ColumnClues, Givens, Title, Author, Metadata, Solution)
{
    internal override PlacementBoard ToBoard() => new(PuzzleGame.Battleships, Width, Height,
        RowClues.Select(row => row.Single()).ToArray(), ColumnClues.Select(column => column.Single()).ToArray(), Givens, Fleet);
}

internal sealed class PlacementBoard
{
    internal const int MaxDimension = 50;
    internal static readonly PuzzleGame[] Games = [PuzzleGame.Tents, PuzzleGame.Battleships];
    internal static readonly string[] Difficulties = ["easy", "medium", "hard", "expert"];
    internal static readonly string[] Solvabilities = ["basic_logic", "advanced_logic", "bounded_lookahead", "requires_search", "unknown"];
    internal static string Slug(PuzzleGame game) => game == PuzzleGame.Tents ? "tents" : "battleships";
    internal static string Name(PuzzleGame game) => game == PuzzleGame.Tents ? "Tents and Trees" : "Battleships";

    internal PlacementBoard(PuzzleGame game, int width, int height, int[] rows, int[] columns, string givens, int[] fleet)
    {
        if (!Games.Contains(game) || width is < 1 or > MaxDimension || height is < 1 or > MaxDimension ||
            givens.Length != width * height || rows.Length != height || columns.Length != width ||
            rows.Any(n => n < 0 || n > width) || columns.Any(n => n < 0 || n > height) ||
            rows.Sum() == 0 || rows.Sum() != columns.Sum())
            throw new InvalidDataException("Invalid placement board dimensions or totals.");

        Game = game;
        Width = width;
        Height = height;
        Rows = rows;
        Columns = columns;
        Givens = givens;
        Fleet = fleet.Reverse().SkipWhile(n => n == 0).Reverse().ToArray();
        if (game == PuzzleGame.Tents)
        {
            if (fleet.Length != 0 || givens.Any(c => c is not ('.' or 't' or 'x' or '#')) ||
                givens.Count(c => c == 't') != rows.Sum())
                throw new InvalidDataException("The tree count must match the tent totals.");
        }
        else if (Fleet.Length == 0 || Fleet.Length > Math.Max(width, height) ||
            Fleet.Any(n => n < 0 || n > givens.Length) ||
            Fleet.Select((n, i) => n * (i + 1)).Sum() != rows.Sum() ||
            givens.Any(c => !".x#onsemw".Contains(c)))
            throw new InvalidDataException("Invalid Battleships fleet or givens.");

        Sides = Enumerable.Range(0, Area).Select(i => Neighbors(i, false)).ToArray();
        Adjacent = Enumerable.Range(0, Area).Select(i => Neighbors(i, true)).ToArray();
        Trees = Enumerable.Range(0, Area).Where(i => Givens[i] == 't').ToArray();
    }

    internal PuzzleGame Game { get; }
    internal int Width { get; }
    internal int Height { get; }
    internal int Area => Width * Height;
    internal int[] Rows { get; }
    internal int[] Columns { get; }
    internal string Givens { get; }
    internal int[] Fleet { get; }
    internal int[] Trees { get; }
    internal int[][] Sides { get; }
    internal int[][] Adjacent { get; }

    internal static char FixedMark(char clue) => clue switch { '.' => '.', 't' => '.', 'x' => 'x', _ => '#' };

    internal bool ValidMarks(string marks) => marks.Length == Area && marks.All(c => c is '.' or '#' or 'x') &&
        Enumerable.Range(0, Area).All(i => Givens[i] == '.' || marks[i] == FixedMark(Givens[i]));

    internal bool IsSolved(string marks)
    {
        if (!ValidMarks(marks))
            return false;
        var rows = new int[Height];
        var columns = new int[Width];
        for (var i = 0; i < Area; i++)
        {
            if (marks[i] != '#')
                continue;
            rows[i / Width]++;
            columns[i % Width]++;
            if (Adjacent[i].Any(next => marks[next] == '#' &&
                (Game == PuzzleGame.Tents || next / Width != i / Width && next % Width != i % Width)))
                return false;
        }
        return rows.SequenceEqual(Rows) && columns.SequenceEqual(Columns) &&
            (Game == PuzzleGame.Tents ? HasMatching(i => marks[i] == '#') : HasFleet(marks));
    }

    internal bool HasMatching(Func<int, bool> allowed)
    {
        var owners = new int[Area];
        Array.Fill(owners, -1);
        var seen = new int[Area];
        foreach (var tree in Trees)
            if (!Match(tree, tree + 1))
                return false;
        return true;

        bool Match(int tree, int stamp)
        {
            foreach (var cell in Sides[tree])
            {
                if (seen[cell] == stamp || !allowed(cell))
                    continue;
                seen[cell] = stamp;
                if (owners[cell] >= 0 && !Match(owners[cell], stamp))
                    continue;
                owners[cell] = tree;
                return true;
            }
            return false;
        }
    }

    private bool HasFleet(string marks)
    {
        var remaining = (int[])Fleet.Clone();
        var seen = new bool[Area];
        for (var i = 0; i < Area; i++)
        {
            if (seen[i] || marks[i] != '#')
                continue;
            var ship = new List<int> { i };
            seen[i] = true;
            for (var j = 0; j < ship.Count; j++)
                foreach (var next in Sides[ship[j]])
                    if (!seen[next] && marks[next] == '#')
                    {
                        seen[next] = true;
                        ship.Add(next);
                    }

            ship.Sort();
            var horizontal = ship.All(cell => cell / Width == i / Width);
            if (!horizontal && ship.Any(cell => cell % Width != i % Width) ||
                ship.Count > remaining.Length || --remaining[ship.Count - 1] < 0)
                return false;
            for (var j = 0; j < ship.Count; j++)
                if (Givens[ship[j]] is not ('.' or '#') && Givens[ship[j]] != Segment(j, ship.Count, horizontal))
                    return false;
        }
        return remaining.All(n => n == 0);
    }

    internal static char Segment(int offset, int length, bool horizontal) => length == 1 ? 'o' :
        offset == 0 ? horizontal ? 'w' : 'n' : offset == length - 1 ? horizontal ? 'e' : 's' : 'm';

    private int[] Neighbors(int index, bool diagonal)
    {
        var row = index / Width;
        var col = index % Width;
        var result = new List<int>(diagonal ? 8 : 4);
        for (var y = Math.Max(0, row - 1); y <= Math.Min(Height - 1, row + 1); y++)
            for (var x = Math.Max(0, col - 1); x <= Math.Min(Width - 1, col + 1); x++)
                if ((x != col || y != row) && (diagonal || x == col || y == row))
                    result.Add(y * Width + x);
        return result.ToArray();
    }

    internal static (int[] Rows, int[] Columns) Totals(int width, int height, string solution)
    {
        var rows = new int[height];
        var columns = new int[width];
        for (var i = 0; i < solution.Length; i++)
            if (solution[i] == '#')
            {
                rows[i / width]++;
                columns[i % width]++;
            }
        return (rows, columns);
    }

    internal string Hash()
    {
        var canonical = FormattableString.Invariant($"1|{Slug(Game)}|{Width}|{Height}|{Givens}|{Join(Rows)}|{Join(Columns)}|{Join(Fleet)}");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(canonical)));

        static string Join(int[] values) => string.Join(',', values.Select(n => n.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }

    internal PlacementDefinition ToDefinition(PlacementMetadata metadata, string? title, string? author, string solution)
    {
        var id = PuzzleId.FromPlacement(Game, metadata.ContentHash).Value;
        title ??= $"{Name(Game)} {id[^6..]}";
        var rows = Rows.Select(n => new[] { n }).ToArray();
        var columns = Columns.Select(n => new[] { n }).ToArray();
        return Game == PuzzleGame.Tents ? new TentsDefinition(id, Width, Height, rows, columns, Givens, title, author, metadata, solution) :
            new BattleshipsDefinition(id, Width, Height, rows, columns, Givens, Fleet, title, author, metadata, solution);
    }
}
