local Puzzle = require("kobologicpuzzles/puzzle")
local Viewport = require("kobologicpuzzles/viewport")
local JSON = require("json")
local ffiutil = require("ffi/util")
local lfs = require("libs/libkoreader-lfs")
local logger = require("logger")

local Store = {}
Store.__index = Store
Store.targetKeys = { nonogram = "target", hashi = "hashiTarget", ["fill-a-pix"] = "fillAPixTarget" }

local function copy(record)
    local result = {}
    for key, value in pairs(record) do result[key] = value end
    return result
end

local function read(path)
    local file = io.open(path, "rb")
    if not file then return nil end
    local contents = file:read("*all")
    file:close()
    local ok, record = pcall(JSON.decode, contents)
    if ok and type(record) == "table" and record.format == 1 then return record end
end

local function write(path, record)
    local ok, contents = pcall(JSON.encode, record)
    if not ok then return nil, contents end
    local temporary = path .. ".tmp"
    local file, err = io.open(temporary, "wb")
    if not file then return nil, err end
    ok, err = file:write(contents)
    if ok then ok, err = file:flush() end
    if ok then ok, err = ffiutil.fsyncOpenedFile(file) end
    local closed, closeError = file:close()
    if ok and closed then
        ok, err = os.rename(temporary, path)
        if ok then
            ffiutil.fsyncDirectory(path)
            return true
        end
    end
    os.remove(temporary)
    return nil, err or closeError or "Unable to save local data."
end

function Store.open(root)
    if lfs.attributes(root, "mode") ~= "directory" then
        local ok, err = lfs.mkdir(root)
        if not ok then return nil, err end
    end
    local settings = read(root .. "/settings.json") or { format = 1 }
    if type(settings.server) ~= "string" then settings.server = "" end
    for game, key in pairs(Store.targetKeys) do
        local target = settings[key]
        if type(target) ~= "number" or target < 0 or target > 100 then target = game == "nonogram" and 10 or 5 end
        settings[key] = math.floor(target)
    end
    if not Puzzle.gameNames[settings.lastGame] then settings.lastGame = "nonogram" end
    settings.hashiGuides = settings.hashiGuides ~= false
    if type(settings.filters) ~= "table" then settings.filters = {} end
    for _, game in ipairs(Puzzle.games) do settings.filters[game] = Puzzle.filters(game, settings.filters[game]) end
    local clueLimit = settings.clueLimit
    if type(clueLimit) ~= "number" or clueLimit < 1 or clueLimit > Viewport.maxClueLimit
        or clueLimit ~= math.floor(clueLimit) then
        settings.clueLimit = Viewport.defaultClueLimit
    end
    local self = setmetatable({ root = root, records = {}, settings = settings }, Store)
    for name in lfs.dir(root) do
        local id = Puzzle.fileId(name)
        if id then
            local record = read(root .. "/" .. name)
            local rules = Puzzle.rules(id)
            if record and Puzzle.valid(record.puzzle) and record.puzzle.id == id
                and (record.marks == nil or (rules.validMarks(record.puzzle, record.marks, record.completed)
                    and type(record.completed) == "boolean")) then
                if record.checkpoint and not rules.validMarks(record.puzzle, record.checkpoint) then record.checkpoint = nil end
                self.records[id] = record
            else
                logger.warn("Kobo Logic Puzzles: cannot load local puzzle", id)
                self.skipped = (self.skipped or 0) + 1
            end
        end
    end
    return self
end

function Store:updateSettings(patch)
    local settings = copy(self.settings)
    local changed = false
    for key, value in pairs(patch) do
        if settings[key] ~= value then
            settings[key] = value
            changed = true
        end
    end
    if not changed then return true end
    local ok, err = write(self.root .. "/settings.json", settings)
    if ok then self.settings = settings end
    return ok, err
end

function Store:save(record)
    local id = record.puzzle.id
    if not Puzzle.isId(id) then return nil, "Invalid local puzzle ID." end
    local ok, err = write(self.root .. "/" .. id .. ".json", record)
    if ok then self.records[record.puzzle.id] = record end
    return ok, err
end

function Store:download(puzzle)
    if not Puzzle.valid(puzzle) then return nil, "The server returned an invalid puzzle definition." end
    if self.records[puzzle.id] then return true end
    return self:save({ format = 1, puzzle = puzzle })
end

function Store:restore(puzzle, progress)
    local existing = self.records[puzzle.id]
    if existing and existing.marks then return true end
    local record = existing and copy(existing) or { format = 1 }
    record.puzzle, record.marks, record.completed = puzzle, progress.marks, progress.completed
    if record.checkpoint and not Puzzle.rules(puzzle.id).validMarks(puzzle, record.checkpoint) then record.checkpoint = nil end
    return self:save(record)
end

function Store:saveCheckpoint(board)
    local record = copy(self.records[board.puzzle.id])
    record.checkpoint = board:marks()
    return self:save(record)
end

function Store:saveSolution(id, solution)
    local record = copy(self.records[id])
    record.puzzle = copy(record.puzzle)
    record.puzzle.solution = solution
    return self:save(record)
end

function Store:saveMetadata(id, metadata)
    local current = self.records[id]
    local previous = current.puzzle.metadata
    if type(previous) == "table" then
        local same = true
        for key, value in pairs(previous) do
            if metadata[key] ~= value then same = false break end
        end
        if same then
            for key, value in pairs(metadata) do
                if previous[key] ~= value then same = false break end
            end
        end
        if same then return true end
    end
    local record = copy(current)
    record.puzzle = copy(record.puzzle)
    record.puzzle.metadata = metadata
    return self:save(record)
end

function Store:saveGame(board, view, changed)
    local current = self.records[board.puzzle.id]
    local record = copy(current)
    record.row, record.column, record.zoom = view.row, view.column, view.zoom
    if changed then
        local marks, completed = board:marks(), board:isComplete()
        if current.marks ~= marks or current.completed ~= completed then
            record.marks, record.completed, record.dirty = marks, completed, true
        end
    end
    if record.marks == current.marks and record.completed == current.completed and record.row == current.row
        and record.column == current.column and record.zoom == current.zoom then return true end
    return self:save(record)
end

function Store:acknowledge(upload)
    local record = self.records[upload.puzzleId]
    if not record or not record.dirty or record.marks ~= upload.marks or record.completed ~= upload.completed then return true end
    local saved = copy(record)
    saved.dirty = nil
    return self:save(saved)
end

function Store:list(game)
    local records = {}
    for id, record in pairs(self.records) do
        if not game or Puzzle.game(id) == game then records[#records + 1] = record end
    end
    local function rank(record)
        if record.completed then return 3 end
        return record.marks and 1 or 2
    end
    table.sort(records, function(a, b)
        local aRank, bRank = rank(a), rank(b)
        if aRank ~= bRank then return aRank < bRank end
        if type(a.puzzle.id) ~= type(b.puzzle.id) then return type(a.puzzle.id) == "number" end
        return a.puzzle.id > b.puzzle.id
    end)
    return records
end

function Store:target(game) return self.settings[Store.targetKeys[game]] end

function Store:setFilters(game, value)
    local filters = copy(self.settings.filters)
    filters[game] = Puzzle.filters(game, value)
    return self:updateSettings({ filters = filters })
end

function Store:hasGame(game)
    for id in pairs(self.records) do if Puzzle.game(id) == game then return true end end
    return false
end

return Store
