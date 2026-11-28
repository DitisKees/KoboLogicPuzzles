local Board = require("kobologicpuzzles/board")

local Nurikabe = setmetatable({ autoCross = false }, { __index = Board })
Nurikabe.__index = Nurikabe

local function isInteger(value, min, max)
    return type(value) == "number" and value >= min and value <= max and value == math.floor(value)
end

function Nurikabe.validPuzzle(puzzle)
    if type(puzzle) ~= "table" or not isInteger(puzzle.width, 1, 50) or not isInteger(puzzle.height, 1, 50)
        or type(puzzle.title) ~= "string" or (puzzle.author ~= nil and type(puzzle.author) ~= "string")
        or type(puzzle.clues) ~= "table" or #puzzle.clues ~= puzzle.width * puzzle.height then return false end
    local area = 0
    for _, clue in ipairs(puzzle.clues) do
        if not isInteger(clue, 0, #puzzle.clues) then return false end
        area = area + clue
    end
    return area > 0 and area < #puzzle.clues
end

local function neighbors(width, height)
    local result = {}
    for cell = 1, width * height do
        local col = (cell - 1) % width
        local list = {}
        if cell > width then list[#list + 1] = cell - width end
        if cell <= width * (height - 1) then list[#list + 1] = cell + width end
        if col > 0 then list[#list + 1] = cell - 1 end
        if col < width - 1 then list[#list + 1] = cell + 1 end
        result[cell] = list
    end
    return result
end

local function flood(start, adjacent, seen, allowed)
    local cells = { start }
    local i = 1
    seen[start] = true
    while i <= #cells do
        for _, cell in ipairs(adjacent[cells[i]]) do
            if not seen[cell] and allowed(cell) then
                seen[cell] = true
                cells[#cells + 1] = cell
            end
        end
        i = i + 1
    end
    return cells
end

function Nurikabe:refreshClues()
    local cells, clues, adjacent, width = self.cells, self.puzzle.clues, self.adjacent, self.width
    local status, seen = {}, {}
    self.undecided, self.conflicts = 0, 0
    local function color(region, name)
        for _, cell in ipairs(region) do status[cell] = name end
    end
    local function conflict(region)
        self.conflicts = self.conflicts + 1
        color(region, "conflict")
    end
    local sea
    for cell, mark in ipairs(cells) do
        if mark == "." then
            self.undecided = self.undecided + 1
        elseif mark == "#" then
            sea = sea or cell
            if (cell - 1) % width < width - 1
                and cells[cell + 1] == "#" and cells[cell + width] == "#" and cells[cell + width + 1] == "#" then
                conflict({ cell, cell + 1, cell + width, cell + width + 1 })
            end
        elseif not seen[cell] then
            local island = flood(cell, adjacent, seen, function(i) return cells[i] == "x" end)
            local area, count, open = 0, 0, false
            for _, i in ipairs(island) do
                if clues[i] > 0 then area, count = clues[i], count + 1 end
                for _, nextCell in ipairs(adjacent[i]) do
                    if cells[nextCell] == "." then open = true end
                end
            end
            if count > 1 or count == 1 and #island > area or not open and (count == 0 or #island ~= area) then
                conflict(island)
            elseif count == 1 and #island == area then
                color(island, "satisfied")
            end
        end
    end
    if sea then
        local reachable, separated = {}, {}
        flood(sea, adjacent, reachable, function(i) return cells[i] ~= "x" end)
        for cell, mark in ipairs(cells) do
            if mark == "#" and not reachable[cell] then separated[#separated + 1] = cell end
        end
        if #separated > 0 then
            for cell, mark in ipairs(cells) do
                if mark == "#" and reachable[cell] then separated[#separated + 1] = cell end
            end
            conflict(separated)
        end
    elseif self.undecided == 0 then
        self.conflicts = self.conflicts + 1
    end
    self.clueStates = status
    self.unfinished = self.conflicts + self.undecided
end

function Nurikabe.validMarks(puzzle, marks, completed)
    if not Board.validMarks(puzzle, marks) then return false end
    for cell, clue in ipairs(puzzle.clues) do
        if clue > 0 and marks:sub(cell, cell) ~= "x" then return false end
    end
    if not completed then return true end
    if marks:find(".", 1, true) then return false end
    local state = setmetatable({ puzzle = puzzle, width = puzzle.width,
        adjacent = neighbors(puzzle.width, puzzle.height), cells = {} }, Nurikabe)
    for i = 1, #marks do state.cells[i] = marks:sub(i, i) end
    state:refreshClues()
    return state:isComplete()
end

function Nurikabe.validSolution(puzzle, answer)
    return Nurikabe.validMarks(puzzle, answer, true)
end

function Nurikabe.initialMarks(puzzle)
    local marks = {}
    for i, clue in ipairs(puzzle.clues) do marks[i] = clue > 0 and "x" or "." end
    return table.concat(marks)
end

function Nurikabe:new(puzzle, marks)
    local board = self:newGrid(puzzle, marks or self.initialMarks(puzzle))
    board.adjacent = neighbors(puzzle.width, puzzle.height)
    board:refreshClues()
    return board
end

function Nurikabe:markCell(row, col, stroke)
    if self.puzzle.clues[(row - 1) * self.width + col] > 0 then return false end
    return Board.markCell(self, row, col, stroke)
end

function Nurikabe:checkSolution()
    local errors = Board.checkSolution(self)
    return errors, self.undecided
end

return Nurikabe
