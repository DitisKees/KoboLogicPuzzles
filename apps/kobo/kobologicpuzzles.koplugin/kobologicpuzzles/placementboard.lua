local Board = require("kobologicpuzzles/board")

local Placement = setmetatable({}, { __index = Board })
Placement.__index = Placement
Placement.autoCross = false

local function integer(n, min, max)
    return type(n) == "number" and n >= min and n <= max and n == math.floor(n)
end

local function total(lines, n, max)
    if type(lines) ~= "table" or #lines ~= n then return end
    local sum = 0
    for i = 1, n do
        local clue = lines[i]
        if type(clue) ~= "table" or #clue ~= 1 or not integer(clue[1], 0, max) then return end
        sum = sum + clue[1]
    end
    return sum
end

function Placement.validPuzzle(puzzle)
    local width, height = puzzle.width, puzzle.height
    if not integer(width, 1, 50) or not integer(height, 1, 50) or type(puzzle.title) ~= "string"
        or type(puzzle.givens) ~= "string" or #puzzle.givens ~= width * height then return false end
    local count = total(puzzle.rowClues, height, width)
    if not count or count == 0 or count ~= total(puzzle.columnClues, width, height) then return false end
    if puzzle.game == "tents" then
        local _, trees = puzzle.givens:gsub("t", "")
        return trees == count and not puzzle.givens:find("[^.#xt]")
    end
    local fleet = puzzle.fleet
    if puzzle.game ~= "battleships" or puzzle.givens:find("[^.#xonsemw]") or type(fleet) ~= "table"
        or #fleet == 0 or #fleet > math.max(width, height) then return false end
    local segments = 0
    for len = 1, #fleet do
        if not integer(fleet[len], 0, count) then return false end
        segments = segments + len * fleet[len]
    end
    return segments == count
end

function Placement.initialMarks(puzzle)
    return (puzzle.givens:gsub("[^%.tx]", "#"):gsub("t", "."))
end

local function sides(i, width, height)
    local row, col = math.floor((i - 1) / width), (i - 1) % width
    return row > 0 and i - width, col + 1 < width and i + 1, row + 1 < height and i + width, col > 0 and i - 1
end

local function matching(puzzle, cells, allowUnknown)
    local owners, seen = {}, {}
    local function match(tree, stamp)
        local n, e, s, w = sides(tree, puzzle.width, puzzle.height)
        local function claim(cell)
            if not cell or seen[cell] == stamp
                or not (cells[cell] == "#" or allowUnknown and cells[cell] == "." and puzzle.givens:sub(cell, cell) == ".") then
                return false
            end
            seen[cell] = stamp
            if owners[cell] and not match(owners[cell], stamp) then return false end
            owners[cell] = tree
            return true
        end
        return claim(n) or claim(e) or claim(s) or claim(w)
    end
    for tree = 1, #cells do
        if puzzle.givens:sub(tree, tree) == "t" and not match(tree, tree) then return false end
    end
    return true
end

