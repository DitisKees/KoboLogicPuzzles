local Board = require("kobologicpuzzles/board")

local FillAPix = setmetatable({ autoCross = false }, { __index = Board })
FillAPix.__index = FillAPix
local filledByte = string.byte("#")

local function positiveInteger(value)
    return type(value) == "number" and value >= 1 and value <= 2147483647 and value == math.floor(value)
end

local function bounds(width, height, row, column)
    return math.max(1, row - 1), math.min(height, row + 1), math.max(1, column - 1), math.min(width, column + 1)
end

local function coordinates(width, index)
    return math.floor((index - 1) / width) + 1, (index - 1) % width + 1
end

function FillAPix.validPuzzle(puzzle)
    if type(puzzle) ~= "table" or not positiveInteger(puzzle.width) or not positiveInteger(puzzle.height)
        or type(puzzle.title) ~= "string" or (puzzle.author ~= nil and type(puzzle.author) ~= "string")
        or type(puzzle.clues) ~= "table" then return false end

    local width, height = puzzle.width, puzzle.height
    if not positiveInteger(width * height) or #puzzle.clues ~= width * height then return false end
    local hasClue = false
    for index = 1, #puzzle.clues do
        local clue = puzzle.clues[index]
        if clue ~= false then
            local row, column = coordinates(width, index)
            local top, bottom, left, right = bounds(width, height, row, column)
            local size = (bottom - top + 1) * (right - left + 1)
            if type(clue) ~= "number" or clue < 0 or clue > size or clue ~= math.floor(clue) then return false end
            hasClue = true
        end
    end
    return hasClue
end

local function matchesClues(puzzle, marks)
    local width, height = puzzle.width, puzzle.height
    for index, clue in ipairs(puzzle.clues) do
        if clue ~= false then
            local row, column = coordinates(width, index)
            local top, bottom, left, right = bounds(width, height, row, column)
            local filled = 0
            for r = top, bottom do
                for c = left, right do
                    if marks:byte((r - 1) * width + c) == filledByte then filled = filled + 1 end
                end
            end
            if filled ~= clue then return false end
        end
    end
    return true
end

function FillAPix.validMarks(puzzle, marks, completed)
    return Board.validMarks(puzzle, marks) and (not completed or matchesClues(puzzle, marks))
end

function FillAPix.validSolution(puzzle, solution)
    return type(solution) == "string" and #solution == puzzle.width * puzzle.height
        and not solution:find("[^.#]") and matchesClues(puzzle, solution)
end

function FillAPix:new(puzzle, marks)
    local board = self:newGrid(puzzle, marks)
    board.clueStates, board.conflicts = {}, 0
    for index, clue in ipairs(puzzle.clues) do
        if clue ~= false then board:updateClue(index, coordinates(board.width, index)) end
    end
    return board
end

function FillAPix:counts(row, column)
    local top, bottom, left, right = bounds(self.width, self.height, row, column)
    local filled, undecided = 0, 0
    for r = top, bottom do
        for c = left, right do
            local mark = self:get(r, c)
            if mark == "#" then filled = filled + 1
            elseif mark == "." then undecided = undecided + 1 end
        end
    end
    return filled, undecided
end

function FillAPix:updateClue(index, row, column)
    local clue = self.puzzle.clues[index]
    local filled, undecided = self:counts(row, column)
    local status = filled == clue and (undecided == 0 and "satisfied" or "matched")
        or (filled > clue or filled + undecided < clue) and "conflict" or "open"
    local previous = self.clueStates[index]
    if previous == "open" or previous == "conflict" then self.unfinished = self.unfinished - 1 end
    if filled ~= clue then self.unfinished = self.unfinished + 1 end
    if previous == "conflict" then self.conflicts = self.conflicts - 1 end
    if status == "conflict" then self.conflicts = self.conflicts + 1 end
    self.clueStates[index] = status
end

function FillAPix:refreshClues(changes)
    local updated = {}
    for _, change in ipairs(changes) do
        local top, bottom, left, right = bounds(self.width, self.height, change.row, change.column)
        for row = top, bottom do
            for column = left, right do
                local index = (row - 1) * self.width + column
                if self.puzzle.clues[index] ~= false and not updated[index] then
                    self:updateClue(index, row, column)
                    updated[index] = true
                end
            end
        end
    end
end

return FillAPix
