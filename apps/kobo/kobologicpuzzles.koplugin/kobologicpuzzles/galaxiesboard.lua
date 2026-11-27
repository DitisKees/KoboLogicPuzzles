local EdgeHistory = require("kobologicpuzzles/edgehistory")
local Slitherlink = require("kobologicpuzzles/slitherlinkboard")

local Galaxies = setmetatable({ edgeSize = 1 }, { __index = EdgeHistory })
Galaxies.__index = Galaxies

local function whole(value, lo, hi)
    return type(value) == "number" and value >= lo and value <= hi and value == math.floor(value)
end

local function withCore(puzzle, add)
    local width = puzzle.width
    for i, center in ipairs(puzzle.centers) do
        for y = math.floor((center.y - 1) / 2), math.floor(center.y / 2) do
            for x = math.floor((center.x - 1) / 2), math.floor(center.x / 2) do
                if not add(y * width + x + 1, i) then return false end
            end
        end
    end
    return true
end

function Galaxies.validPuzzle(puzzle)
    local width, height, centers, metadata = puzzle.width, puzzle.height, puzzle.centers, puzzle.metadata
    if not whole(width, 1, 250) or not whole(height, 1, 250) or type(puzzle.title) ~= "string"
        or type(centers) ~= "table" or #centers < 1 or #centers > width * height or type(metadata) ~= "table"
        or not Slitherlink.validSource(metadata.source) or type(metadata.sourceName) ~= "string"
        or type(metadata.picture) ~= "boolean" then return false end
    local black, white = false, false
    for _, center in ipairs(centers) do
        if type(center) ~= "table" or not whole(center.x, 1, 2 * width - 1)
            or not whole(center.y, 1, 2 * height - 1) or type(center.black) ~= "boolean" then return false end
        black, white = black or center.black, white or not center.black
    end
    if metadata.picture and not (black and white) then return false end
    local owned = {}
    return withCore(puzzle, function(cell)
        if owned[cell] then return false end
        owned[cell] = true
        return true
    end)
end

local function edgeCount(puzzle)
    return 2 * puzzle.width * puzzle.height - puzzle.width - puzzle.height
end

function Galaxies:new(puzzle, marks)
    local board = setmetatable({ puzzle = puzzle, width = puzzle.width, height = puzzle.height,
        solution = puzzle.solution, state = {}, undo = {}, redo = {}, core = {}, dotsByRow = {} }, self)
    local initial = marks or string.rep(".", edgeCount(puzzle))
    for mark in initial:gmatch(".") do board.state[#board.state + 1] = mark end
    withCore(puzzle, function(cell, center)
        board.core[cell] = center
        return true
    end)
    for _, center in ipairs(puzzle.centers) do
        local y = math.floor(center.y / 2)
        local row = board.dotsByRow[y] or {}
        row[#row + 1] = center
        board.dotsByRow[y] = row
    end
    return board
end

function Galaxies:horizontal(row, column)
    return (row - 1) * self.width + column + 1
end

function Galaxies:vertical(row, column)
    return (self.height - 1) * self.width + row * (self.width - 1) + column
end

function Galaxies:edgeCells(index)
    local across = self.width * (self.height - 1)
    if index <= across then return index, index + self.width end
    local cell = index - across
    local a = cell + math.floor((cell - 1) / (self.width - 1))
    return a, a + 1
end

function Galaxies:regions()
    if self.shade then return self.shade end
    local width, n = self.width, self.width * self.height
    local parent, center, invalid, cells = {}, {}, {}, {}
    for cell = 1, n do parent[cell] = cell end
    local function root(cell)
        while parent[cell] ~= cell do
            parent[cell] = parent[parent[cell]]
            cell = parent[cell]
        end
        return cell
    end
    for index, mark in ipairs(self.state) do
        if mark ~= "#" then
            local a, b = self:edgeCells(index)
            parent[root(b)] = root(a)
        end
    end
    for cell = 1, n do cells[cell] = root(cell) end
    for cell, owner in pairs(self.core) do
        local group = cells[cell]
        if center[group] and center[group] ~= owner then invalid[group] = true end
        center[group] = owner
    end
    -- A center crossing a wall invalidates all components it touches.
    local first = {}
    for cell, owner in pairs(self.core) do
        local group = cells[cell]
        first[owner] = first[owner] or group
        if first[owner] ~= group then invalid[group], invalid[first[owner]] = true, true end
    end
    for index, mark in ipairs(self.state) do
        if mark == "#" then
            local a, b = self:edgeCells(index)
            if cells[a] == cells[b] then invalid[cells[a]] = true end
        end
    end
    for cell = 1, n do
        local group = cells[cell]
        local clue = self.puzzle.centers[center[group]]
        if clue and not invalid[group] then
            local x = clue.x - 1 - (cell - 1) % width
            local y = clue.y - 1 - math.floor((cell - 1) / width)
            if x < 0 or x >= width or y < 0 or y >= self.height or cells[y * width + x + 1] ~= group then
                invalid[group] = true
            end
        end
    end
    self.shade, self.completed = {}, true
    for cell = 1, n do
        local group = cells[cell]
        local clue = self.puzzle.centers[center[group]]
        if not clue or invalid[group] then
            self.completed = false
        elseif clue.black then
            self.shade[cell] = true
        end
    end
    return self.shade
end

function Galaxies:isComplete()
    self:regions()
    return self.completed
end

function Galaxies:apply(changes, key)
    self.shade = nil
    return EdgeHistory.apply(self, changes, key)
end

function Galaxies.validMarks(puzzle, marks, completed)
    return type(marks) == "string" and #marks == edgeCount(puzzle) and not marks:find("[^.#x]")
        and (not completed or Galaxies:new(puzzle, marks):isComplete())
end

function Galaxies.validSolution(puzzle, solution)
    return type(solution) == "string" and not solution:find("%.") and Galaxies.validMarks(puzzle, solution, true)
end

function Galaxies.progress(puzzle, marks)
    local _, marked = (marks or ""):gsub("[#x]", "")
    return marked, math.max(1, edgeCount(puzzle))
end

return Galaxies
