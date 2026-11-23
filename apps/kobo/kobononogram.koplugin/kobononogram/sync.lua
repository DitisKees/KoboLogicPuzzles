local Board = require("kobononogram/board")

local Sync = {}

function Sync.restore(store, request)
    if store.settings.restoredServer == store.settings.server then return 0 end
    local saved, err = request("GET", "/restore")
    if not saved then return nil, err end
    for _, entry in ipairs(saved) do
        local puzzle = type(entry) == "table" and entry.puzzle
        local progress = type(entry) == "table" and entry.progress
        if not Board.validPuzzle(puzzle) or type(progress) ~= "table" or progress.puzzleId ~= puzzle.id
            or type(progress.completed) ~= "boolean" or not Board.validMarks(puzzle, progress.marks) then
            return nil, "The server returned a malformed backup."
        end
    end
    local restored, latest = 0, store.settings.lastBackup
    for _, entry in ipairs(saved) do
        local previous = store.records[entry.puzzle.id]
        local ok, restoreError = store:restore(entry.puzzle, entry.progress)
        if not ok then return nil, restoreError end
        if not previous or not previous.marks then restored = restored + 1 end
        local timestamp = entry.progress.backedUpAt
        if type(timestamp) == "string" and (type(latest) ~= "string" or timestamp > latest) then
            latest = timestamp
        end
    end
    local ok, saveError = store:updateSettings({ restoredServer = store.settings.server, lastBackup = latest })
    if not ok then return nil, saveError end
    return restored
end

function Sync.run(store, request)
    local result = { restored = 0, backedUp = 0, added = 0 }
    local restored, restoreError = Sync.restore(store, request)
    if not restored then return result, restoreError end
    result.restored = restored

    local dirty, unstarted = {}, {}
    for _, record in ipairs(store:list()) do
        if not record.marks then
            unstarted[#unstarted + 1] = record.puzzle.id
        elseif record.dirty then
            dirty[#dirty + 1] = { puzzleId = record.puzzle.id, marks = record.marks, completed = record.completed }
        end
    end

    for first = 1, #dirty, 100 do
        local batch = {}
        for i = first, math.min(first + 99, #dirty) do batch[#batch + 1] = dirty[i] end
        local receipt, err = request("POST", "/progress", { boards = batch })
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

    while #unstarted < store.settings.target do
        local needed = math.min(store.settings.target - #unstarted, 15)
        local reply, err = request("POST", "/offline-queue", {
            targetCount = #unstarted + needed, cachedUnstartedPuzzleIds = unstarted,
        })
        if not reply then return result, err end
        if type(reply.puzzles) ~= "table" or #reply.puzzles > needed then
            return result, "The server returned an invalid offline queue."
        end
        local downloaded = 0
        for _, puzzle in ipairs(reply.puzzles) do
            local existing = type(puzzle) == "table" and store.records[puzzle.id]
            local ok, downloadError = store:download(puzzle)
            if not ok then return result, downloadError end
            if not existing then
                downloaded = downloaded + 1
                result.added = result.added + 1
                unstarted[#unstarted + 1] = puzzle.id
            end
        end
        if downloaded < needed then break end
    end
    return result
end

return Sync
