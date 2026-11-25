local BB = require("ffi/blitbuffer")
local ConfirmBox = require("ui/widget/confirmbox")
local Device = require("device")
local Font = require("ui/font")
local Game = require("kobologicpuzzles/game")
local Geom = require("ui/geometry")
local InfoMessage = require("ui/widget/infomessage")
local Puzzle = require("kobologicpuzzles/puzzle")
local Render = require("kobologicpuzzles/hashirender")
local UIManager = require("ui/uimanager")

local HashiGame = Game:extend{
    defaultMode = "bridge",
    buttonRows = { { "bridge", "noBridge", "clear", "restart" }, { "undo", "redo", "minus", "plus", "check" } },
    buttonLabels = { bridge = "Bridge", noBridge = "No bridge", restart = "Restart" },
}

local function rect(x, y, w, h) return Geom:new{ x = x, y = y, w = w, h = h } end
local function contains(region, x, y)
    return x >= region.x and x < region.x + region.w and y >= region.y and y < region.y + region.h
end

function HashiGame:resize()
    Game.resize(self)
    local l = self.layout
    local fontScale = Device.screen:scaleBySize(100) / 100
    self.islandFace = Font:getFace("cfont", math.max(3, math.floor(l.cell * 0.60 / fontScale)))
    self.grid = rect(l.gridX, l.gridY, l.columns * l.cell, l.rows * l.cell)
    self.dirtyGrid = {}
end

function HashiGame:makeBuffers()
    local l = self.layout
    self.buffers = { screen = BB.new(l.width, l.height, BB.TYPE_BB8) }
end

function HashiGame:gridOrigin()
    return self.grid.x - (self.column - 1) * self.layout.cell, self.grid.y - (self.row - 1) * self.layout.cell
end

function HashiGame:paintTo(bb)
    if not self.buffers then self:makeBuffers() end
    local l, canvas, frame = self.layout, self.buffers.screen, self.frame
    if canvas:getInverse() ~= bb:getInverse() then
        canvas:setInverse(bb:getInverse())
        frame = nil
    end
    local complete, disconnected = self.board:isComplete(), self.board.degreesMatch and not self.board:isComplete()
    local full = not frame or frame.overview ~= self.overview or frame.row ~= self.row or frame.column ~= self.column
        or frame.complete ~= complete
    if full then
        frame = { row = self.row, column = self.column, overview = self.overview, complete = complete, buttons = {} }
        self.frame = frame
        canvas:fill(BB.COLOR_WHITE)
        local title = string.format("%s  ·  %d×%d", Puzzle.title(self.board.puzzle), self.board.width, self.board.height)
        if complete then title = title .. "  ·  Solved" end
        Game.paintText(canvas, title, self.titleFace, rect(l.pad, l.pad + l.button, l.width - 2 * l.pad, l.title),
            BB.COLOR_BLACK, true)
    end

    if self.overview then
        if full then
            local x, y, cell = l:overview()
            Render.paint(canvas, self.board, x, y, cell, rect(x, y, self.board.width * cell, self.board.height * cell))
            local viewX, viewY = x + (self.column - 1) * cell, y + (self.row - 1) * cell
            canvas:paintBorder(viewX, viewY, l.columns * cell, l.rows * cell, 4, BB.COLOR_WHITE)
            canvas:paintBorder(viewX, viewY, l.columns * cell, l.rows * cell, 2, BB.COLOR_BLACK)
        end
    else
        local x, y = self:gridOrigin()
        for _, region in ipairs(full and { self.grid } or self.dirtyGrid) do
            Render.paint(canvas, self.board, x, y, l.cell, region, self.islandFace, self.selectedIsland,
                self.store.settings.hashiGuides)
        end
    end
    self.dirtyGrid = {}

    if full or frame.disconnected ~= disconnected then
        local status = self.overview and "Tap the overview to jump to a location" or disconnected and "Connect all islands"
            or string.format("Rows %d–%d  ·  Columns %d–%d  ·  Drag to pan", self.row, self.row + l.rows - 1,
                self.column, self.column + l.columns - 1)
        local region = rect(l.pad, l.footerY - l.status, l.width - 2 * l.pad, l.status)
        canvas:paintRect(region.x, region.y, region.w, region.h, BB.COLOR_WHITE)
        Game.paintText(canvas, status, self.clueFace, region, BB.COLOR_BLACK)
        frame.disconnected = disconnected
        if not full then UIManager:setDirty(nil, "ui", region) end
    end

    self:paintButtons(canvas, frame, full)
    bb:blitFrom(canvas)
