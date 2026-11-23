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

function Board:new(puzzle, marks)
    local board = setmetatable({
        puzzle = puzzle, width = puzzle.width, height = puzzle.height,
        cells = {}, rows = {}, columns = {}, undo = {}, redo = {}, unfinished = 0,
    }, self)
    marks = marks or string.rep(".", puzzle.width * puzzle.height)
    for index = 1, #marks do
        board.cells[index] = marks:sub(index, index)
    end
    for row = 1, board.height do board:checkRow(row) end
    for column = 1, board.width do board:checkColumn(column) end
    return board
end

function Board:checkRow(row)
    local matches = matchLine(self.cells, (row - 1) * self.width + 1, self.width, 1, self.puzzle.rowClues[row])
    if self.rows[row] == false then self.unfinished = self.unfinished - 1 end
    if not matches then self.unfinished = self.unfinished + 1 end
    self.rows[row] = matches
end

function Board:checkColumn(column)
    local matches = matchLine(self.cells, column, self.height, self.width, self.puzzle.columnClues[column])
    if self.columns[column] == false then self.unfinished = self.unfinished - 1 end
    if not matches then self.unfinished = self.unfinished + 1 end
    self.columns[column] = matches
end

function Board:isComplete() return self.unfinished == 0 end
function Board:marks() return table.concat(self.cells) end
function Board:get(row, column) return self.cells[(row - 1) * self.width + column] end

function Board:markCell(row, column)
    local stroke = self.stroke
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
    return self:markCell(row, column)
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
            if self:markCell(r, c) then changed = true end
        end
    end
    if changed then return firstRow, firstColumn, lastRow, lastColumn end
end

function Board:refreshClues(changes)
    local rows, columns = {}, {}
    for _, change in ipairs(changes) do
        rows[change.row], columns[change.column] = true, true
    end
    for row in pairs(rows) do self:checkRow(row) end
    for column in pairs(columns) do self:checkColumn(column) end
end

function Board:finishStroke()
    local stroke = self.stroke
    self.stroke = nil
    if not stroke or #stroke.changes == 0 then return nil end
    self:refreshClues(stroke.changes)
    self.undo[#self.undo + 1] = stroke
    if #self.undo > 100 then table.remove(self.undo, 1) end
    self.redo = {}
    return stroke.changes
end

function Board:replay(from, to, undo)
    local stroke = table.remove(from)
    if not stroke then return nil end
    for _, change in ipairs(stroke.changes) do
        self.cells[change.index] = undo and change.mark or stroke.mark
    end
    self:refreshClues(stroke.changes)
    to[#to + 1] = stroke
    return stroke.changes
end

function Board:undoStroke() return self:replay(self.undo, self.redo, true) end
function Board:redoStroke() return self:replay(self.redo, self.undo, false) end

return Board
