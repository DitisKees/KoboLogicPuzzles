namespace KoboLogicPuzzles.Server;

public sealed record PublishedPuzzle(string Source, string SourceName, string SourceId, string SourceUrl,
    string License, string Title, string Author, string SourceKind = "collection");
