local BB = require("ffi/blitbuffer")
local Geom = require("ui/geometry")
local RenderText = require("ui/rendertext")

local Render = {}

function Render.clip(a, b)
    local x, y = math.max(a.x, b.x), math.max(a.y, b.y)
    local right, bottom = math.min(a.x + a.w, b.x + b.w), math.min(a.y + a.h, b.y + b.h)
    if right > x and bottom > y then return Geom:new{ x = x, y = y, w = right - x, h = bottom - y } end
end

function Render.islandRect(island, x, y, cell)
    return Geom:new{ x = x + island.x * cell, y = y + island.y * cell, w = cell, h = cell }
end

function Render.edgeRect(board, edge, x, y, cell)
    local a, b = board.puzzle.islands[edge.first], board.puzzle.islands[edge.second]
    return Geom:new{ x = x + a.x * cell, y = y + a.y * cell,
        w = (b.x - a.x + 1) * cell, h = (b.y - a.y + 1) * cell }
end

local function stroke(bb, x1, y1, x2, y2, offset, width, color)
    if y1 == y2 then
        bb:paintRect(x1, y1 + offset, x2 - x1 + 1, width, color)
    else
        bb:paintRect(x1 + offset, y1, width, y2 - y1 + 1, color)
    end
end

local function dots(bb, x1, y1, x2, y2, cell, color, crossed)
    local horizontal = y1 == y2
    local first, last = horizontal and x1 or y1, horizontal and x2 or y2
    local size, step = math.max(1, math.floor(cell / 12)), math.max(4, math.floor(cell / 3))
    -- Align dots to the segment so clipped redraws match the full view.
    local visible = math.max(1 - size, first)
    local start = first + math.ceil((visible - first) / step) * step
    local stop = math.min(last, horizontal and bb:getWidth() or bb:getHeight())
    for point = start, stop, step do
        bb:paintRect(horizontal and point or x1, horizontal and y1 or point, size, size, color)
    end
    if crossed then
        local x, y = math.floor((x1 + x2) / 2), math.floor((y1 + y2) / 2)
        local radius = math.max(2, math.floor(cell / 6))
        for offset = -radius, radius do
            bb:paintRect(x + offset, y + offset, size, size, BB.COLOR_BLACK)
            bb:paintRect(x + offset, y - offset, size, size, BB.COLOR_BLACK)
        end
    end
end

function Render.paint(canvas, board, originX, originY, cell, region, face, selected, guides)
    -- KOReader's fill shortcuts can write over a viewport's full stride.
    local bb = BB.new(region.w, region.h, BB.TYPE_BB8)
    bb:setInverse(canvas:getInverse())
    bb:fill(BB.COLOR_WHITE)
    local half = math.floor(cell / 2)
    local x, y = originX - region.x + half, originY - region.y + half
    local width, spacing = math.max(1, math.floor(cell / 14)), math.max(2, math.floor(cell / 8))
    for id, edge in ipairs(board.graph.edges) do
        local mark = board.states[id]
        local hint = guides and (edge.first == selected or edge.second == selected)
        if (mark ~= "." or hint) and Render.clip(Render.edgeRect(board, edge, originX, originY, cell), region) then
            local a, b = board.puzzle.islands[edge.first], board.puzzle.islands[edge.second]
            local x1, y1, x2, y2 = x + a.x * cell, y + a.y * cell, x + b.x * cell, y + b.y * cell
            if mark == "2" and cell > 4 then
                stroke(bb, x1, y1, x2, y2, -spacing, width, BB.COLOR_BLACK)
                stroke(bb, x1, y1, x2, y2, spacing - width + 1, width, BB.COLOR_BLACK)
            elseif mark == "1" or mark == "2" then
                stroke(bb, x1, y1, x2, y2, 0, width, BB.COLOR_BLACK)
            elseif cell > 4 then
                dots(bb, x1, y1, x2, y2, cell, mark == "x" and BB.COLOR_GRAY_5 or BB.COLOR_GRAY_8, mark == "x")
            end
        end
    end

    for id, island in ipairs(board.puzzle.islands) do
        if Render.clip(Render.islandRect(island, originX, originY, cell), region) then
            local centerX, centerY = x + island.x * cell, y + island.y * cell
            local done = board.degree[id] == island.clue
            local outline = done and BB.COLOR_GRAY_6 or BB.COLOR_BLACK
            if not face then
                local mark = math.max(1, math.floor(cell / 2))
                bb:paintRect(centerX - math.floor(mark / 2), centerY - math.floor(mark / 2), mark, mark, outline)
            else
                local radius = math.max(2, math.floor(cell * 0.36))
                local conflict = board.degree[id] > island.clue
                local fill = conflict and BB.COLOR_BLACK or (done and BB.COLOR_GRAY_D or BB.COLOR_WHITE)
                bb:paintCircle(centerX, centerY, radius, fill)
                bb:setPixelClamped(centerX, centerY, fill)
                bb:paintCircle(centerX, centerY, radius, outline, 1)
                if selected == id then bb:paintCircle(centerX, centerY, radius + 2, BB.COLOR_BLACK, 1) end
                local label = tostring(island.clue)
                local size = RenderText:sizeUtf8Text(0, false, face, label, false)
                RenderText:renderUtf8Text(bb, centerX - math.floor(size.x / 2),
                    centerY + math.floor((size.y_top - size.y_bottom) / 2), face, label,
                    false, false, conflict and BB.COLOR_WHITE or outline)
                if done then
                    local strike = math.min(radius - 1, math.ceil(size.x / 2) + 1)
                    bb:paintRect(centerX - strike, centerY, 2 * strike + 1, math.max(1, math.floor(cell / 24)), outline)
                end
            end
        end
    end
    canvas:blitFrom(bb, region.x, region.y)
    bb:free()
end

return Render
