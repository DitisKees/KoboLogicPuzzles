local BB = require("ffi/blitbuffer")
local ConfirmBox = require("ui/widget/confirmbox")
local Device = require("device")
local Font = require("ui/font")
local Game = require("kobologicpuzzles/game")
local Geom = require("ui/geometry")
local InfoMessage = require("ui/widget/infomessage")
local Render = require("kobologicpuzzles/slitherlinkrender")
local Slitherlink = require("kobologicpuzzles/slitherlinkboard")
local UIManager = require("ui/uimanager")
local Viewport = require("kobologicpuzzles/viewport")

local LoopGame = Game:extend{
    defaultMode = "draw",
    buttonRows = { { "draw", "cross", "clear", "pan", "restart" }, { "undo", "redo", "minus", "plus", "check" } },
    buttonLabels = { draw = "Draw", restart = "Restart" },
}
local modes = { draw = "#", cross = "x", clear = "." }
local function rect(x, y, w, h) return Geom:new{ x = x, y = y, w = w, h = h } end
local function inside(r, x, y) return x >= r.x and x < r.x + r.w and y >= r.y and y < r.y + r.h end
local function round(x) return math.floor(x + 0.5) end

function LoopGame:makeLayout(w, h)
    local layout = Viewport:new(w, h, self.board.puzzle, self.zoom)
    local board, pad = self.board, layout.pad
    local grid = rect(pad, layout.headerY, w - 2 * pad, math.max(1, layout.footerY - layout.status - layout.headerY))
    local margin = math.min(pad, math.floor(math.min(grid.w, grid.h) / 4))
    local fit = math.min((grid.w - 2 * margin) / math.max(1, board.width), (grid.h - 2 * margin) / math.max(1, board.height))
    local scale = math.max(fit, layout.cell / board.edgeSize)
    local maxRow = 1 + math.max(0, math.ceil(board.height - (grid.h - 2 * margin) / scale))
    local maxColumn = 1 + math.max(0, math.ceil(board.width - (grid.w - 2 * margin) / scale))
    function layout:origin(row, column)
        return math.min(maxRow, math.max(1, round(row))), math.min(maxColumn, math.max(1, round(column)))
    end
    self.grid, self.margin, self.fit, self.edgeScale = grid, margin, fit, scale
    local size = math.min(18, math.max(6, math.floor(0.42 * scale * board.edgeSize / layout.scale)))
    self.loopFace = Font:getFace("cfont", size)
    return layout
end

function LoopGame:freeBuffers()
    Game.freeBuffers(self)
    if self.graphBuffer then
        self.graphBuffer:free()
        self.graphBuffer = nil
    end
end

function LoopGame:graphOrigin(overview)
    local scale = overview and self.fit or self.edgeScale
    local grid, board = self.grid, self.board
    local function origin(view, size, offset)
        if overview or size * scale <= view - 2 * self.margin then return (view - size * scale) / 2 end
        return self.margin - (offset - 1) * scale
    end
    return origin(grid.w, board.width, self.column), origin(grid.h, board.height, self.row), scale
end

function LoopGame:graphLabel()
    local metadata = self.board.puzzle.metadata
    return self.overview and "Tap the overview to jump to a location"
        or Slitherlink.tilingNames[metadata.tiling] .. "  ·  " .. metadata.faceCount .. " cells"
        .. (metadata.difficulty and ("  ·  " .. (Slitherlink.difficultyNames[metadata.difficulty] or metadata.difficulty)) or "")
end

function LoopGame:paintGraph(graph, x, y, scale, face)
    Render.paint(graph, self.board, x, y, scale, face)
end

function LoopGame:paintTo(bb)
    if not self.buffers then self:makeBuffers() end
    local canvas, layout, grid = self.buffers.screen, self.layout, self.grid
    local frame, complete = self.frame, self.board:isComplete()
    local inverse = bb:getInverse()
    local full = not frame or frame.row ~= self.row or frame.column ~= self.column
        or frame.overview ~= self.overview or frame.complete ~= complete or canvas:getInverse() ~= inverse
    if full then
        canvas:setInverse(inverse)
        canvas:fill(BB.COLOR_WHITE)
        frame = { row = self.row, column = self.column, overview = self.overview, complete = complete, buttons = {} }
        self.frame = frame
        local title = self.board.puzzle.title .. (complete and "  ·  Solved" or "")
        Game.paintText(canvas, title, self.titleFace, rect(layout.pad, layout.pad + layout.button,
            layout.width - 2 * layout.pad, layout.title), BB.COLOR_BLACK, true)
        Game.paintText(canvas, self:graphLabel(), self.clueFace, rect(grid.x, grid.y + grid.h, grid.w, layout.status), BB.COLOR_BLACK)
    end
    if full or self.graphDirty then
        self.graphBuffer = self.graphBuffer or BB.new(grid.w, grid.h, BB.TYPE_BB8)
        local graph = self.graphBuffer
        graph:setInverse(inverse)
        local x, y, scale = self:graphOrigin(self.overview)
        local face = not self.overview and self.loopFace or nil
        local picturePreview = self:paintGraph(graph, x, y, scale, face, full)
        if self.overview and not picturePreview then
            local boardX, boardY = self:graphOrigin(false)
            local ratio = scale / self.edgeScale
            local left = math.max(x, x - boardX * ratio)
            local top = math.max(y, y - boardY * ratio)
            local right = math.min(x + self.board.width * scale, x + (grid.w - boardX) * ratio)
            local bottom = math.min(y + self.board.height * scale, y + (grid.h - boardY) * ratio)
            graph:paintBorder(round(left), round(top), math.max(3, round(right - left)),
                math.max(3, round(bottom - top)), 2, BB.COLOR_GRAY_6)
        end
        canvas:blitFrom(graph, grid.x, grid.y)
        self.graphDirty = nil
    end
    self:paintButtons(canvas, frame, full)
    bb:blitFrom(canvas)
