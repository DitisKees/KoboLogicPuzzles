local Device = require("device")
local Geom = require("ui/geometry")
local InfoMessage = require("ui/widget/infomessage")
local LoopGame = require("kobologicpuzzles/slitherlinkgame")
local Render = require("kobologicpuzzles/galaxiesrender")
local UIManager = require("ui/uimanager")

local GalaxiesGame = LoopGame:extend{
    buttonLabels = { draw = "Wall", cross = "Open", restart = "Restart" },
}

local modes = { draw = "#", cross = "x", clear = "." }
local function round(value) return math.floor(value + 0.5) end

function GalaxiesGame:graphLabel()
    local puzzle = self.board.puzzle
    if self.overview then
        return self.board:isComplete() and puzzle.metadata.picture and "Tap the picture to return to the grid"
            or "Tap the overview to jump to a location"
    end
    return string.format("%d×%d  ·  %s", puzzle.width, puzzle.height,
        puzzle.metadata.picture and "Close symmetric regions to reveal the picture" or "Close one symmetric region per center")
end

function GalaxiesGame:paintGraph(graph, x, y, scale, _, full)
    local shade = self.board:regions()
    local clip = not full and self.graphClip
    if clip then
        graph = graph:viewport(clip.x, clip.y, clip.w, clip.h)
        x, y = x - clip.x, y - clip.y
    end
    local picturePreview = self.overview and self.board:isComplete() and self.board.puzzle.metadata.picture
    Render.paint(graph, self.board, x, y, scale, shade, picturePreview)
    self.graphClip, self.drawnShade = nil, shade
    return picturePreview
end

function GalaxiesGame:refreshStroke(changes)
    if not self.frame or not self.drawnShade then
        UIManager:setDirty(self, "ui")
        return
    end
    local board, grid, before = self.board, self.grid, self.drawnShade
    local ox, oy, scale = self:graphOrigin(false)
    local x0, y0, x1, y1 = grid.w, grid.h, 0, 0
    local function include(cell)
        local x = ox + (cell - 1) % board.width * scale
        local y = oy + math.floor((cell - 1) / board.width) * scale
        if x < grid.w and y < grid.h and x + scale >= 0 and y + scale >= 0 then
            x0, y0 = math.min(x0, x), math.min(y0, y)
            x1, y1 = math.max(x1, x + scale), math.max(y1, y + scale)
        end
    end
    for _, change in ipairs(changes) do
        local a, b = board:edgeCells(change.edge)
        include(a)
        include(b)
    end
    local after = board:regions()
    for cell in pairs(before) do if not after[cell] then include(cell) end end
    for cell in pairs(after) do if not before[cell] then include(cell) end end
    if x1 <= x0 or y1 <= y0 then
        UIManager:setDirty(self)
        return
    end
    local pad = math.ceil(scale / 4) + 2
    x0, y0 = math.max(0, math.floor(x0 - pad)), math.max(0, math.floor(y0 - pad))
    x1, y1 = math.min(grid.w, math.ceil(x1 + pad)), math.min(grid.h, math.ceil(y1 + pad))
    if self.graphClip then
        local prev = self.graphClip
        x0, y0 = math.min(x0, prev.x), math.min(y0, prev.y)
        x1, y1 = math.max(x1, prev.x + prev.w), math.max(y1, prev.y + prev.h)
    end
    self.graphClip = Geom:new{ x = x0, y = y0, w = x1 - x0, h = y1 - y0 }
    self.graphDirty = true
    UIManager:setDirty(self, "ui", Geom:new{ x = x0 + grid.x, y = y0 + grid.y, w = x1 - x0, h = y1 - y0 })
end

function GalaxiesGame:edgeAt(x, y, direction)
    local grid = self.grid
    local ox, oy, scale = self:graphOrigin(false)
    if x < grid.x or y < grid.y or x >= grid.x + grid.w or y >= grid.y + grid.h then return end
    x, y = (x - grid.x - ox) / scale, (y - grid.y - oy) / scale
    local board = self.board
    if x < 0 or y < 0 or x >= board.width or y >= board.height then return end
    local col, row = round(x), round(y)
    local dx, dy = math.abs(x - col), math.abs(y - row)
    local radius = math.min(0.29, Device.screen:scaleBySize(17) / scale)
    local horizontal = row > 0 and row < board.height and dy <= radius
    local vertical = col > 0 and col < board.width and dx <= radius
    if horizontal and vertical then horizontal = direction == "horizontal" or direction == nil and dy <= dx end
    if horizontal then return board:horizontal(row, math.floor(x)) end
    if vertical then return board:vertical(math.floor(y), col) end
end

function GalaxiesGame:beginMotion(start, multi)
    LoopGame.beginMotion(self, start, multi)
    local motion = self.motion
    if motion and motion.edge then
        local mark = modes[self.mode]
        motion.mark = self.board.state[motion.edge] == mark and "." or mark
        motion.position, motion.seen, motion.changes = start, {}, {}
    end
end

function GalaxiesGame:extendWall(position)
    local motion = self.motion
    local from = motion.position
    local dx, dy = position.x - from.x, position.y - from.y
    local direction
    if dx ~= 0 or dy ~= 0 then direction = math.abs(dx) > math.abs(dy) and "horizontal" or "vertical" end
    local samples = math.max(1, math.ceil(math.max(math.abs(dx), math.abs(dy)) / (self.edgeScale / 5)))
    local changes = {}
    for step = 0, samples do
        local edge = self:edgeAt(from.x + dx * step / samples, from.y + dy * step / samples, direction)
        if edge and not motion.seen[edge] and self.board.state[edge] ~= motion.mark then
            local change = { edge = edge, before = self.board.state[edge], after = motion.mark }
            motion.seen[edge] = true
            motion.changes[#motion.changes + 1] = change
            changes[#changes + 1] = change
        end
    end
    motion.position = position
    if #changes > 0 then
        self.board:apply(changes, "after")
        if not self.suspended then self:refreshStroke(changes) end
    end
end

function GalaxiesGame:onPan(_, gesture)
    if not self.motion then self:beginMotion(gesture.start_pos) end
    if self.motion and self.motion.edge then
        self:extendWall(gesture.pos)
        return true
    end
    return LoopGame.onPan(self, nil, gesture)
end

function GalaxiesGame:finishMotion(position)
    local motion = self.motion
    if not motion or not motion.edge then return LoopGame.finishMotion(self, position) end
    self:extendWall(position or motion.position)
    self.motion = nil
    self:changed(self.board:commit(motion.changes))
end

function GalaxiesGame:cancelSingleTouch()
    local motion = self.motion
    if motion and motion.edge then
        self.motion = nil
        self.board:apply(motion.changes, "before")
        self:refreshStroke(motion.changes)
        return
    end
    return LoopGame.cancelSingleTouch(self)
end

function GalaxiesGame:checkSolution()
    local message = "No saved solution is available."
    if self.board.solution then
        local errors, remaining = self.board:checkSolution()
        message = errors == 1 and "1 marked edge disagrees with the solution."
            or errors > 1 and string.format("%d marked edges disagree with the solution.", errors)
            or remaining > 0 and "No mistakes in the edges marked so far." or "Your regions match the solution."
    end
    UIManager:show(InfoMessage:new{ text = message })
end

return GalaxiesGame
