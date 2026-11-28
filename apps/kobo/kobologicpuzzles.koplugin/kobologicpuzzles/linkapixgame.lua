local BB = require("ffi/blitbuffer")
local ConfirmBox = require("ui/widget/confirmbox")
local Game = require("kobologicpuzzles/game")
local Geom = require("ui/geometry")
local InfoMessage = require("ui/widget/infomessage")
local UIManager = require("ui/uimanager")
local bit = require("bit")

local LinkGame = Game:extend{
    defaultMode = "draw",
    buttonRows = { { "draw", "white", "clear", "pan", "restart" }, { "undo", "redo", "minus", "plus", "rules", "check" } },
    buttonLabels = { draw = "Draw", white = "White", clear = "Clear", pan = "Pan", restart = "Restart", rules = "Rules" },
}

function LinkGame:init()
    local max = 1
    for _, clue in ipairs(self.board.puzzle.clues) do max = math.max(max, clue.length) end
    self.clueSample = string.rep("0", #tostring(max))
    Game.init(self)
    if self.wasComplete and not self.record.marks then self.pendingSave = true end
end

function LinkGame:paintCell(bb, mark, r, c, full, index, style)
    Game.paintCell(self, bb, ".", r, c, full)
    local l = self.layout
    local s = l.cell
    if s < 6 then return end
    local x, y = l.gridX + c * s, l.gridY + r * s
    local clue = self.board.clues[index]
    if style == "ink" then
        bb:paintRect(x + 2, y + 2, s - 2, s - 2, BB.COLOR_BLACK)
        if clue and s >= 12 then
            Game.paintText(bb, tostring(clue), self.gridClueFace, Geom:new{ x = x + 2, y = y + 2, w = s - 3, h = s - 3 },
                BB.COLOR_LIGHT_GRAY)
        end
    elseif style == "path" then
        local mask = tonumber(mark, 16)
        local cx, cy = x + math.floor(s / 2), y + math.floor(s / 2)
        local t = math.max(2, math.floor(s / 7))
        local dx, dy = cx - math.floor(t / 2), cy - math.floor(t / 2)
        if bit.band(mask, 1) ~= 0 then bb:paintRect(dx, y + 2, t, dy + t - y - 2, BB.COLOR_GRAY_6) end
        if bit.band(mask, 2) ~= 0 then bb:paintRect(dx, dy, x + s - dx, t, BB.COLOR_GRAY_6) end
        if bit.band(mask, 4) ~= 0 then bb:paintRect(dx, dy, t, y + s - dy, BB.COLOR_GRAY_6) end
        if bit.band(mask, 8) ~= 0 then bb:paintRect(x + 2, dy, dx + t - x - 2, t, BB.COLOR_GRAY_6) end
    elseif mark == "x" then
        bb:paintRect(x + math.floor(s / 3), y + math.floor(s / 2), math.max(2, math.floor(s / 3)), 2, BB.COLOR_GRAY_6)
    end
    if clue and style ~= "ink" then
        local region = Geom:new{ x = x + 2, y = y + 2, w = s - 3, h = s - 3 }
        if style == "path" then
            local p = math.max(2, math.floor(s / 6))
            bb:paintRect(x + p, y + p, s - 2 * p, s - 2 * p, BB.COLOR_GRAY_D)
        end
        Game.paintText(bb, tostring(clue), self.gridClueFace, region, BB.COLOR_BLACK)
    end
end

function LinkGame:paintOverview(bb)
    if not self.board:isComplete() then return Game.paintOverview(self, bb) end
    local x, y, scale = self.layout:overview()
    local board = self.board
    for row = 0, board.height - 1 do
        local top, bottom = math.floor(row * scale), math.floor((row + 1) * scale)
        if bottom > top then
            for col = 0, board.width - 1 do
                local left, right = math.floor(col * scale), math.floor((col + 1) * scale)
                if right > left and board.clueStates[board:index(row + 1, col + 1)] == "ink" then
                    bb:paintRect(x + left, y + top, right - left, bottom - top, BB.COLOR_BLACK)
                end
            end
        end
    end
end

function LinkGame:statusText()
    local board, active = self.board, self.active
    if self.overview then return board:isComplete() and "Finished picture" or Game.statusText(self) end
    if board.feedback then return board.feedback end
    local stroke = board.stroke
    local path = stroke and stroke.path or active and board.paths[active]
    if path or active then
        return string.format("%d of %d cells", path and #path or 1, board.clues[path and path[1] or active])
    end
    return string.format("%d of %d pairs connected   ·   %s", board.confirmed, board.required, Game.statusText(self))
end

function LinkGame:refreshStatus()
    local l = self.layout
    UIManager:setDirty(self, "ui", Geom:new{ x = l.pad, y = l.footerY - l.status, w = l.width - 2 * l.pad, h = l.status })
end

function LinkGame:markAt(row, col)
    if self.mode == "draw" and self.board:canStart(self.board:index(row, col)) then return "draw" end
end

function LinkGame:beginMotion(start, multi)
    self.active, self.board.feedback = nil, nil
    Game.beginMotion(self, start, multi)
end

function LinkGame:edgeOrigin()
    local board, layout = self.board, self.layout
    if board.feedback then return self.row, self.column end
    local stroke = board.stroke
    local path, pos = stroke.path, self.motion.position
    if #path > 1 and not stroke.replace and board.clues[path[#path]] then return self.row, self.column end
    local tail = path[stroke.replace and 1 or #path]
    local row, col = math.floor((tail - 1) / board.width) + 1, (tail - 1) % board.width + 1
    local cx = layout.gridX + (col - self.column + 0.5) * layout.cell
    local cy = layout.gridY + (row - self.row + 0.5) * layout.cell
    local margin = math.floor(layout.cell / 3)
    local dx, dy = pos.x - cx, pos.y - cy
    local stepX, stepY = 0, 0
    if dx < 0 and pos.x < layout.gridX + margin then stepX = -1
    elseif dx > 0 and pos.x >= layout.gridX + layout.columns * layout.cell - margin then stepX = 1 end
    if dy < 0 and pos.y < layout.gridY + margin then stepY = -1
    elseif dy > 0 and pos.y >= layout.gridY + layout.rows * layout.cell - margin then stepY = 1 end
    if stepX ~= 0 and stepY ~= 0 then
        if math.abs(dx) >= math.abs(dy) then stepY = 0 else stepX = 0 end
    end
    return layout:origin(self.row + stepY, self.column + stepX)
end

function LinkGame:onPan(_, gesture)
    local status = self:statusText()
    Game.onPan(self, nil, gesture)
    if self:statusText() ~= status then self:refreshStatus() end
    return true
end

function LinkGame:finishMotion(position)
    local wasDrawing = self.motion and self.motion.paint
    Game.finishMotion(self, position)
    if wasDrawing and not self.suspended then self:refreshStatus() end
end

function LinkGame:onTap(_, gesture)
    if self.motion then self:finishMotion() end
    local pos = gesture.pos
    for action, area in pairs(self.buttons) do
        if pos.x >= area.x and pos.x < area.x + area.w and pos.y >= area.y and pos.y < area.y + area.h then
            return self:onButton(action)
        end
    end
    if self.overview then return Game.onTap(self, nil, gesture) end
    local row, col = self.layout:cellAt(pos.x, pos.y, self.row, self.column)
    if not row then return true end
    local board = self.board
    local i = board:index(row, col)
    board.feedback = nil
    if self.mode == "white" then
        self:changed(board:toggleWhite(i))
    elseif self.mode == "clear" then
        self:changed(board:clearPath(i))
    elseif self.mode == "draw" then
        local active = self.active
        if active and active ~= i then
            local r, c = math.floor((active - 1) / board.width) + 1, (active - 1) % board.width + 1
            if r == row or c == col then
                board:beginStroke(r, c)
                board:extendStroke(row, col)
                local last = board.stroke and board.stroke.path[#board.stroke.path]
                local changes = board:finishStroke()
                if changes then
                    self.active = last and not board.clues[last] and last or nil
                    self:changed(changes)
                    self:refreshStatus()
                    return true
                end
            end
        end
        if not board.feedback then self.active = active ~= i and board:canStart(i) and i or nil end
    end
    self:refreshStatus()
    return true
end

function LinkGame:onHold(_, gesture)
    if self.overview then return true end
    local row, col = self.layout:cellAt(gesture.pos.x, gesture.pos.y, self.row, self.column)
    if row then
        self.active, self.board.feedback = nil, nil
        self:changed(self.board:clearPath(self.board:index(row, col)))
        self:refreshStatus()
    end
    return true
end

function LinkGame:checkSolution()
    local board = self.board
    local remaining = board.required - board.confirmed
    local text = board:isComplete() and "All pairs are connected. Open Overview to see the picture."
        or remaining == 1 and "One pair still needs a path of the indicated length."
        or string.format("%d pairs still need paths of the indicated length.", remaining)
    UIManager:show(InfoMessage:new{ text = text })
end

function LinkGame:onButton(action)
    if action ~= "draw" then self.active, self.board.feedback = nil, nil end
    if action == "rules" then
        UIManager:show(InfoMessage:new{ text = "Connect equal numbers through exactly that many cells, including both ends. "
            .. "Drag along rows and columns or tap to extend. Backtrack to shorten a path, or hold a path to clear it. "
            .. "A 1 is a single painted cell. White marks are optional." })
        return true
    end
    if action == "restart" then
        UIManager:show(ConfirmBox:new{
            text = "Clear all paths and white marks?",
            ok_text = "Restart",
            ok_callback = function()
                self:changed(self.board:restoreCheckpoint(self.board.initialMarks(self.board.puzzle)), true)
            end,
        })
        return true
    end
    return Game.onButton(self, action)
end

return LinkGame
