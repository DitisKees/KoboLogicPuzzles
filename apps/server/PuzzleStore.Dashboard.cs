namespace KoboLogicPuzzles.Server;

internal sealed record DashboardCatalogCount(string Game, string Source, string SourceName, int Total, int Unstarted);

public sealed partial class PuzzleStore
{
    internal IReadOnlyList<DashboardCatalogCount> ReadDashboardCatalog()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 'nonogram', 'webpbn', 'WebPBN', count(*), count(*) - count(s.puzzle_id)
                FROM catalog p LEFT JOIN progress s ON s.puzzle_id = p.id
            UNION ALL
            SELECT 'hashi', 'bridges', 'Bridges', count(*), count(*) - count(s.content_hash)
                FROM hashi_puzzles p LEFT JOIN hashi_progress s USING (content_hash)
            UNION ALL
            SELECT 'fill-a-pix', p.source,
                CASE p.source WHEN 'janko' THEN 'Janko' WHEN 'grandgames' THEN 'GrandGames'
                    WHEN 'fillapixnet' THEN 'Fill-a-Pix.net' ELSE p.source END,
                count(*), count(*) - count(s.puzzle_id)
                FROM fill_a_pix_puzzles p LEFT JOIN fill_a_pix_progress s ON s.puzzle_id = p.id
                WHERE p.active = 1 GROUP BY p.source
            UNION ALL
            SELECT 'slitherlink', p.source, min(p.source_name), count(*), count(*) - count(s.puzzle_id)
                FROM slitherlink_puzzles p LEFT JOIN slitherlink_progress s ON s.puzzle_id = p.id
                GROUP BY p.source
            ORDER BY 1, 3
            """;
        using var reader = command.ExecuteReader();
        var catalog = new List<DashboardCatalogCount>();
        while (reader.Read())
            catalog.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3), reader.GetInt32(4)));
        return catalog;
    }
}
