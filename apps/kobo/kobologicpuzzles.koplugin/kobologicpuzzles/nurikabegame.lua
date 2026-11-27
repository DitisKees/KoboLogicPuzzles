local BB = require("ffi/blitbuffer")
local FillAPixGame = require("kobologicpuzzles/fillapixgame")
local Game = require("kobologicpuzzles/game")
local Geom = require("ui/geometry")
local InfoMessage = require("ui/widget/infomessage")
local UIManager = require("ui/uimanager")

local NurikabeGame = FillAPixGame:extend{
    buttonLabels = { clues = "Rules", cross = "White", restart = "Restart" },
}

function NurikabeGame:init()
    local maxClue = 1
    for _, clue in ipairs(self.board.puzzle.clues) do maxClue = math.max(maxClue, clue) end
    self.clueSample = string.rep("0", #tostring(maxClue))
    FillAPixGame.init(self)
end

function NurikabeGame:paintCell(bb, mark, row, col, full, index, state)
    local clue = self.board.puzzle.clues[index]
    local layout = self.layout
    local cell = layout.cell
    local x, y = layout.gridX + col * cell, layout.gridY + row * cell
    Game.paintCell(self, bb, clue > 0 and "." or mark, row, col, full)
    local color = mark == "#" and BB.COLOR_WHITE or BB.COLOR_BLACK
    if state == "conflict" then
        bb:paintBorder(x + 2, y + 2, cell - 3, cell - 3, 2, color)
    elseif state == "satisfied" and clue == 0 then
        bb:paintRect(x + 3, y + 3, cell - 5, cell - 5, BB.COLOR_LIGHT_GRAY)
    end
    if clue > 0 then
        if state == "satisfied" then color = BB.COLOR_GRAY_5 end
        Game.paintText(bb, tostring(clue), self.gridClueFace,
            Geom:new{ x = x + 2, y = y + 2, w = cell - 3, h = cell - 3 }, color, false, state == "satisfied")
    end
end

function NurikabeGame:refreshStroke(changes)
    local repaint = {}
    for _, change in ipairs(changes) do repaint[#repaint + 1] = change end
    if self.frame then
        local board = self.board
        for row = self.row, self.row + self.layout.rows - 1 do
            for col = self.column, self.column + self.layout.columns - 1 do
                local index = (row - 1) * board.width + col
                if self.frame.clueStates[index] ~= board.clueStates[index] then
                    repaint[#repaint + 1] = { row = row, column = col }
                end
            end
        end
    end
    Game.refreshStroke(self, repaint)
end

function NurikabeGame:showClues()
    UIManager:show(InfoMessage:new{ text = "Shade one connected black sea with no 2×2 black blocks. "
        .. "Every white island must contain exactly one clue and have that many cells. "
        .. "Count connections by sides. Mark island cells with White." })
end

function NurikabeGame:checkSolution()
    local board = self.board
    local message = board:isComplete() and "The sea and all islands satisfy the rules."
        or board.conflicts > 0 and string.format("Conflicts found: %d. Check outlined cells.", board.conflicts)
        or string.format("No rule conflicts detected. Undecided cells: %d.", board.undecided)
    UIManager:show(InfoMessage:new{ text = message })
end

return NurikabeGame
