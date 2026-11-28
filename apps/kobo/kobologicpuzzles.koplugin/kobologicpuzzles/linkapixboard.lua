local bit = require("bit")
local EdgeHistory = require("kobologicpuzzles/edgehistory")

local Link = setmetatable({}, { __index = EdgeHistory })
Link.__index = Link
local masks = "0123456789abcdef"
local steps = { 1, 2, 4, 8 }
local opposite = { 3, 4, 1, 2 }

local function integer(n, min, max)
    return type(n) == "number" and n >= min and n <= max and n == math.floor(n)
end

local function neighbor(i, side, width, height)
    if side == 1 then return i > width and i - width or nil end
    if side == 2 then return i % width ~= 0 and i + 1 or nil end
    if side == 3 then return i <= width * (height - 1) and i + width or nil end
    return (i - 1) % width ~= 0 and i - 1 or nil
end

local function direction(i, j, width, height)
    for side = 1, 4 do
        if neighbor(i, side, width, height) == j then return side end
    end
end

function Link.validPuzzle(puzzle)
    local w, h = puzzle.width, puzzle.height
    if type(puzzle.title) ~= "string" or not integer(w, 1, 200) or not integer(h, 1, 200)
        or type(puzzle.clues) ~= "table" or #puzzle.clues == 0 or #puzzle.clues > w * h then return false end
    local counts, used = {}, 0
    local previous = -1
    for i = 1, #puzzle.clues do
        local clue = puzzle.clues[i]
        if type(clue) ~= "table" or not integer(clue.cell, previous + 1, w * h - 1)
            or not integer(clue.length, 1, w * h) or clue.color ~= nil then return false end
        previous = clue.cell
        counts[clue.length] = (counts[clue.length] or 0) + 1
        used = used + (clue.length == 1 and 2 or clue.length)
    end
    if used > 2 * w * h then return false end
    for length, count in pairs(counts) do if length > 1 and count % 2 ~= 0 then return false end end
    return true
end

local function clueMap(puzzle)
    local clues = {}
    for _, clue in ipairs(puzzle.clues) do clues[clue.cell + 1] = clue.length end
    return clues
end

