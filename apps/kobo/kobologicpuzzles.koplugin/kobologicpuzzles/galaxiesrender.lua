local BB = require("ffi/blitbuffer")

local Render = {}

local function round(value) return math.floor(value + 0.5) end

function Render.paint(bb, board, ox, oy, scale, shade, pictureOnly)
    local viewWidth, viewHeight = bb:getWidth(), bb:getHeight()
    bb:paintRect(0, 0, viewWidth, viewHeight, BB.COLOR_WHITE)
    local width, height = board.width, board.height
    local firstCol = math.max(0, math.floor(-ox / scale) - 1)
    local firstRow = math.max(0, math.floor(-oy / scale) - 1)
    local lastCol = math.min(width - 1, math.ceil((viewWidth - ox) / scale))
    local lastRow = math.min(height - 1, math.ceil((viewHeight - oy) / scale))
    local bold = math.max(1, math.floor(scale / 11))
    local grid = BB.COLOR_GRAY_D
    for row = firstRow, lastRow do
        local y, nextY = round(oy + row * scale), round(oy + (row + 1) * scale)
        for col = firstCol, lastCol do
            local x, nextX = round(ox + col * scale), round(ox + (col + 1) * scale)
            local black = shade[row * width + col + 1]
            if black then
                bb:paintRect(x, y, nextX - x, nextY - y, pictureOnly and BB.COLOR_BLACK or BB.COLOR_GRAY_6)
            end
        end
    end
    if pictureOnly then return end

    for row = firstRow, lastRow do
        local y, nextY = round(oy + row * scale), round(oy + (row + 1) * scale)
        for col = firstCol, lastCol do
            local x, nextX = round(ox + col * scale), round(ox + (col + 1) * scale)
            local top = row == 0 or board.state[board:horizontal(row, col)] == "#"
            local left = col == 0 or board.state[board:vertical(row, col)] == "#"
            if top or scale >= 4 then
                bb:paintRect(x, y, nextX - x + 1, top and bold or 1, top and BB.COLOR_BLACK or grid)
            end
            if left or scale >= 4 then
                bb:paintRect(x, y, left and bold or 1, nextY - y + 1, left and BB.COLOR_BLACK or grid)
            end
            if row > 0 and board.state[board:horizontal(row, col)] == "x" and scale >= 9 then
                bb:paintRect(round((x + nextX) / 2) - 1, y - 2, 3, 5, BB.COLOR_GRAY_5)
            end
            if col > 0 and board.state[board:vertical(row, col)] == "x" and scale >= 9 then
                bb:paintRect(x - 2, round((y + nextY) / 2) - 1, 5, 3, BB.COLOR_GRAY_5)
            end
        end
    end

    local x2, y2 = round(ox + width * scale), round(oy + height * scale)
    bb:paintRect(x2, round(oy), bold, y2 - round(oy) + bold, BB.COLOR_BLACK)
    bb:paintRect(round(ox), y2, x2 - round(ox) + bold, bold, BB.COLOR_BLACK)
    local radius = math.max(1, round(scale * 0.16))
    for row = firstRow, lastRow do
        for _, center in ipairs(board.dotsByRow[row] or {}) do
            local x, y = round(ox + center.x * scale / 2), round(oy + center.y * scale / 2)
            if x + radius >= 0 and x - radius < viewWidth and y + radius >= 0 and y - radius < viewHeight then
                bb:paintCircle(x, y, radius + 1, BB.COLOR_WHITE)
                bb:paintCircle(x, y, radius, BB.COLOR_BLACK)
                if not center.black then
                    if radius > 1 then bb:paintCircle(x, y, radius - 1, BB.COLOR_WHITE)
                    else bb:paintRect(x, y, 1, 1, BB.COLOR_WHITE) end
                end
            end
        end
    end
end

return Render
