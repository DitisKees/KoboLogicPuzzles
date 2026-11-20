# Shared data contracts

This directory is for the versioned JSON formats that the home server and clients exchange. Add schemas and example payloads here as the puzzle import and progress features are implemented.

- A puzzle definition uses the WebPBN puzzle ID and describes the board dimensions and row and column clues.
- A progress record uses the same ID and carries the cell marks and completion state.

Keep these formats independent of the server’s SQLite schema and the clients’ UI state. The Kobo, a future standalone client, and future Android and web apps can then use the same puzzle definitions and saves.
