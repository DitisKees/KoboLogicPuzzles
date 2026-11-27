local BB = require("ffi/blitbuffer")
local ConfirmBox = require("ui/widget/confirmbox")
local Game = require("kobologicpuzzles/game")
local Geom = require("ui/geometry")
local InfoMessage = require("ui/widget/infomessage")
local UIManager = require("ui/uimanager")

local FillAPixGame = Game:extend{
    buttonRows = { { "fill", "cross", "clear", "cycle", "pan", "restart" },
        { "undo", "redo", "minus", "plus", "clues", "check" } },
    buttonLabels = { clues = "Clue", restart = "Restart" },
}

function FillAPixGame:init()
    Game.init(self)
    if self.wasComplete and not self.record.marks then self.pendingSave = true end
end

function FillAPixGame:paintCell(bb, mark, row, column, full, index, clueState)
    if not clueState then return Game.paintCell(self, bb, mark, row, column, full) end

    Game.paintCell(self, bb, mark == "x" and "." or mark, row, column, full)
    local layout = self.layout
    local cell = layout.cell
    local x, y = layout.gridX + column * cell, layout.gridY + row * cell
    -- A gray numbered cell is crossed out, leaving room for the clue itself.
    if mark == "x" then bb:paintRect(x + 2, y + 2, cell - 3, cell - 3, BB.COLOR_LIGHT_GRAY) end
    local color = mark == "#" and BB.COLOR_WHITE
        or clueState == "satisfied" and BB.COLOR_GRAY_5 or BB.COLOR_BLACK
    if clueState == "conflict" then bb:paintBorder(x + 2, y + 2, cell - 3, cell - 3, 2, color) end
    Game.paintText(bb, tostring(self.board.puzzle.clues[index]), self.gridClueFace,
        Geom:new{ x = x + 2, y = y + 2, w = cell - 3, h = cell - 3 }, color, false, clueState == "satisfied")
end

function FillAPixGame:showClues(row, column)
    local board = self.board
    local clue = board.puzzle.clues[(row - 1) * board.width + column]
    local message = "Tap or hold a numbered cell to inspect its clue."
    if clue then
        local filled, undecided = board:counts(row, column)
        message = string.format("Clue %d at row %d, column %d\n\n%d filled · %d undecided\n\n"
            .. "The clue counts all touching cells, including diagonals and the numbered cell itself.",
            clue, row, column, filled, undecided)
    end
    UIManager:show(InfoMessage:new{ text = message })
end

function FillAPixGame:checkSolution()
    local board = self.board
    if not board:isComplete() and board.solution then return Game.checkSolution(self) end
    local message = board:isComplete() and "All clues are satisfied."
        or board.conflicts == 1 and "1 clue conflicts with your marks."
        or board.conflicts > 1 and string.format("%d clues conflict with your marks.", board.conflicts)
        or string.format("No conflicts so far. Clues still open: %d.", board.unfinished)
    UIManager:show(InfoMessage:new{ text = message })
end

function FillAPixGame:onButton(action)
    if action == "restart" then
        UIManager:show(ConfirmBox:new{
            text = "Clear all marks on this board?",
            ok_text = "Restart",
            ok_callback = function()
                local board = self.board
                local marks = board.initialMarks and board.initialMarks(board.puzzle) or string.rep(".", #board.cells)
                self:changed(board:restoreCheckpoint(marks), true)
            end,
        })
        return true
    end
    return Game.onButton(self, action)
end

return FillAPixGame
