local BB = require("ffi/blitbuffer")
local FillAPixGame = require("kobologicpuzzles/fillapixgame")
local Game = require("kobologicpuzzles/game")
local InfoMessage = require("ui/widget/infomessage")
local UIManager = require("ui/uimanager")

local PlacementGame = FillAPixGame:extend{ defaultMode = "cycle", clueSample = "00" }

function PlacementGame:init()
    self.tents = self.board.puzzle.game == "tents"
    self.buttonLabels = { fill = self.tents and "Tent" or "Ship", cross = self.tents and "Grass" or "Water",
        clues = self.tents and "Rules" or "Fleet", restart = "Restart" }
    FillAPixGame.init(self)
end

function PlacementGame:paintGrid(bb, frame, full)
    local board, layout = self.board, self.layout
    if board.clueStates then
        for row = self.row, self.row + layout.rows - 1 do
            for col = self.column, self.column + layout.columns - 1 do
                local i = (row - 1) * board.width + col
                board.clueStates[i] = board.cells[i] == "#" and board:shipShape(i) or nil
            end
        end
    end
    Game.paintGrid(self, bb, frame, full)
end

function PlacementGame:preview(firstRow, firstCol, lastRow, lastCol, refreshType)
    if firstRow and not self.tents and not refreshType then
        firstRow, firstCol, lastRow, lastCol = firstRow - 1, firstCol - 1, lastRow + 1, lastCol + 1
    end
    Game.preview(self, firstRow, firstCol, lastRow, lastCol, refreshType)
end

function PlacementGame:paintCell(bb, mark, row, col, full, index, shape)
    local clue = self.board.puzzle.givens:sub(index, index)
    Game.paintCell(self, bb, ".", row, col, full)
    local l = self.layout
    local cell = l.cell
    if cell < 8 then return end
    local x, y = l.gridX + col * cell, l.gridY + row * cell
    local center = math.floor(cell / 2)
    local cx, cy = x + center, y + center
    local radius = math.max(2, math.floor(cell * 0.25))
    local color = BB.COLOR_BLACK
    if clue ~= "." then bb:paintRect(x + 2, y + 2, cell - 3, cell - 3, BB.COLOR_GRAY_D) end

    if clue == "t" then
        for dy = 0, 2 * radius - 1 do
            local half = math.floor(dy / 2)
            bb:paintRect(cx - half, cy - radius + dy, 2 * half + 1, 1, color)
        end
        bb:paintRect(cx - 1, cy + radius - 1, 3, 2, color)
    elseif mark == "#" and self.tents then
        local weight = math.max(1, math.floor(cell / 18))
        for dy = 0, 2 * radius - 1 do
            local half = math.floor(dy / 2)
            bb:paintRect(cx - half, cy - radius + dy, weight, 1, color)
            bb:paintRect(cx + half, cy - radius + dy, weight, 1, color)
        end
        bb:paintRect(cx - radius, cy + radius, 2 * radius + 1, weight, color)
        bb:paintRect(cx, cy + 1, weight, radius, color)
    elseif mark == "x" then
        bb:paintRect(cx - radius, cy, 2 * radius + 1, 1, BB.COLOR_GRAY_5)
        if not self.tents then bb:paintRect(cx - radius + 1, cy + 2, 2 * radius - 1, 1, BB.COLOR_GRAY_5) end
    elseif mark == "#" then
        if shape == "o" then
            bb:paintCircle(cx, cy, radius, color)
        elseif shape == "#" or shape == "m" then
            bb:paintRect(cx - radius, cy - radius, 2 * radius + 1, 2 * radius + 1, color)
        else
            local left = (shape == "e" or shape == "=") and x + 2 or cx - radius
            local right = (shape == "w" or shape == "=") and x + cell - 1 or cx + radius + 1
            local top = (shape == "s" or shape == "|") and y + 2 or cy - radius
            local bottom = (shape == "n" or shape == "|") and y + cell - 1 or cy + radius + 1
            if shape == "w" or shape == "e" or shape == "n" or shape == "s" then
                bb:paintCircle(cx, cy, radius, color)
                if shape == "w" then left = cx
                elseif shape == "e" then right = cx + 1
                elseif shape == "n" then top = cy
                else bottom = cy + 1 end
            end
            bb:paintRect(left, top, right - left, bottom - top, color)
        end
    end
end

function PlacementGame:statusText()
    if self.tents or self.overview then return Game.statusText(self) end
    local fleet = {}
    for length, remaining in ipairs(self.board:remainingFleet()) do
        if self.board.puzzle.fleet[length] > 0 then
            fleet[#fleet + 1] = string.format("%d-cell × %d", length, remaining)
        end
    end
    return "Fleet remaining   ·   " .. table.concat(fleet, "   ·   ")
end

function PlacementGame:showClues(row, col)
    local board = self.board
    local lines = {}
    if row then lines[#lines + 1] = ("Row %d · %d"):format(row, board.puzzle.rowClues[row][1]) end
    if col then lines[#lines + 1] = ("Column %d · %d"):format(col, board.puzzle.columnClues[col][1]) end
    if self.tents then
        lines[#lines + 1] = "Pair each tree with its own tent by a shared side. Tents cannot touch, even diagonally. Match the row and column totals."
    else
        local remaining = board:remainingFleet()
        local fleet = {}
        for length, count in ipairs(remaining) do
            if board.puzzle.fleet[length] > 0 then fleet[#fleet + 1] = string.format("%d-cell  %d/%d", length, count, board.puzzle.fleet[length]) end
        end
        lines[#lines + 1] = "Fleet remaining\n" .. table.concat(fleet, "   ·   ")
        lines[#lines + 1] = "Mark the ends of a ship with water to count it as placed. Ships are straight and cannot touch, even diagonally."
    end
    UIManager:show(InfoMessage:new{ text = table.concat(lines, "\n\n") })
end

function PlacementGame:checkSolution()
    local board = self.board
    local conflicts = board:ruleConflicts()
    local message = board:isComplete() and "All placement rules are satisfied."
        or conflicts > 0 and string.format("Rule conflicts found: %d.", conflicts)
        or "No rule conflicts detected so far."
    UIManager:show(InfoMessage:new{ text = message })
end

return PlacementGame