local function readPaths(puzzle, cells, clues)
    local width, height = puzzle.width, puzzle.height
    local bits, seen, paths = {}, {}, {}
    for i, char in ipairs(cells) do
        local length = clues[i]
        if char == "." or char == "x" then
            if char == "x" and length then return end
            bits[i] = 0
        else
            local mask = masks:find(char, 1, true)
            if not mask or mask < 2 then return end
            mask = mask - 1
            local degree = 0
            for _, step in ipairs(steps) do if bit.band(mask, step) ~= 0 then degree = degree + 1 end end
            if degree > 2 or length and (length == 1 or degree ~= 1) then return end
            bits[i] = mask
        end
    end
    for i, mask in ipairs(bits) do
        if mask > 0 then
            for side, step in ipairs(steps) do
                if bit.band(mask, step) ~= 0 then
                    local nextCell = neighbor(i, side, width, height)
                    if not nextCell or bit.band(bits[nextCell], steps[opposite[side]]) == 0 then return end
                end
            end
        end
    end
    local pairsNeeded, completed = 0, 0
    for _, clue in ipairs(puzzle.clues) do
        local i, length = clue.cell + 1, clue.length
        if length > 1 then
            pairsNeeded = pairsNeeded + 0.5
            if not seen[i] and bits[i] > 0 then
                local path, previous, current = {}, nil, i
                while current do
                    if seen[current] or #path == length then return end
                    seen[current] = true
                    path[#path + 1] = current
                    local nextCell
                    for side, step in ipairs(steps) do
                        if bit.band(bits[current], step) ~= 0 then
                            local next = neighbor(current, side, width, height)
                            if next ~= previous then nextCell = next end
                        end
                    end
                    previous, current = current, nextCell
                end
                if clues[previous] then
                    if clues[previous] ~= length or #path ~= length then return end
                    completed = completed + 1
                elseif #path == length then return end
                paths[#paths + 1] = path
            end
        end
    end
    for i, mask in ipairs(bits) do if mask > 0 and not seen[i] then return end end
    return paths, pairsNeeded, completed
end

local function mark(path, offset, width, height)
    local i, mask = path[offset], 0
    if offset > 1 then mask = steps[direction(i, path[offset - 1], width, height)] end
    if offset < #path then mask = mask + steps[direction(i, path[offset + 1], width, height)] end
    return masks:sub(mask + 1, mask + 1)
end

function Link.validSolution(puzzle, paths)
    if type(paths) ~= "table" then return false end
    local w, h = puzzle.width, puzzle.height
    local cells, clues, used = {}, clueMap(puzzle), {}
    for i = 1, w * h do cells[i] = "." end
    for _, wirePath in ipairs(paths) do
        if type(wirePath) ~= "table" or #wirePath < 2 or #wirePath > w * h then return false end
        local path = {}
        for offset, cell in ipairs(wirePath) do
            if not integer(cell, 0, w * h - 1) or used[cell]
                or offset > 1 and not direction(path[offset - 1], cell + 1, w, h) then return false end
            used[cell], path[offset] = true, cell + 1
        end
        for offset, i in ipairs(path) do cells[i] = mark(path, offset, w, h) end
    end
    local valid, required, completed = readPaths(puzzle, cells, clues)
    return valid ~= nil and required == completed
end

function Link.validMarks(puzzle, marks, completed)
    if type(marks) ~= "string" or #marks ~= puzzle.width * puzzle.height then return false end
    local cells = {}
    for i = 1, #marks do cells[i] = marks:sub(i, i) end
    local paths, required, done = readPaths(puzzle, cells, clueMap(puzzle))
    return paths ~= nil and (not completed or required == done)
end

function Link.initialMarks(puzzle) return string.rep(".", puzzle.width * puzzle.height) end

function Link:new(puzzle, marks)
    local cells = {}
    marks = marks or self.initialMarks(puzzle)
    for i = 1, #marks do cells[i] = marks:sub(i, i) end
    local board = setmetatable({
        puzzle = puzzle, width = puzzle.width, height = puzzle.height, clues = clueMap(puzzle),
        state = cells, cells = cells, undo = {}, redo = {}, rows = {}, columns = {},
    }, self)
    board:rebuild()
    return board
end

function Link:rebuild()
    local paths, required, completed = readPaths(self.puzzle, self.cells, self.clues)
    self.paths, self.required, self.confirmed = {}, required, completed
    self.clueStates = {}
    for i, length in pairs(self.clues) do if length == 1 then self.clueStates[i] = "ink" end end
    for _, path in ipairs(paths) do self:applyPath(path) end
end

function Link:applyPath(path)
    local closed = #path >= 2 and self.clues[path[#path]] ~= nil
    for offset, i in ipairs(path) do
        self.paths[i] = path
        self.clueStates[i] = closed and "ink" or "path"
        self.cells[i] = mark(path, offset, self.width, self.height)
    end
    return closed and 1 or 0
end

function Link:index(row, col) return (row - 1) * self.width + col end
function Link:get(row, col) return self.cells[self:index(row, col)] end
function Link:isComplete() return self.required == self.confirmed end
function Link:getOverviewMark(row, col)
    local i = self:index(row, col)
    return self.clueStates[i] == "ink" and "#" or self.clueStates[i] == "path" and "x" or "."
end

function Link:canStart(i)
    local path = self.paths[i]
    return (self.clues[i] or 0) > 1 or path and path[#path] == i
end

local function copyPath(path, reverse)
    local copy = {}
    for i = 1, #path do copy[i] = path[reverse and #path + 1 - i or i] end
    return copy
end

function Link:beginStroke(row, col)
    local i = self:index(row, col)
    if not self:canStart(i) then return false end
    local original = self.paths[i]
    local path = original and copyPath(original, original[1] == i and self.clues[original[#original]] ~= nil) or { i }
    self.stroke = { before = self:marks(), working = original, path = path, touched = {},
        replace = original and original[1] == i and not self.clues[original[#original]] }
    self.feedback = nil
    return false
end

function Link:replacePath(old, new)
    if old then
        if self.clues[old[#old]] then self.confirmed = self.confirmed - 1 end
        for _, i in ipairs(old) do
            self.paths[i], self.clueStates[i], self.cells[i] = nil, nil, "."
        end
    end
    if new and #new > 1 then self.confirmed = self.confirmed + self:applyPath(new) end
end

function Link:extendStroke(row, col)
    local stroke = self.stroke
    if not stroke then return end
    local path = stroke.path
    local endCell = stroke.replace and path[1] or path[#path]
    local endRow, endCol = math.floor((endCell - 1) / self.width) + 1, (endCell - 1) % self.width + 1
    if row ~= endRow and col ~= endCol then self.feedback = "Move along a row or column" return end
    if row == endRow and col == endCol then return end
    local step = row ~= endRow and (row > endRow and self.width or -self.width) or (col > endCol and 1 or -1)
    local target, changed = self:index(row, col), false
    local required = self.clues[path[1]]
    while endCell ~= target do
        local nextCell = endCell + step
        local clue, owner = self.clues[nextCell], self.paths[nextCell]
        local offset = not stroke.replace and self.pathOffset(path, nextCell)
        self.feedback = nil
        if offset then
            path = copyPath(path)
            for i = #path, offset + 1, -1 do path[i] = nil end
        else
            local length = stroke.replace and 2 or #path + 1
            if owner and owner ~= stroke.working then self.feedback = "That cell belongs to another path"
            elseif self.cells[nextCell] == "x" then self.feedback = "That cell is marked white"
            elseif clue and clue ~= required then self.feedback = "Choose a matching clue"
            elseif clue and length ~= required then self.feedback = "The path must use exactly " .. required .. " cells"
            elseif not clue and length >= required then self.feedback = "End on a matching clue"
            else
                path = stroke.replace and { path[1] } or copyPath(path)
                path[#path + 1] = nextCell
            end
            if self.feedback then break end
        end
        stroke.replace = false
        if stroke.working then for _, i in ipairs(stroke.working) do stroke.touched[i] = true end end
        for _, i in ipairs(path) do stroke.touched[i] = true end
        self:replacePath(stroke.working, path)
        stroke.working = #path > 1 and path or nil
        stroke.path, endCell, changed = path, nextCell, true
        if clue and #path > 1 then break end
    end
    if changed then
        local r1, c1, r2, c2 = self.height, self.width, 1, 1
        for i in pairs(stroke.touched) do
            local r, c = math.floor((i - 1) / self.width) + 1, (i - 1) % self.width + 1
            r1, c1, r2, c2 = math.min(r1, r), math.min(c1, c), math.max(r2, r), math.max(c2, c)
        end
        return r1, c1, r2, c2
    end
end

function Link.pathOffset(path, cell)
    for i, current in ipairs(path) do if current == cell then return i end end
end

local function addCoordinates(board, changes, cells)
    for i in pairs(cells) do
        changes[#changes + 1] = { row = math.floor((i - 1) / board.width) + 1, column = (i - 1) % board.width + 1 }
    end
    return changes
end

function Link:apply(changes, key)
    local old = self.clueStates
    EdgeHistory.apply(self, changes, key)
    self:rebuild()
    local dirty = {}
    local repaint = {}
    for _, change in ipairs(changes) do dirty[change.edge] = true end
    for i, style in pairs(old) do if style ~= self.clueStates[i] then dirty[i] = true end end
    for i, style in pairs(self.clueStates) do if style ~= old[i] then dirty[i] = true end end
    return addCoordinates(self, repaint, dirty)
end

function Link:finishStroke()
    local stroke = self.stroke
    self.stroke = nil
    if not stroke then return end
    local changes = {}
    for i in pairs(stroke.touched) do
        local prior, after = stroke.before:sub(i, i), self.cells[i]
        if prior ~= after then changes[#changes + 1] = { edge = i, before = prior, after = after } end
    end
    local painted = self:commit(changes)
    if painted then return addCoordinates(self, painted, stroke.touched) end
end

function Link:cancelStroke()
    if not self.stroke then return end
    local before = self.stroke.before
    for i in pairs(self.stroke.touched) do self.cells[i] = before:sub(i, i) end
    self.stroke, self.feedback = nil, nil
    self:rebuild()
end

function Link:clearPath(i)
    local path = self.paths[i]
    if not path then return end
    local changes = {}
    for _, cell in ipairs(path) do
        changes[#changes + 1] = { edge = cell, before = self.cells[cell], after = "." }
    end
    return self:commit(changes)
end

function Link:toggleWhite(i)
    if self.clues[i] or self.paths[i] then return end
    return self:setMark(i, self.cells[i] == "x" and "." or "x")
end

function Link.progress(puzzle, marks)
    if marks == "" then return 0, 1 end
    local cells = {}
    for i = 1, #marks do cells[i] = marks:sub(i, i) end
    local _, required, done = readPaths(puzzle, cells, clueMap(puzzle))
    return done or 0, math.max(1, required or 0)
end

return Link