end

function HashiGame:invalidate(region)
    region = Render.clip(region, self.grid)
    if region then
        self.dirtyGrid[#self.dirtyGrid + 1] = region
        UIManager:setDirty(self, "ui", region)
    end
end

function HashiGame:refreshStroke(changes)
    if not self.frame or #changes > 8 then
        self.frame = nil
        UIManager:setDirty(self, "ui")
        return
    end
    local x, y = self:gridOrigin()
    for _, change in ipairs(changes) do
        self:invalidate(Render.edgeRect(self.board, self.board.graph.edges[change.edge], x, y, self.layout.cell))
    end
    -- Buttons or the connectivity message can change when an edited edge is off screen.
    if #self.dirtyGrid == 0 then UIManager:setDirty(self) end
end

function HashiGame:selectIsland(id)
    if self.selectedIsland == id then return end
    local old = self.selectedIsland
    self.selectedIsland = id
    local x, y = self:gridOrigin()
    local function invalidate(island)
        if not island then return end
        self:invalidate(Render.islandRect(self.board.puzzle.islands[island], x, y, self.layout.cell))
        if self.store.settings.hashiGuides then
            for _, edge in pairs(self.board.graph.neighbors[island]) do
                if self.board.states[edge] == "." then
                    self:invalidate(Render.edgeRect(self.board, self.board.graph.edges[edge], x, y, self.layout.cell))
                end
            end
        end
    end
    invalidate(old)
    invalidate(id)
end

function HashiGame:onTap(_, gesture)
    if self.motion then
        local scrolled = self.motion.scrolled
        self:finishMotion()
        if scrolled then return true end
    end
    local x, y = gesture.pos.x, gesture.pos.y
    for action, button in pairs(self.buttons) do
        if contains(button, x, y) then return self:onButton(action) end
    end
    local l = self.layout
    if self.overview then
        local startX, startY, cell = l:overview()
        if contains(rect(startX, startY, self.board.width * cell, self.board.height * cell), x, y) then
            self.overview = false
            self:moveViewport(math.floor((y - startY) / cell) + 1 - math.floor(l.rows / 2),
                math.floor((x - startX) / cell) + 1 - math.floor(l.columns / 2))
        end
        return true
    end

    local row, column = l:cellAt(x, y, self.row, self.column)
    local id = row and self.board:islandAt(row, column)
    local selected = self.selectedIsland
    if not id or id == selected then
        self:selectIsland(nil)
    elseif selected and self.board.graph.neighbors[selected][id] then
        local changes, err = self.board:edit(selected, id, self.mode)
        self:changed(changes)
        if err then UIManager:show(InfoMessage:new{ text = err, timeout = 2 }) end
    else
        self:selectIsland(id)
    end
    return true
end

function HashiGame:markAt() return nil end
function HashiGame:onHold() return true end

function HashiGame:onPan(_, gesture)
    if not self.motion then self:beginMotion(gesture.start_pos) end
    if self.motion and not self.motion.multi then self:panMotion(self.motion, gesture.pos) end
    return true
end

function HashiGame:onButton(action)
    if action == "restart" then
        UIManager:show(ConfirmBox:new{
            text = "Clear all bridges and marks on this board?",
            ok_text = "Restart",
            ok_callback = function()
                self:selectIsland(nil)
                self:changed(self.board:restart(), true)
            end,
        })
        return true
    end
    return Game.onButton(self, action)
end

function HashiGame:checkSolution()
    local errors, complete = self.board:checkSolution()
    local message = errors == 1 and "1 marked edge disagrees with the solution."
        or errors > 1 and string.format("%d marked edges disagree with the solution.", errors)
        or complete and "Your bridges match the solution." or "No mistakes so far."
    UIManager:show(InfoMessage:new{ text = message })
end

return HashiGame
