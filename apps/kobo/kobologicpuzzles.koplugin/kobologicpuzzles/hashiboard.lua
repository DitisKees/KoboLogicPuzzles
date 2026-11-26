local Board = {}
Board.__index = Board

local function integer(value, low, high)
    return type(value) == "number" and value >= low and value <= high and value == math.floor(value)
end

local function array(value, low, high)
    if type(value) ~= "table" or #value < low or #value > high then return false end
    local count = 0
    for key in pairs(value) do
        if not integer(key, 1, #value) then return false end
        count = count + 1
    end
    return count == #value
end

local function index(puzzle, x, y) return y * puzzle.width + x end
local function bridges(mark) return mark == "1" and 1 or mark == "2" and 2 or 0 end

function Board.bridgeTotals(puzzle, marks)
    local placed, clueTotal = 0, 0
    for i = 1, #marks do placed = placed + bridges(marks:sub(i, i)) end
    for _, island in ipairs(puzzle.islands) do clueTotal = clueTotal + island.clue end
    -- Every bridge contributes to the clue at both ends.
    return placed, clueTotal / 2
end

local function topology(puzzle)
    if type(puzzle) ~= "table" or puzzle.schemaVersion ~= 1 or puzzle.game ~= "hashi"
        or not integer(puzzle.width, 20, 50) or puzzle.width % 5 ~= 0 or puzzle.height ~= puzzle.width
        or not array(puzzle.islands, 2, math.floor(puzzle.width * puzzle.height * 0.3))
        or not array(puzzle.edges, 1, 2 * #puzzle.islands) then return end

    local graph = { cells = {}, neighbors = {}, edges = {} }
    local rows, columns, expected = {}, {}, {}
    local previous = -1
    for i, island in ipairs(puzzle.islands) do
        if type(island) ~= "table" or not integer(island.x, 0, puzzle.width - 1)
            or not integer(island.y, 0, puzzle.height - 1) or not integer(island.clue, 1, 8) then return end
        local order = island.x * puzzle.height + island.y
        if order <= previous then return end
        previous = order
        graph.cells[index(puzzle, island.x, island.y)] = i
        graph.neighbors[i] = {}
        if rows[island.y] then expected[#expected + 1] = { rows[island.y], i } end
        if columns[island.x] then expected[#expected + 1] = { columns[island.x], i } end
        rows[island.y], columns[island.x] = i, i
    end
    table.sort(expected, function(a, b) return a[1] < b[1] or (a[1] == b[1] and a[2] < b[2]) end)
    if #expected ~= #puzzle.edges then return end

    local vertical, horizontal = {}, {}
    for i, edge in ipairs(puzzle.edges) do
        local first, second = expected[i][1], expected[i][2]
        if type(edge) ~= "table" or edge.first ~= first - 1 or edge.second ~= second - 1 then return end
        graph.edges[i] = { first = first, second = second, crossing = {} }
        graph.neighbors[first][second], graph.neighbors[second][first] = i, i
        local a, b = puzzle.islands[first], puzzle.islands[second]
        if a.y == b.y then
            for x = a.x + 1, b.x - 1 do horizontal[index(puzzle, x, a.y)] = i end
        else
            for y = a.y + 1, b.y - 1 do vertical[index(puzzle, a.x, y)] = i end
        end
    end
    for cell, first in pairs(horizontal) do
        local second = vertical[cell]
        if second then
            table.insert(graph.edges[first].crossing, second)
            table.insert(graph.edges[second].crossing, first)
        end
    end
    return graph
end

local function compatible(graph, counts)
    for id, edge in ipairs(graph.edges) do
        if counts[id] > 0 then
            for _, other in ipairs(edge.crossing) do
                if counts[other] > 0 then return false end
            end
        end
    end
    return true
end

local function connected(graph, counts)
    local visited, pending, reached = { [1] = true }, { 1 }, 1
    while #pending > 0 do
        for other, id in pairs(graph.neighbors[table.remove(pending)]) do
            if counts[id] > 0 and not visited[other] then
                visited[other] = true
                pending[#pending + 1] = other
                reached = reached + 1
            end
        end
    end
    return reached == #graph.neighbors
end

local function degreesFor(graph, counts)
    local degree = {}
    for i = 1, #graph.neighbors do degree[i] = 0 end
    for i, edge in ipairs(graph.edges) do
        degree[edge.first] = degree[edge.first] + counts[i]
        degree[edge.second] = degree[edge.second] + counts[i]
    end
    return degree
end

local function matchesClues(puzzle, degree)
    for i, island in ipairs(puzzle.islands) do
        if degree[i] ~= island.clue then return false end
    end
    return true
end

function Board.validPuzzle(puzzle)
    local graph = topology(puzzle)
    if not graph or not array(puzzle.solution, #graph.edges, #graph.edges) then return false end
    for _, count in ipairs(puzzle.solution) do
        if not integer(count, 0, 2) then return false end
    end
    return compatible(graph, puzzle.solution) and matchesClues(puzzle, degreesFor(graph, puzzle.solution))
        and connected(graph, puzzle.solution)
end

local function validMarkText(puzzle, marks)
    return type(marks) == "string" and #marks == #puzzle.edges and not marks:find("[^.x12]")
end

function Board.validMarks(puzzle, marks, completed)
    if not validMarkText(puzzle, marks) then return false end
    local graph = topology(puzzle)
    if not graph then return false end
    local counts = {}
    for i = 1, #marks do counts[i] = bridges(marks:sub(i, i)) end
    return compatible(graph, counts) and (not completed or matchesClues(puzzle, degreesFor(graph, counts))
        and connected(graph, counts))
end

function Board.decodeMarks(puzzle, marks, completed)
    if not array(marks, 0, #puzzle.edges) then return end
    local states = {}
    for i = 1, #puzzle.edges do states[i] = "." end
    for _, mark in ipairs(marks) do
        if type(mark) ~= "table" or not integer(mark.edge, 0, #states - 1) or not integer(mark.count, 0, 2)
            or mark.noBridge ~= (mark.count == 0) or states[mark.edge + 1] ~= "." then return end
        states[mark.edge + 1] = mark.noBridge and "x" or tostring(mark.count)
    end
    local decoded = table.concat(states)
    if Board.validMarks(puzzle, decoded, completed) then return decoded end
end

function Board.encodeMarks(marks)
    local encoded = {}
    for i = 1, #marks do
        local mark = marks:sub(i, i)
        if mark ~= "." then
            encoded[#encoded + 1] = { edge = i - 1, count = bridges(mark), noBridge = mark == "x" }
        end
    end
    return encoded
end

function Board:new(puzzle, marks)
    local graph = assert(topology(puzzle), "Invalid Hashi topology")
    local board = setmetatable({ puzzle = puzzle, width = puzzle.width, height = puzzle.height,
        graph = graph, states = {}, counts = {}, undo = {}, redo = {} }, self)
    marks = marks or string.rep(".", #puzzle.edges)
    for i = 1, #marks do
        board.states[i] = marks:sub(i, i)
        board.counts[i] = bridges(board.states[i])
    end
    board:refreshCompletion()
    return board
end

function Board:refreshCompletion()
    self.degree = degreesFor(self.graph, self.counts)
    self.degreesMatch = matchesClues(self.puzzle, self.degree)
    self.complete = self.degreesMatch and compatible(self.graph, self.counts) and connected(self.graph, self.counts)
end

function Board:isComplete() return self.complete end
function Board:marks() return table.concat(self.states) end
function Board:islandAt(row, column) return self.graph.cells[index(self.puzzle, column - 1, row - 1)] end

function Board:apply(changes, reverse)
    for _, change in ipairs(changes) do
        local mark = reverse and change.before or change.after
        self.states[change.edge], self.counts[change.edge] = mark, bridges(mark)
    end
    self:refreshCompletion()
    return changes
end

function Board:remember(changes)
    if #changes == 0 then return end
    if #self.undo == 100 then table.remove(self.undo, 1) end
    self.undo[#self.undo + 1] = changes
    self.redo = {}
    return self:apply(changes)
end

function Board:edit(first, second, mode)
    local id = self.graph.neighbors[first] and self.graph.neighbors[first][second]
    if not id then return end
    local before = self.states[id]
    local after = "."
    if mode == "bridge" then after = before == "1" and "2" or before == "2" and "." or "1"
    elseif mode == "noBridge" then after = before == "x" and "." or "x" end
    if after == before then return end
    if bridges(after) > 0 then
        for _, other in ipairs(self.graph.edges[id].crossing) do
            if self.counts[other] > 0 then return nil, "That bridge would cross an existing bridge." end
        end
    end
    return self:remember({ { edge = id, before = before, after = after } })
end

function Board:restoreCheckpoint(marks)
    if not Board.validMarks(self.puzzle, marks) then return end
    local changes = {}
    for i, before in ipairs(self.states) do
        local after = marks:sub(i, i)
        if before ~= after then changes[#changes + 1] = { edge = i, before = before, after = after } end
    end
    return self:remember(changes)
end

function Board:restart() return self:restoreCheckpoint(string.rep(".", #self.states)) end

function Board:replay(from, to, reverse)
    local changes = table.remove(from)
    if not changes then return end
    to[#to + 1] = changes
    return self:apply(changes, reverse)
end

function Board:undoStroke() return self:replay(self.undo, self.redo, true) end
function Board:redoStroke() return self:replay(self.redo, self.undo, false) end

function Board:checkSolution()
    local errors = 0
    for i, mark in ipairs(self.states) do
        local required = self.puzzle.solution[i]
        if self.counts[i] > required or (mark == "x" and required > 0) then errors = errors + 1 end
    end
    return errors, self.complete
end

return Board
