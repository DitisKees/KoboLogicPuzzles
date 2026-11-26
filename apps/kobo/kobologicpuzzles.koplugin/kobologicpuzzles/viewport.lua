local Viewport = {}
Viewport.__index = Viewport
Viewport.defaultZoom = 2
Viewport.minZoom = -1
Viewport.maxZoom = 4
Viewport.defaultClueLimit = 5
Viewport.maxClueLimit = 50

-- Keep the original zoom indexes so saved views open at the same scale.
local cellSizes = { [-1] = 12, [0] = 17, 23, 29, 36, 46 }

local function clamp(value, first, last)
    return math.max(first, math.min(value, last))
end

local function clueSlots(lines, limit, available)
    local longest = 1
    for _, line in ipairs(lines) do longest = math.max(longest, #line) end
    local slots = math.min(longest, limit, available)
    local ellipsis = longest > slots and available > 1
    if ellipsis then slots = math.min(slots + 1, available) end
    return slots, ellipsis
end

function Viewport:new(width, height, puzzle, zoom, clueLimit)
    local scale = math.min(width, height) / 720
    local pad = math.max(5, math.floor(9 * scale))
    local button = math.max(32, math.floor(42 * scale))
    local title = math.max(26, math.floor(36 * scale))
    local status = math.max(22, math.floor(29 * scale))
    local cellSize = cellSizes[zoom]
    local step = math.max(17, math.floor(math.min(23, cellSize + 2) * scale))
    local cell = math.max(1, math.floor(cellSize * scale))
    local headerY = pad + button + title
    local footerY = height - 2 * (button + pad)
    clueLimit = clueLimit or Viewport.defaultClueLimit
    local sidePad = puzzle.game == "hashi" and 2 * pad or pad
    local availableW, availableH = width - 2 * sidePad - 1, footerY - status - headerY - 1
    -- Preserve room for at least four grid cells where the screen allows it.
    local maxLeft = math.max(1, math.floor((availableW - math.min(4, puzzle.width) * cell) / step))
    local maxTop = math.max(1, math.floor((availableH - math.min(4, puzzle.height) * cell) / step))
    local leftSlots, leftEllipsis, topSlots, topEllipsis = 0, false, 0, false
    if puzzle.game ~= "hashi" and puzzle.game ~= "fill-a-pix" and puzzle.game ~= "slitherlink" then
        leftSlots, leftEllipsis = clueSlots(puzzle.rowClues, clueLimit, maxLeft)
        topSlots, topEllipsis = clueSlots(puzzle.columnClues, clueLimit, maxTop)
    end
    local gridX, gridY = sidePad + leftSlots * step, headerY + topSlots * step
    local columns = clamp(math.floor((availableW - leftSlots * step) / cell), 1, puzzle.width)
    local rows = clamp(math.floor((availableH - topSlots * step) / cell), 1, puzzle.height)

    return setmetatable({
        width = width, height = height, puzzle = puzzle, zoom = zoom,
        scale = scale, pad = pad, button = button, title = title, status = status,
        headerY = headerY, footerY = footerY, step = step,
        leftSlots = leftSlots, topSlots = topSlots,
        leftEllipsis = leftEllipsis, topEllipsis = topEllipsis,
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
    local fit = math.min(
        (self.width - 2 * self.pad - 2) / self.puzzle.width,
        (self.footerY - self.headerY - 2 * self.pad - self.status) / self.puzzle.height)
    local cell = fit < 1 and fit or math.floor(fit)
    return math.floor((self.width - cell * self.puzzle.width) / 2),
        self.headerY + self.pad, cell
end

return Viewport
