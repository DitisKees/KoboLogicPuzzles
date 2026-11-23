local Board = require("kobononogram/board")
local Viewport = require("kobononogram/viewport")
local JSON = require("json")
local ffiutil = require("ffi/util")
local lfs = require("libs/libkoreader-lfs")
local logger = require("logger")

local Store = {}
Store.__index = Store

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
    if type(settings.target) ~= "number" or settings.target < 0 or settings.target > 100 then settings.target = 10 end
    settings.target = math.floor(settings.target)
    local clueLimit = settings.clueLimit
    if type(clueLimit) ~= "number" or clueLimit < 1 or clueLimit > Viewport.maxClueLimit
        or clueLimit ~= math.floor(clueLimit) then
        settings.clueLimit = Viewport.defaultClueLimit
    end
    local self = setmetatable({ root = root, records = {}, settings = settings }, Store)
    for name in lfs.dir(root) do
        local id = tonumber(name:match("^(%d+)%.json$"))
        if id then
            local record = read(root .. "/" .. name)
            if record and Board.validPuzzle(record.puzzle) and record.puzzle.id == id
                and (record.marks == nil or (Board.validMarks(record.puzzle, record.marks)
                    and type(record.completed) == "boolean")) then
                if not Board.validMarks(record.puzzle, record.checkpoint) then record.checkpoint = nil end
                self.records[id] = record
            else
                logger.warn("Kobo Nonogram: cannot load local puzzle", id)
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
    local ok, err = write(string.format("%s/%d.json", self.root, record.puzzle.id), record)
    if ok then self.records[record.puzzle.id] = record end
    return ok, err
end

function Store:download(puzzle)
    if not Board.validPuzzle(puzzle) then return nil, "The server returned an invalid puzzle definition." end
    if self.records[puzzle.id] then return true end
    return self:save({ format = 1, puzzle = puzzle })
end

function Store:restore(puzzle, progress)
    local existing = self.records[puzzle.id]
    if existing and existing.marks then return true end
    local record = existing and copy(existing) or { format = 1 }
    record.puzzle, record.marks, record.completed = puzzle, progress.marks, progress.completed
    if not Board.validMarks(puzzle, record.checkpoint) then record.checkpoint = nil end
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
    if type(previous) == "table" and previous.difficulty == metadata.difficulty
        and previous.quality == metadata.quality and previous.solvability == metadata.solvability then return true end
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
    if not record.dirty or record.marks ~= upload.marks or record.completed ~= upload.completed then return true end
    local saved = copy(record)
    saved.dirty = nil
    return self:save(saved)
end

function Store:list()
    local records = {}
    for _, record in pairs(self.records) do records[#records + 1] = record end
    local function rank(record)
        if record.completed then return 3 end
        return record.marks and 1 or 2
    end
    table.sort(records, function(a, b)
        local aRank, bRank = rank(a), rank(b)
        if aRank ~= bRank then return aRank < bRank end
        return a.puzzle.id > b.puzzle.id
    end)
    return records
end

return Store
