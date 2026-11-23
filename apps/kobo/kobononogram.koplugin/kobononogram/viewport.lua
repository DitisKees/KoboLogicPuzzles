local Viewport = {}
Viewport.__index = Viewport
Viewport.defaultZoom = 2
Viewport.zoomCount = 4

local cellSizes = { 23, 29, 36, 46 }

local function clamp(value, first, last)
    return math.max(first, math.min(value, last))
end

local function clueSlots(lines)
    local slots = 1
    for _, line in ipairs(lines) do slots = math.max(slots, #line) end
    return math.min(5, slots)
end

function Viewport:new(width, height, puzzle, zoom)
    local scale = math.min(width, height) / 720
    local pad = math.max(5, math.floor(9 * scale))
    local button = math.max(32, math.floor(42 * scale))
    local title = math.max(26, math.floor(36 * scale))
    local status = math.max(22, math.floor(29 * scale))
    local step = math.max(17, math.floor(23 * scale))
    local cell = math.floor(cellSizes[zoom] * scale)
    local leftSlots, topSlots = clueSlots(puzzle.rowClues), clueSlots(puzzle.columnClues)
    local headerY = pad + button + title
    local footerY = height - 2 * (button + pad)
    local gridX, gridY = pad + leftSlots * step, headerY + topSlots * step
    local columns = math.min(puzzle.width, math.floor((width - pad - gridX - 1) / cell))
    local rows = math.min(puzzle.height, math.floor((footerY - status - gridY - 1) / cell))

    return setmetatable({
        width = width, height = height, puzzle = puzzle, zoom = zoom,
        scale = scale, pad = pad, button = button, title = title, status = status,
        headerY = headerY, footerY = footerY, step = step,
        leftSlots = leftSlots, topSlots = topSlots,
        gridX = gridX, gridY = gridY, columns = columns, rows = rows, cell = cell,
    }, self)
end

function Viewport:origin(row, column)
    return clamp(row, 1, self.puzzle.height - self.rows + 1),
        clamp(column, 1, self.puzzle.width - self.columns + 1)
end

function Viewport:cellAt(x, y, firstRow, firstColumn, extend)
    local row = math.floor((y - self.gridY) / self.cell) + 1
    local column = math.floor((x - self.gridX) / self.cell) + 1
    if extend then
        row, column = clamp(row, 1, self.rows), clamp(column, 1, self.columns)
    elseif row < 1 or row > self.rows or column < 1 or column > self.columns then
        return nil
    end
    return firstRow + row - 1, firstColumn + column - 1
end

function Viewport:overview()
    local cell = math.max(1, math.floor(math.min(
        (self.width - 2 * self.pad - 2) / self.puzzle.width,
        (self.footerY - self.headerY - 2 * self.pad - self.status) / self.puzzle.height)))
    return math.floor((self.width - cell * self.puzzle.width) / 2),
        self.headerY + self.pad, cell
end

return Viewport
