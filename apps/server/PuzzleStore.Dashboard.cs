using KoboLogicPuzzles.Server.Slitherlink;

namespace KoboLogicPuzzles.Server;

internal sealed record DashboardCatalogCount(string Game, string Source, string SourceName, int Total, int Unstarted, string SourceKind);

public sealed partial class PuzzleStore
{
    internal IReadOnlyList<DashboardCatalogCount> ReadDashboardCatalog()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 'nonogram', 'webpbn', 'WebPBN', count(*), count(*) - count(s.puzzle_id), 'collection'
                FROM catalog p LEFT JOIN progress s ON s.puzzle_id = p.id
            UNION ALL
            SELECT 'nonogram', p.source, min(json_extract(p.definition, '$.metadata.sourceName')),
                count(*), count(*) - count(s.puzzle_id), 'collection'
                FROM nonogram_collection_puzzles p LEFT JOIN nonogram_collection_progress s ON s.puzzle_id = p.id
                GROUP BY p.source
            UNION ALL
            SELECT 'hashi', coalesce(json_extract(p.metadata, '$.attribution.source'), 'bridges'),
                coalesce(min(json_extract(p.metadata, '$.attribution.sourceName')), 'Bridges'),
                count(*), count(*) - count(s.content_hash),
                coalesce(json_extract(p.metadata, '$.attribution.sourceKind'),
                    CASE WHEN json_extract(p.metadata, '$.attribution') IS NULL THEN 'generated' ELSE 'collection' END)
                FROM hashi_puzzles p LEFT JOIN hashi_progress s USING (content_hash)
                GROUP BY json_extract(p.metadata, '$.attribution.source')
            UNION ALL
            SELECT 'fill-a-pix', p.source,
                CASE p.source WHEN 'janko' THEN 'Janko' WHEN 'grandgames' THEN 'GrandGames'
                    WHEN 'fillapixnet' THEN 'Fill-a-Pix.net' ELSE p.source END,
                count(*), count(*) - count(s.puzzle_id), 'collection'
                FROM fill_a_pix_puzzles p LEFT JOIN fill_a_pix_progress s ON s.puzzle_id = p.id
                WHERE p.active = 1 GROUP BY p.source
            UNION ALL
            SELECT 'slitherlink', p.source, min(p.source_name), count(*), count(*) - count(s.puzzle_id),
                CASE WHEN p.source = $loopy THEN 'generated' ELSE 'collection' END
                FROM slitherlink_puzzles p LEFT JOIN slitherlink_progress s ON s.puzzle_id = p.id
                GROUP BY p.source
            UNION ALL
            SELECT 'nurikabe', p.source, min(p.source_name), count(*), count(*) - count(s.puzzle_id), p.source_kind
                FROM nurikabe_puzzles p LEFT JOIN nurikabe_progress s ON s.puzzle_id = p.id
                GROUP BY p.source, p.source_kind
            UNION ALL
            SELECT 'galaxies', p.source, min(p.source_name), count(*), count(*) - count(s.puzzle_id), p.source_kind
                FROM galaxies_puzzles p LEFT JOIN galaxies_progress s ON s.puzzle_id = p.id
                GROUP BY p.source, p.source_kind
            UNION ALL
            SELECT p.game, p.source, min(p.source_name), count(*), count(*) - count(s.puzzle_id), p.source_kind
                FROM placement_puzzles p LEFT JOIN placement_progress s ON s.puzzle_id = p.id
                GROUP BY p.game, p.source, p.source_kind
            UNION ALL
            SELECT 'link-a-pix', json_extract(a.value, '$.source'), min(json_extract(a.value, '$.sourceName')),
                count(DISTINCT p.id), count(DISTINCT CASE WHEN s.puzzle_id IS NULL THEN p.id END), 'collection'
                FROM link_a_pix_puzzles p LEFT JOIN link_a_pix_progress s ON s.puzzle_id = p.id,
                    json_each(p.metadata, '$.attributions') AS a
                GROUP BY json_extract(a.value, '$.source')
            ORDER BY 1, 3
            """;
        command.Parameters.AddWithValue("$loopy", LoopyGenerator.Source);
        using var reader = command.ExecuteReader();
        var catalog = new List<DashboardCatalogCount>();
        while (reader.Read())
            catalog.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3), reader.GetInt32(4), reader.GetString(5)));
        return catalog;
    }
}