local function fleetState(puzzle, cells, complete)
    local width, height = puzzle.width, puzzle.height
    local seen, remaining, conflicts = {}, {}, 0
    for len, count in ipairs(puzzle.fleet) do remaining[len] = count end
    for i = 1, #cells do
        if cells[i] == "#" and not seen[i] then
            local ship = { i }
            seen[i] = true
            local j = 1
            while j <= #ship do
                local n, e, s, w = sides(ship[j], width, height)
                local function visit(nextCell)
                    if nextCell and cells[nextCell] == "#" and not seen[nextCell] then
                        ship[#ship + 1] = nextCell
                        seen[nextCell] = true
                    end
                end
                visit(n)
                visit(e)
                visit(s)
                visit(w)
                j = j + 1
            end
            table.sort(ship)
            local first, last = ship[1], ship[#ship]
            local horizontal = math.floor((first - 1) / width) == math.floor((last - 1) / width)
            local straight = (horizontal and last - first or (last - first) / width) == #ship - 1
            if straight and not horizontal then
                for k, cell in ipairs(ship) do
                    if cell ~= first + (k - 1) * width then straight = false break end
                end
            end
            if not straight or #ship > #remaining then
                conflicts = conflicts + 1
            else
                local firstClue, lastClue = puzzle.givens:sub(first, first), puzzle.givens:sub(last, last)
                local firstN, _, _, firstW = sides(first, width, height)
                local _, lastE, lastS = sides(last, width, height)
                local closed
                if #ship == 1 then
                    closed = firstClue == "o" or cells[firstN] ~= "." and cells[firstW] ~= "."
                        and cells[lastE] ~= "." and cells[lastS] ~= "."
                elseif horizontal then
                    closed = (firstClue == "w" or cells[firstW] ~= ".") and (lastClue == "e" or cells[lastE] ~= ".")
                else
                    closed = (firstClue == "n" or cells[firstN] ~= ".") and (lastClue == "s" or cells[lastS] ~= ".")
                end
                if complete or closed then
                    remaining[#ship] = remaining[#ship] - 1
                    if remaining[#ship] < 0 then conflicts = conflicts + 1 end
                end
                for k, cell in ipairs(ship) do
                    local clue = puzzle.givens:sub(cell, cell)
                    if clue ~= "." and clue ~= "#" then
                        local shape = #ship == 1 and "o" or k == 1 and (horizontal and "w" or "n")
                            or k == #ship and (horizontal and "e" or "s") or "m"
                        if (complete or closed or clue == "o") and clue ~= shape then conflicts = conflicts + 1 end
                    end
                end
            end
        end
    end
    return remaining, conflicts
end

local function adjacency(puzzle, cells)
    local width, height, conflicts = puzzle.width, puzzle.height, 0
    local tents = puzzle.game == "tents"
    for i = 1, #cells do
        if cells[i] == "#" then
            local row, col = math.floor((i - 1) / width), (i - 1) % width
            if row + 1 < height then
                if col > 0 and cells[i + width - 1] == "#" then conflicts = conflicts + 1 end
                if col + 1 < width and cells[i + width + 1] == "#" then conflicts = conflicts + 1 end
                if tents and cells[i + width] == "#" then conflicts = conflicts + 1 end
            end
            if tents and col + 1 < width and cells[i + 1] == "#" then conflicts = conflicts + 1 end
            if tents then
                local n, e, s, w = sides(i, width, height)
                local function tree(neighbor) return neighbor and puzzle.givens:sub(neighbor, neighbor) == "t" end
                if not (tree(n) or tree(e) or tree(s) or tree(w)) then conflicts = conflicts + 1 end
            end
        end
    end
    return conflicts
end

local function line(board, first, length, step, target)
    local occupied, unknown = 0, 0
    for i = first, first + (length - 1) * step, step do
        local cell = board.cells[i]
        if cell == "#" then occupied = occupied + 1
        elseif cell == "." and board.puzzle.givens:sub(i, i) ~= "t" then unknown = unknown + 1 end
    end
    return { complete = occupied == target, impossible = occupied > target or occupied + unknown < target }
end

function Placement:checkRow(row)
    return self:updateLine(self.rows, row, line(self, (row - 1) * self.width + 1, self.width, 1, self.puzzle.rowClues[row][1]))
end

function Placement:checkColumn(column)
    return self:updateLine(self.columns, column, line(self, column, self.height, self.width, self.puzzle.columnClues[column][1]))
end

function Placement:new(puzzle, marks)
    local board = self:newGrid(puzzle, marks or self.initialMarks(puzzle))
    -- Offline Check uses only the rules and the current marks.
    board.solution = nil
    if puzzle.game == "battleships" then board.clueStates = {} end
    for row = 1, board.height do board:checkRow(row) end
    for col = 1, board.width do board:checkColumn(col) end
    return board
end

function Placement:refreshClues(changes)
    self.complete, self.fleetRemaining = nil, nil
    return Board.refreshClues(self, changes)
end

function Placement:markCell(row, col, stroke)
    local i = (row - 1) * self.width + col
    if self.puzzle.givens:sub(i, i) ~= "." then return false end
    return Board.markCell(self, row, col, stroke)
end

function Placement:isComplete()
    if self.unfinished ~= 0 then return false end
    if self.complete ~= nil then return self.complete end
    local puzzle, cells = self.puzzle, self.cells
    self.complete = false
    if adjacency(puzzle, cells) > 0 then return false end
    if puzzle.game == "tents" then
        self.complete = matching(puzzle, cells, false)
    else
        local _, conflicts = fleetState(puzzle, cells, true)
        -- The fleet and the line totals count the same number of segments.
        self.complete = conflicts == 0
    end
    return self.complete
end

function Placement.validMarks(puzzle, marks, completed)
    if type(marks) ~= "string" or #marks ~= puzzle.width * puzzle.height or marks:find("[^.#x]") then return false end
    local initial = Placement.initialMarks(puzzle)
    for i = 1, #marks do
        if puzzle.givens:sub(i, i) ~= "." and marks:sub(i, i) ~= initial:sub(i, i) then return false end
    end
    if not completed then return true end
    local grid = Board.newGrid(Placement, { width = puzzle.width, height = puzzle.height }, marks)
    grid.puzzle = puzzle
    for row = 1, grid.height do grid:checkRow(row) end
    for col = 1, grid.width do grid:checkColumn(col) end
    return grid:isComplete()
end

function Placement.validSolution(puzzle, answer) return Placement.validMarks(puzzle, answer, true) end

function Placement:ruleConflicts()
    if self:isComplete() then return 0 end
    local conflicts = adjacency(self.puzzle, self.cells)
    for _, lines in ipairs{ self.rows, self.columns } do
        for _, status in ipairs(lines) do if status.impossible then conflicts = conflicts + 1 end end
    end
    if self.puzzle.game == "tents" then
        if not matching(self.puzzle, self.cells, self.unfinished ~= 0) then conflicts = conflicts + 1 end
    else
        local _, fleetConflicts = fleetState(self.puzzle, self.cells, self.unfinished == 0)
        conflicts = conflicts + fleetConflicts
    end
    return conflicts
end

function Placement:remainingFleet()
    if not self.fleetRemaining then
        self.fleetRemaining = fleetState(self.puzzle, self.cells, self.unfinished == 0)
    end
    return self.fleetRemaining
end

function Placement:shipShape(i)
    local clue = self.puzzle.givens:sub(i, i)
    if clue ~= "." and clue ~= "#" and clue ~= "m" then return clue end
    local n, e, s, w = sides(i, self.width, self.height)
    local up, right, down, left = self.cells[n] == "#", self.cells[e] == "#", self.cells[s] == "#", self.cells[w] == "#"
    if clue == "m" then
        if (up or down) and not (right or left) then return "|" end
        if (right or left) and not (up or down) then return "=" end
        return "m"
    end
    if (up or down) and (right or left) then return "#" end
    if up and down then return "|" end
    if right and left then return "=" end
    return up and "s" or down and "n" or right and "w" or left and "e" or (clue == "#" and "#" or "o")
end

function Placement:getOverviewMark(row, column)
    local i = (row - 1) * self.width + column
    return self.puzzle.givens:sub(i, i) == "t" and "t" or self.cells[i]
end

function Placement.progress(puzzle, marks)
    local marked, total = 0, 0
    for i = 1, #puzzle.givens do
        if puzzle.givens:sub(i, i) == "." then
            total = total + 1
            if marks:sub(i, i):match("[#x]") then marked = marked + 1 end
        end
    end
    return marked, math.max(1, total)
end

return Placement
