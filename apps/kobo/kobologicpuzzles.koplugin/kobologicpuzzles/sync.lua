local Puzzle = require("kobologicpuzzles/puzzle")

local Sync = {}

function Sync.restore(store, request)
    local supportedGames = table.concat(Puzzle.games, ",")
    if store.settings.restoredV2Server == store.settings.server
        and store.settings.restoredV2Games == supportedGames then return 0 end
    local saved, err = request("GET", "/restore")
    if not saved then return nil, err end
    local records, seen = {}, {}
    for _, entry in ipairs(saved) do
        local wire = type(entry) == "table" and entry.puzzle
        if type(wire) ~= "table" or type(wire.game) ~= "string" or wire.game == "" then
            return nil, "The server returned a malformed backup."
        end
        if Puzzle.gameNames[wire.game] then
            local puzzle = Puzzle.fromWire(wire)
            local progress = puzzle and Puzzle.decodeProgress(puzzle, entry.progress)
            if not progress or seen[puzzle.id] then
                return nil, "The server returned a malformed backup."
            end
            seen[puzzle.id] = true
            records[#records + 1] = { puzzle = puzzle, progress = progress }
        end
    end
    local restored, latest = 0, store.settings.lastBackup
    for _, entry in ipairs(records) do
        local previous = store.records[entry.puzzle.id]
        local ok, restoreError = store:restore(entry.puzzle, entry.progress)
        if not ok then return nil, restoreError end
        if not previous or not previous.marks then restored = restored + 1 end
        local timestamp = entry.progress.backedUpAt
        if type(timestamp) == "string" and (type(latest) ~= "string" or timestamp > latest) then
            latest = timestamp
        end
    end
    local ok, saveError = store:updateSettings({ restoredV2Server = store.settings.server,
        restoredV2Games = supportedGames, lastBackup = latest })
    if not ok then return nil, saveError end
    return restored
end

local function topUp(store, request, game, result, advancedFilters)
    local target = store:target(game)
    if target == 0 then
        result.shortfalls[game] = 0
        return true
    end
    local filters = store.settings.filters[game]
    local parameters, advanced = Puzzle.filterParameters(filters)
    if advanced and not advancedFilters then return nil, Puzzle.advancedFilterMessage end

    local cached, excluded, matching = {}, {}, 0
    local records = store:list(game)
    if game == "nonogram" and (filters.difficulty or filters.solvability) then
        -- WebPBN ratings can change between downloads.
        local unstarted = {}
        for _, record in ipairs(records) do
            if not record.marks then unstarted[#unstarted + 1] = record end
        end
        local ok, err = Sync.refreshMetadata(store, request, unstarted)
        if not ok then return nil, err end
        records = store:list(game)
    end
    local matches = Puzzle.filterMatcher(game, filters)
    for _, record in ipairs(records) do
        local puzzle = record.puzzle
        if not record.marks and matches(puzzle) then
            matching = matching + 1
            if #cached < 100 then cached[#cached + 1] = Puzzle.wireId(puzzle.id) end
        elseif #excluded < 1000 then
            excluded[#excluded + 1] = Puzzle.wireId(puzzle.id)
        end
    end

    while matching < target do
        local payload = { game = game, targetCount = math.min(target, matching + 15),
            cachedUnstartedPuzzleIds = cached, excludedPuzzleIds = excluded }
        for key, value in pairs(parameters) do payload[key] = value end
        local reply, err = request("POST", "/offline-queue", payload, "Downloading " .. Puzzle.gameNames[game] .. " puzzles…")
        if not reply then return nil, err end
        if type(reply.puzzles) ~= "table" or type(reply.remaining) ~= "number" or reply.remaining < 0
            or reply.remaining ~= math.floor(reply.remaining) or #reply.puzzles + reply.remaining > payload.targetCount then
            return nil, "The server returned an invalid offline queue."
        end

        local entries, seen = {}, {}
        for _, wire in ipairs(reply.puzzles) do
            local puzzle = Puzzle.fromWire(wire)
            if not puzzle or Puzzle.game(puzzle.id) ~= game or seen[puzzle.id] or not matches(puzzle) then
                return nil, "The server returned an invalid puzzle for this queue."
            end
            seen[puzzle.id] = true
            entries[#entries + 1] = puzzle
        end

        local added = 0
        for _, puzzle in ipairs(entries) do
            if matching + added < target and not store.records[puzzle.id] then
                local ok, downloadError = store:download(puzzle)
                if not ok then return nil, downloadError end
                added = added + 1
                result.added = result.added + 1
                cached[#cached + 1] = Puzzle.wireId(puzzle.id)
            end
        end
        matching = matching + added
        if added == 0 or reply.remaining > 0 then break end
    end
    result.shortfalls[game] = math.max(0, target - matching)
    return true
end

function Sync.run(store, request, games)
    local result = { restored = 0, backedUp = 0, added = 0, pending = 0, shortfalls = {} }
    local restored, restoreError = Sync.restore(store, request)
    if not restored then return result, restoreError end
    result.restored = restored

    local dirty = {}
    for _, record in ipairs(store:list()) do
        if record.marks and record.dirty then
            if games[Puzzle.game(record.puzzle.id)] then
                dirty[#dirty + 1] = { puzzleId = record.puzzle.id, marks = record.marks, completed = record.completed }
            else
                result.pending = result.pending + 1
            end
        end
    end

    for first = 1, #dirty, 100 do
        local batch, encoded = {}, {}
        for i = first, math.min(first + 99, #dirty) do
            batch[#batch + 1] = dirty[i]
            encoded[#encoded + 1] = Puzzle.encodeProgress(dirty[i])
        end
        local receipt, err = request("POST", "/progress", { schemaVersion = 1, boards = encoded })
        if not receipt then return result, err end
        if receipt.savedCount ~= #batch or type(receipt.backedUpAt) ~= "string" then
            return result, "The server did not acknowledge the progress backup."
        end
        local ok, saveError = store:updateSettings({ lastBackup = receipt.backedUpAt })
        if not ok then return result, saveError end
        result.backedUp = result.backedUp + #batch
        for _, upload in ipairs(batch) do
            ok, saveError = store:acknowledge(upload)
            if not ok then return result, saveError end
        end
    end

    local queueError
    for _, game in ipairs(Puzzle.games) do
        if games[game] then
            local _, err = topUp(store, request, game, result, games.advancedFilters)
            if err and not queueError then queueError = err end
        end
    end
    return result, queueError
end

function Sync.refreshMetadata(store, request, records)
    records = records or store:list()
    for first = 1, #records, 100 do
        local ids, query = {}, {}
        for index = first, math.min(first + 99, #records) do
            local id = records[index].puzzle.id
            ids[#ids + 1] = id
            query[#query + 1] = "id=" .. Puzzle.wireId(id)
        end
        local reply, err = request("GET", "/puzzles/metadata?" .. table.concat(query, "&"),
            nil, "Updating puzzle ratings…")
        if not reply then return nil, err end
        for _, id in ipairs(ids) do
            local metadata = reply[Puzzle.wireId(id)]
            if type(metadata) == "table" then
                local ok, saveError = store:saveMetadata(id, metadata)
                if not ok then return nil, saveError end
            end
        end
    end
    return true
end

return Sync
