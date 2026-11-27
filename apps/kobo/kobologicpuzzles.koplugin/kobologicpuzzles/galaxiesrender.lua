local BB = require("ffi/blitbuffer")

local Render = {}

local function round(value) return math.floor(value + 0.5) end

local function paintBoard(bb, board, ox, oy, scale, shade, pictureOnly, left, top)
    local viewWidth, viewHeight = bb:getWidth(), bb:getHeight()
    bb:fill(BB.COLOR_WHITE)
    local width, height = board.width, board.height
    local firstCol = math.max(0, math.floor((left - ox) / scale) - 1)
    local firstRow = math.max(0, math.floor((top - oy) / scale) - 1)
    local lastCol = math.min(width - 1, math.ceil((left + viewWidth - ox) / scale))
    local lastRow = math.min(height - 1, math.ceil((top + viewHeight - oy) / scale))
    local xs, ys = {}, {}
    -- Round in board-view coordinates so a clipped repaint lands on the same pixels.
    for col = firstCol, lastCol + 1 do xs[col] = round(ox + col * scale) - left end
    for row = firstRow, lastRow + 1 do ys[row] = round(oy + row * scale) - top end

    local fill = pictureOnly and BB.COLOR_BLACK or BB.COLOR_GRAY_6
    for row = firstRow, lastRow do
        for col = firstCol, lastCol do
            if shade[row * width + col + 1] then
                bb:paintRect(xs[col], ys[row], xs[col + 1] - xs[col], ys[row + 1] - ys[row], fill)
            end
        end
    end
    if pictureOnly then return end

    local guideWidth = scale >= 16 and 2 or 1
    local wallWidth = math.max(scale >= 5 and guideWidth + 1 or 1, math.floor(scale / 11))
    if scale >= 2 then
        for row = firstRow, lastRow do
            local y, nextY = ys[row], ys[row + 1]
            for col = firstCol, lastCol do
                local x, nextX = xs[col], xs[col + 1]
                local color = shade[row * width + col + 1] and BB.COLOR_LIGHT_GRAY or BB.COLOR_GRAY_7
                bb:paintRect(x, y, nextX - x, guideWidth, color)
                bb:paintRect(x, y, guideWidth, nextY - y, color)
                if scale >= 9 and row > 0 and board.state[board:horizontal(row, col)] == "x" then
                    bb:paintRect(round((x + nextX) / 2) - 1, y - 2, 3, 5, BB.COLOR_GRAY_4)
                end
                if scale >= 9 and col > 0 and board.state[board:vertical(row, col)] == "x" then
                    bb:paintRect(x - 2, round((y + nextY) / 2) - 1, 5, 3, BB.COLOR_GRAY_4)
                end
            end
        end
    end

    local radius = math.max(1, round(scale * 0.16))
    local halo = radius + 1
    for row = firstRow, lastRow do
        for _, center in ipairs(board.dotsByRow[row] or {}) do
            local x = round(ox + center.x * scale / 2) - left
            local y = round(oy + center.y * scale / 2) - top
            if x + halo >= 0 and x - halo < viewWidth and y + halo >= 0 and y - halo < viewHeight then
                bb:paintCircle(x, y, halo, BB.COLOR_WHITE)
                bb:paintCircle(x, y, radius, BB.COLOR_BLACK)
                if not center.black then
                    if radius > 1 then bb:paintCircle(x, y, radius - 1, BB.COLOR_WHITE)
                    else bb:setPixelClamped(x, y, BB.COLOR_WHITE) end
                end
            end
        end
    end

    -- Draw walls over grid guides and clue halos so intersections stay solid.
    for row = firstRow, lastRow do
        local y, nextY = ys[row], ys[row + 1]
        for col = firstCol, lastCol do
            local x, nextX = xs[col], xs[col + 1]
            if row == 0 or board.state[board:horizontal(row, col)] == "#" then
                bb:paintRect(x, y, nextX - x + 1, wallWidth, BB.COLOR_BLACK)
            end
            if col == 0 or board.state[board:vertical(row, col)] == "#" then
                bb:paintRect(x, y, wallWidth, nextY - y + 1, BB.COLOR_BLACK)
            end
        end
    end
    local x1, y1 = round(ox) - left, round(oy) - top
    local x2, y2 = round(ox + width * scale) - left, round(oy + height * scale) - top
    bb:paintRect(x2, y1, wallWidth, y2 - y1 + wallWidth, BB.COLOR_BLACK)
    bb:paintRect(x1, y2, x2 - x1 + wallWidth, wallWidth, BB.COLOR_BLACK)
end

function Render.paint(bb, board, ox, oy, scale, shade, pictureOnly, clip)
    if not clip then
        paintBoard(bb, board, ox, oy, scale, shade, pictureOnly, 0, 0)
        return
    end

    -- A blitbuffer viewport shares the parent stride. Render fills into owned rows.
    local patch = BB.new(clip.w, clip.h, bb:getType())
    patch:setInverse(bb:getInverse())
    paintBoard(patch, board, ox, oy, scale, shade, pictureOnly, clip.x, clip.y)
    bb:blitFrom(patch, clip.x, clip.y)
    patch:free()
end

return Render
