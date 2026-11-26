local Board = {}
Board.__index = Board

local function positiveInteger(value, limit)
    return type(value) == "number" and value >= 1 and value <= limit and value == math.floor(value)
end

local function clueTotal(lines, lineCount, lineLength)
    if type(lines) ~= "table" or #lines ~= lineCount then return nil end
    local total = 0
    for _, clues in ipairs(lines) do
        if type(clues) ~= "table" then return nil end
        local filled = 0
        for _, count in ipairs(clues) do
            if not positiveInteger(count, lineLength) then return nil end
            filled = filled + count
        end
        if filled + math.max(0, #clues - 1) > lineLength then return nil end
        total = total + filled
    end
    return total
end

function Board.validPuzzle(puzzle)
    if type(puzzle) ~= "table" or not positiveInteger(puzzle.id, 2147483647)
        or not positiveInteger(puzzle.width, 100) or not positiveInteger(puzzle.height, 100)
        or type(puzzle.title) ~= "string" or type(puzzle.author) ~= "string" then
        return false
    end
    local rows = clueTotal(puzzle.rowClues, puzzle.height, puzzle.width)
    local columns = clueTotal(puzzle.columnClues, puzzle.width, puzzle.height)
    return rows ~= nil and rows == columns
end

function Board.validMarks(puzzle, marks)
    return type(marks) == "string" and #marks == puzzle.width * puzzle.height
        and not marks:find("[^.#x]")
end

local function matchLine(cells, start, length, stride, clues)
    local nextClue, run = 1, 0
    for index = start, start + (length - 1) * stride, stride do
        if cells[index] == "#" then
            run = run + 1
        elseif run > 0 then
            if clues[nextClue] ~= run then return false end
            nextClue, run = nextClue + 1, 0
        end
    end
    if run > 0 then
        if clues[nextClue] ~= run then return false end
        nextClue = nextClue + 1
    end
    return nextClue > #clues
end

function Board.validSolution(puzzle, solution)
    if type(solution) ~= "string" or #solution ~= puzzle.width * puzzle.height
        or solution:find("[^.#]") then return false end
    local cells = {}
    for i = 1, #solution do cells[i] = solution:sub(i, i) end
    for row, clues in ipairs(puzzle.rowClues) do
        if not matchLine(cells, (row - 1) * puzzle.width + 1, puzzle.width, 1, clues) then return false end
    end
    for column, clues in ipairs(puzzle.columnClues) do
        if not matchLine(cells, column, puzzle.height, puzzle.width, clues) then return false end
    end
    return true
end

local completeLine, blankLine, impossibleLine = { complete = true }, {}, { impossible = true }

local function canPrecedeRun(prefix, line, first)
    if first == 1 then return prefix[0] end
    return line[first - 1] ~= "#" and prefix[first - 2]
end

-- Each entry says whether the first k clues can fit in the first p cells.
local function linePrefixes(line, clues, crossed, reverse)
    local empty = { [0] = true }
    for p = 1, #line do empty[p] = empty[p - 1] and line[p] ~= "#" end
    local prefixes = { [0] = empty }
    for k = 1, #clues do
        local count = clues[reverse and #clues - k + 1 or k]
        local previous, fits = prefixes[k - 1], {}
        for p = 1, #line do
            local first = p - count + 1
            fits[p] = (line[p] ~= "#" and fits[p - 1]) or
                (first >= 1 and crossed[p] == crossed[first - 1] and canPrecedeRun(previous, line, first))
        end
        prefixes[k] = fits
    end
    return prefixes
end

local function checkLine(cells, start, length, stride, clues)
    if matchLine(cells, start, length, stride, clues) then return completeLine end
    local line, crossed, filled = {}, { [0] = 0 }, { [0] = 0 }
    for p = 1, length do
        local mark = cells[start + (p - 1) * stride]
        line[p] = mark
        crossed[p] = crossed[p - 1] + (mark == "x" and 1 or 0)
        filled[p] = filled[p - 1] + (mark == "#" and 1 or 0)
    end
    if crossed[length] == 0 and filled[length] == 0 then return blankLine end
    local prefix = linePrefixes(line, clues, crossed)
    if not prefix[#clues][length] then return impossibleLine end
    if filled[length] == 0 then return blankLine end

    local reversed, reverseCrossed = {}, { [0] = 0 }
    for p = 1, length do
        reversed[p] = line[length - p + 1]
        reverseCrossed[p] = crossed[length] - crossed[length - p]
    end
    local suffix = linePrefixes(reversed, clues, reverseCrossed, true)
    local completed = {}
    for k, count in ipairs(clues) do
        for first = 1, length - count + 1 do
            local last = first + count - 1
            if crossed[last] == crossed[first - 1] and canPrecedeRun(prefix[k - 1], line, first)
                and canPrecedeRun(suffix[#clues - k], reversed, length - last + 1) then
                -- Every compatible placement must cover the same fully filled run.
                if completed[k] or filled[last] - filled[first - 1] ~= count then
                    completed[k] = nil
                    break
                end
                completed[k] = true
            end
        end
    end
    return completed
end

function Board:newGrid(puzzle, marks)
    local board = setmetatable({
        puzzle = puzzle, width = puzzle.width, height = puzzle.height,
        cells = {}, rows = {}, columns = {}, undo = {}, redo = {}, unfinished = 0,
    }, self)
    if self.validSolution(puzzle, puzzle.solution) then board.solution = puzzle.solution end
    local initialMarks = marks or string.rep(".", puzzle.width * puzzle.height)
    for index = 1, #initialMarks do
        board.cells[index] = initialMarks:sub(index, index)
    end
    return board
end

function Board:new(puzzle, marks)
    local board = self:newGrid(puzzle, marks)
    local rows, columns = {}, {}
    for row = 1, board.height do rows[row] = board:checkRow(row) end
    for column = 1, board.width do columns[column] = board:checkColumn(column) end
    if not marks then
        local automatic = board:crossCompletedLines(rows, columns)
        if automatic then board:pushUndo(automatic) end
    end
    return board
end

function Board:updateLine(states, index, status)
    local previous = states[index]
    if previous and not previous.complete then self.unfinished = self.unfinished - 1 end
    if not status.complete then self.unfinished = self.unfinished + 1 end
    states[index] = status
    if status.complete and not (previous and previous.complete) then return true end
end

function Board:checkRow(row)
    return self:updateLine(self.rows, row,
        checkLine(self.cells, (row - 1) * self.width + 1, self.width, 1, self.puzzle.rowClues[row]))
end

function Board:checkColumn(column)
    return self:updateLine(self.columns, column,
        checkLine(self.cells, column, self.height, self.width, self.puzzle.columnClues[column]))
end

function Board:isComplete() return self.unfinished == 0 end
function Board:marks() return table.concat(self.cells) end
function Board:get(row, column) return self.cells[(row - 1) * self.width + column] end

function Board:checkSolution()
    local errors, remaining = 0, 0
    for index, mark in ipairs(self.cells) do
        local answer = self.solution:sub(index, index)
        if (mark == "#" and answer == ".") or (mark == "x" and answer == "#") then
            errors = errors + 1
        elseif mark == "." and answer == "#" then
            remaining = remaining + 1
        end
    end
    return errors, remaining
end

function Board:markCell(row, column, stroke)
    local index = (row - 1) * self.width + column
    local previous = self.cells[index]
    if previous == stroke.mark then return false end
    stroke.changes[#stroke.changes + 1] = { index = index, row = row, column = column, mark = previous }
    self.cells[index] = stroke.mark
    return true
end

function Board:beginStroke(row, column, mark)
    if self:get(row, column) == mark then mark = "." end
    self.stroke = { row = row, column = column, lastRow = row, lastColumn = column, mark = mark, changes = {} }
    return self:markCell(row, column, self.stroke)
end

function Board:extendStroke(row, column)
    local stroke = self.stroke
    if not stroke.axis then
        local vertical = math.abs(row - stroke.row)
        local horizontal = math.abs(column - stroke.column)
        if vertical == 0 and horizontal == 0 then return nil end
        stroke.axis = vertical > horizontal and "column" or "row"
    end
    if stroke.axis == "row" then row = stroke.row else column = stroke.column end

    local firstRow, lastRow = math.min(stroke.lastRow, row), math.max(stroke.lastRow, row)
    local firstColumn, lastColumn = math.min(stroke.lastColumn, column), math.max(stroke.lastColumn, column)
    stroke.lastRow, stroke.lastColumn = row, column
    local changed = false
    for r = firstRow, lastRow do
        for c = firstColumn, lastColumn do
            if self:markCell(r, c, stroke) then changed = true end
        end
    end
    if changed then return firstRow, firstColumn, lastRow, lastColumn end
end

function Board:refreshClues(changes)
    local rows, columns = {}, {}
    for _, change in ipairs(changes) do
        rows[change.row], columns[change.column] = true, true
    end
    for row in pairs(rows) do rows[row] = self:checkRow(row) end
    for column in pairs(columns) do columns[column] = self:checkColumn(column) end
    return rows, columns
end

function Board:crossCompletedLines(rows, columns)
    if not next(rows) and not next(columns) then return nil end
    local stroke = { mark = "x", changes = {} }
    local function cross(row, column)
        if self:get(row, column) == "." then self:markCell(row, column, stroke) end
    end
    for row in pairs(rows) do
        for column = 1, self.width do cross(row, column) end
    end
    for column in pairs(columns) do
        for row = 1, self.height do cross(row, column) end
    end
    if #stroke.changes == 0 then return nil end
    self:refreshClues(stroke.changes)
    return stroke
end

function Board:finishStroke()
    local stroke = self.stroke
    self.stroke = nil
    if stroke then return self:remember(stroke, self.autoCross ~= false) end
end

function Board:cancelStroke()
    for _, change in ipairs(self.stroke.changes) do
        self.cells[change.index] = change.mark
    end
    self.stroke = nil
end

function Board:restoreCheckpoint(marks)
    local changes = {}
    for index, mark in ipairs(self.cells) do
        local restored = marks:sub(index, index)
        if restored ~= mark then
            changes[#changes + 1] = { index = index, row = math.floor((index - 1) / self.width) + 1,
                column = (index - 1) % self.width + 1, mark = mark }
            self.cells[index] = restored
        end
    end
    return self:remember({ changes = changes, marks = marks })
end

function Board:pushUndo(stroke)
    self.undo[#self.undo + 1] = stroke
    if #self.undo > 100 then table.remove(self.undo, 1) end
end

function Board:remember(stroke, autoCross)
    if #stroke.changes == 0 then return nil end
    local rows, columns = self:refreshClues(stroke.changes)
    self:pushUndo(stroke)
    self.redo = {}
    if not autoCross then return stroke.changes end
    local automatic = self:crossCompletedLines(rows, columns)
    if not automatic then return stroke.changes end
    self:pushUndo(automatic)

    -- Repaint both actions together while keeping their history entries separate.
    local changes = {}
    for _, batch in ipairs({ stroke.changes, automatic.changes }) do
        for _, change in ipairs(batch) do changes[#changes + 1] = change end
    end
    return changes
end

function Board:replay(from, to, undo)
    local stroke = table.remove(from)
    if not stroke then return nil end
    for _, change in ipairs(stroke.changes) do
        self.cells[change.index] = undo and change.mark or stroke.mark or stroke.marks:sub(change.index, change.index)
    end
    self:refreshClues(stroke.changes)
    to[#to + 1] = stroke
    return stroke.changes
end

function Board:undoStroke() return self:replay(self.undo, self.redo, true) end
function Board:redoStroke() return self:replay(self.redo, self.undo, false) end

return Board
