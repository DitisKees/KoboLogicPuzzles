local BB = require("ffi/blitbuffer")
local Device = require("device")
local RenderText = require("ui/rendertext")

local Render = {}

local function line(bb, x1, y1, x2, y2, width, color)
    local dx, dy = x2 - x1, y2 - y1
    local first, last, margin = 0, 1, width
    local function clip(origin, direction, maximum)
        if direction == 0 then return origin >= -margin and origin <= maximum + margin end
        local low = (-margin - origin) / direction
        local high = (maximum + margin - origin) / direction
        if low > high then low, high = high, low end
        first, last = math.max(first, low), math.min(last, high)
        return first <= last
    end
    if not clip(x1, dx, bb:getWidth()) or not clip(y1, dy, bb:getHeight()) then return end
    local steps = math.max(1, math.ceil(math.max(math.abs(dx), math.abs(dy))))
    local half = math.floor(width / 2)
    for step = math.ceil(first * steps), math.floor(last * steps) do
        local x = math.floor(x1 + dx * step / steps + 0.5) - half
        local y = math.floor(y1 + dy * step / steps + 0.5) - half
        bb:paintRect(x, y, width, width, color)
    end
end

function Render.paint(bb, board, originX, originY, scale, face, thumbnail)
    bb:fill(BB.COLOR_WHITE)
    local puzzle = board.puzzle
    local detail = board.edgeSize * scale
    local screen = Device.screen
    local thick = math.min(screen:scaleBySize(5), math.max(2, math.floor(detail / 12)))
    local dot = math.min(screen:scaleBySize(4), math.max(2, math.floor(detail / 14)))
    local cross = math.min(screen:scaleBySize(7), math.max(3, math.floor(detail / 8)))
    for i, edge in ipairs(puzzle.edges) do
        local mark = board.state[i]
        local a, b = puzzle.vertices[edge.first + 1], puzzle.vertices[edge.second + 1]
        local x1, y1 = originX + a.x * scale, originY + a.y * scale
        local x2, y2 = originX + b.x * scale, originY + b.y * scale
        if mark == "#" or not thumbnail then
            line(bb, x1, y1, x2, y2, mark == "#" and (thumbnail and 1 or thick) or 1,
                mark == "#" and BB.COLOR_BLACK or BB.COLOR_GRAY_9)
        end
        if mark == "x" and face then
            local x, y = (x1 + x2) / 2, (y1 + y2) / 2
            local stroke = math.max(1, math.floor(dot / 2))
            line(bb, x - cross, y - cross, x + cross, y + cross, stroke, BB.COLOR_BLACK)
            line(bb, x - cross, y + cross, x + cross, y - cross, stroke, BB.COLOR_BLACK)
        end
    end
    if not thumbnail then
        for _, vertex in ipairs(puzzle.vertices) do
            local x, y = math.floor(originX + vertex.x * scale + 0.5), math.floor(originY + vertex.y * scale + 0.5)
            bb:paintRect(x - math.floor(dot / 2), y - math.floor(dot / 2), dot, dot, BB.COLOR_GRAY_5)
        end
    end
    if not face then return end
    local w, h = bb:getWidth(), bb:getHeight()
    for _, polygon in ipairs(puzzle.faces) do
        local clue = polygon.clue
        if clue then
            local x, y = math.floor(originX + polygon.x * scale + 0.5), math.floor(originY + polygon.y * scale + 0.5)
            if x >= 0 and x < w and y >= 0 and y < h then
                local drawn, undecided = 0, 0
                for _, index in ipairs(polygon.edges) do
                    local mark = board.state[index + 1]
                    if mark == "#" then drawn = drawn + 1
                    elseif mark == "." then undecided = undecided + 1 end
                end
                local conflict = drawn > clue or drawn + undecided < clue
                local label = tostring(clue)
                local size = RenderText:sizeUtf8Text(0, false, face, label, true)
                local left = x - math.floor(size.x / 2)
                local top = y - math.ceil((size.y_top + size.y_bottom) / 2)
                local labelHeight = math.max(1, size.y_top + size.y_bottom)
                if conflict then
                    bb:paintRect(left - 2, top - 2, size.x + 4, labelHeight + 4, BB.COLOR_BLACK)
                end
                RenderText:renderUtf8Text(bb, left, y + math.floor((size.y_top - size.y_bottom) / 2), face, label,
                    true, false, conflict and BB.COLOR_WHITE or drawn == clue and BB.COLOR_GRAY_6 or BB.COLOR_BLACK)
            end
        end
    end
end

return Render