end

function LoopGame:refreshStroke()
    self.graphDirty = true
    UIManager:setDirty(self, "ui", self.grid)
end

function LoopGame:edgeAt(x, y)
    if not inside(self.grid, x, y) then return end
    local ox, oy, scale = self:graphOrigin(false)
    local puzzle = self.board.puzzle
    x, y = (x - self.grid.x - ox) / scale, (y - self.grid.y - oy) / scale
    local radius = math.min(Device.screen:scaleBySize(15) / scale, self.board.edgeSize / 3)
    local nearest, minimum = nil, radius * radius
    for i, edge in ipairs(puzzle.edges) do
        local a, b = puzzle.vertices[edge.first + 1], puzzle.vertices[edge.second + 1]
        local dx, dy = b.x - a.x, b.y - a.y
        local t = math.max(0, math.min(1, ((x - a.x) * dx + (y - a.y) * dy) / (dx * dx + dy * dy)))
        local distance = (x - a.x - t * dx)^2 + (y - a.y - t * dy)^2
        if distance < minimum then nearest, minimum = i, distance end
    end
    return nearest
end

function LoopGame:markEdge(edge)
    local mark = modes[self.mode]
    if not edge or not mark then return end
    if self.board.state[edge] == mark then mark = "." end
    self:changed(self.board:setMark(edge, mark))
end

function LoopGame:onTap(_, gesture)
    if self.motion then self:finishMotion() end
    local x, y = gesture.pos.x, gesture.pos.y
    for action, button in pairs(self.buttons) do
        if inside(button, x, y) then return self:onButton(action) end
    end
    if self.overview then
        if inside(self.grid, x, y) then
            local ox, oy, scale = self:graphOrigin(true)
            self.overview = false
            self:moveViewport((y - self.grid.y - oy) / scale - self.grid.h / (2 * self.edgeScale) + 1,
                (x - self.grid.x - ox) / scale - self.grid.w / (2 * self.edgeScale) + 1)
        end
    else
        self:markEdge(self:edgeAt(x, y))
    end
    return true
end

function LoopGame:onHold()
    return true
end

function LoopGame:beginMotion(start, multi)
    if self.overview or not start or not inside(self.grid, start.x, start.y) then return end
    local edge = not multi and modes[self.mode] and self:edgeAt(start.x, start.y)
    self.motion = { start = start, row = self.row, column = self.column, multi = multi, edge = edge }
end

function LoopGame:panMotion(motion, position)
    if motion.edge or not position then return end
    local scale = self.edgeScale
    local row, column = self.layout:origin(motion.row + (motion.start.y - position.y) / scale,
        motion.column + (motion.start.x - position.x) / scale)
    if row ~= self.row or column ~= self.column then
        self.row, self.column, motion.scrolled = row, column, true
        UIManager:setDirty(self, "ui", self.grid)
    end
end

function LoopGame:finishMotion(position)
    local motion = self.motion
    if motion and motion.edge then
        self.motion = nil
        if position and self:edgeAt(position.x, position.y) == motion.edge then self:markEdge(motion.edge) end
        return
    end
    Game.finishMotion(self, position)
end

function LoopGame:onPan(_, gesture)
    if not self.motion then self:beginMotion(gesture.start_pos) end
    if self.motion and not self.motion.multi then self:panMotion(self.motion, gesture.pos) end
    return true
end

function LoopGame:checkSolution()
    local message = "No saved solution is available."
    if self.board.solution then
        local errors, remaining = self.board:checkSolution()
        message = errors > 0 and string.format("%d marked edges disagree with the solution.", errors)
            or remaining > 0 and "No mistakes so far." or "Your loop matches the solution."
    end
    UIManager:show(InfoMessage:new{ text = message })
end

function LoopGame:onButton(action)
    if action == "restart" then
        UIManager:show(ConfirmBox:new{
            text = "Clear all marks on this board?",
            ok_text = "Restart",
            ok_callback = function()
                self:changed(self.board:restoreCheckpoint(string.rep(".", #self.board.state)), true)
            end,
        })
        return true
    end
    return Game.onButton(self, action)
end

return LoopGame
