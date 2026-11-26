local BB = require("ffi/blitbuffer")
local ButtonDialog = require("ui/widget/buttondialog")
local Device = require("device")
local Font = require("ui/font")
local Geom = require("ui/geometry")
local GestureRange = require("ui/gesturerange")
local InfoMessage = require("ui/widget/infomessage")
local InputContainer = require("ui/widget/container/inputcontainer")
local RenderText = require("ui/rendertext")
local UIManager = require("ui/uimanager")
local Viewport = require("kobologicpuzzles/viewport")
local Widget = require("ui/widget/widget")

local screen = Device.screen
local Game = InputContainer:extend{ modal = true, covers_fullscreen = true }
local CluePanel = Widget:extend{ not_focusable = true }
local marks = { fill = "#", cross = "x", clear = "." }
local cycle = { ["."] = "#", ["#"] = "x", x = "." }
local labels = { fill = "Fill", cross = "Cross", clear = "Clear", cycle = "Cycle", pan = "Pan",
    undo = "Undo", redo = "Redo", minus = "−", plus = "+", clues = "Clues",
    checkpoint = "Checkpoint", check = "Check" }

local function rectangle(x, y, w, h) return Geom:new{ x = x, y = y, w = w, h = h } end
local function contains(rect, x, y)
    return x >= rect.x and y >= rect.y and x < rect.x + rect.w and y < rect.y + rect.h
end

local function text(bb, value, face, region, color, leading, strike)
    local size = RenderText:sizeUtf8Text(0, false, face, value, true)
    if size.x > region.w then
        value = RenderText:truncateTextByWidth(value, face, region.w, true)
        size = RenderText:sizeUtf8Text(0, false, face, value, true)
    end
    local x = region.x + (leading and 0 or math.max(0, math.floor((region.w - size.x) / 2)))
    local baseline = region.y + math.floor((region.h + size.y_top - size.y_bottom) / 2)
    RenderText:renderUtf8Text(bb, x, baseline, face, value, true, false, color, region.w)
    if strike then
        bb:paintRect(x, region.y + math.floor(region.h / 2), size.x, 2, color)
    end
end
Game.paintText = text

function CluePanel:init()
    self.lineHeight = screen:scaleBySize(23)
    self.slotWidth = screen:scaleBySize(33)
    self.slots = math.max(1, math.floor(self.width / self.slotWidth))
    local lineCount = #self.lines - 1
    for _, line in ipairs(self.lines) do
        lineCount = lineCount + 1 + math.ceil(math.max(1, #line.clues) / self.slots)
        if line.status.impossible then lineCount = lineCount + 1 end
    end
    self.dimen = rectangle(0, 0, self.width, lineCount * self.lineHeight)
end

function CluePanel:paintTo(bb, x, y)
    local height = self.lineHeight
    for _, line in ipairs(self.lines) do
        text(bb, line.label, self.face, rectangle(x, y, self.width, height), BB.COLOR_BLACK, true)
        y = y + height
        local count = math.max(1, #line.clues)
        for i = 1, count do
            local column, row = (i - 1) % self.slots, math.floor((i - 1) / self.slots)
            local done = line.status.complete or line.status[i]
            local region = rectangle(x + column * self.slotWidth, y + row * height, self.slotWidth, height)
            text(bb, tostring(line.clues[i] or 0), self.face, region,
                done and BB.COLOR_GRAY_6 or BB.COLOR_BLACK, true, done)
        end
        y = y + math.ceil(count / self.slots) * height
        if line.status.impossible then
            text(bb, "Marks conflict with these clues.", self.face, rectangle(x, y, self.width, height),
                BB.COLOR_BLACK, true)
            y = y + height
        end
        y = y + height
    end
end

function Game:init()
    self.mode = self.defaultMode or "fill"
    self.row = math.max(1, math.floor(tonumber(self.record.row) or 1))
    self.column = math.max(1, math.floor(tonumber(self.record.column) or 1))
    self.zoom = math.max(Viewport.minZoom, math.min(Viewport.maxZoom,
        math.floor(tonumber(self.record.zoom) or Viewport.defaultZoom)))
    self.wasComplete = self.board:isComplete()
    self.pendingSave = #self.board.undo > 0
    self.key_events = { Close = { { "Back" }, { "Esc" } } }
    self:resize()
end

function Game:makeLayout(w, h)
    return Viewport:new(w, h, self.board.puzzle, self.zoom, self.store.settings.clueLimit)
end

function Game:resize()
    self:freeBuffers()
    local w, h = screen:getWidth(), screen:getHeight()
    local layout = self:makeLayout(w, h)
    self.layout = layout
    self.row, self.column = layout:origin(self.row, self.column)
    self.dimen = rectangle(0, 0, w, h)
    self.ges_events = {}
    local gestures = { Tap = "tap", Hold = "hold", Pan = "pan", PanRelease = "pan_release",
        Swipe = "swipe", MultiSwipe = "multiswipe", TwoFingerPan = "two_finger_pan",
        TwoFingerPanRelease = "two_finger_pan_release", TwoFingerSwipe = "two_finger_swipe",
        TwoFingerHoldPan = "two_finger_hold_pan", TwoFingerHoldPanRelease = "two_finger_hold_pan_release",
        InwardPan = "inward_pan", OutwardPan = "outward_pan",
        Pinch = "pinch", Spread = "spread", Rotate = "rotate", TwoFingerTap = "two_finger_tap" }
    for event, gesture in pairs(gestures) do
        self.ges_events[event] = { GestureRange:new{ ges = gesture, range = self.dimen } }
    end
    self.titleFace = Font:getFace("tfont", 21)
    self.buttonFace = Font:getFace("cfont", 17)
    self.clueFace = Font:getFace("cfont", 16)
    local sample = self.board.clueStates and "0"
        or (math.max(layout.puzzle.width, layout.puzzle.height) == 100 and "100" or "00")
    local sampleWidth = RenderText:sizeUtf8Text(0, false, self.clueFace, sample, true).x
    local gridFontSize = math.max(5, math.min(16, math.floor(16 * (layout.cell - 2) / sampleWidth)))
    self.gridClueFace = Font:getFace("cfont", gridFontSize)
    self.markFace = self.zoom < 1 and self.gridClueFace or self.buttonFace
    self.buttons = {}
    local pad, button = layout.pad, layout.button
    self.buttons.library = rectangle(pad, pad, 2 * button, button)
    local checkpointWidth = math.floor(3.5 * button)
    self.buttons.checkpoint = rectangle(math.floor((w - checkpointWidth) / 2), pad, checkpointWidth, button)
    self.buttons.overview = rectangle(w - pad - 3 * button, pad, 3 * button, button)
    local function row(actions, y)
        local step = (w - pad) / #actions
        for i, action in ipairs(actions) do
            local x = math.floor((i - 1) * step) + pad
            self.buttons[action] = rectangle(x, y, math.floor(i * step) - x, button)
        end
    end
    row(self.buttonRows and self.buttonRows[1] or { "fill", "cross", "clear", "cycle", "pan" }, layout.footerY)
    row(self.buttonRows and self.buttonRows[2] or { "undo", "redo", "minus", "plus", "clues", "check" },
        layout.footerY + button + pad)
end

function Game:paintButtons(bb, frame, full)
    for action, region in pairs(self.buttons) do
        local active = self.mode == action or (action == "overview" and self.overview)
        local disabled = (action == "undo" and #self.board.undo == 0)
            or (action == "redo" and #self.board.redo == 0)
            or (action == "minus" and self.zoom == Viewport.minZoom)
            or (action == "plus" and self.zoom == Viewport.maxZoom)
        local state = active and "active" or disabled and "disabled" or "enabled"
        if full or frame.buttons[action] ~= state then
            local label = (self.buttonLabels and self.buttonLabels[action]) or labels[action]
                or (action == "library" and "Library" or (self.overview and "Grid" or "Overview"))
            local color = active and BB.COLOR_WHITE or (disabled and BB.COLOR_GRAY_7 or BB.COLOR_BLACK)
            bb:paintRect(region.x, region.y, region.w, region.h, active and BB.COLOR_BLACK or BB.COLOR_WHITE)
            bb:paintBorder(region.x, region.y, region.w, region.h, 1,
                disabled and BB.COLOR_LIGHT_GRAY or BB.COLOR_BLACK)
            text(bb, label, self.buttonFace, region, color)
            frame.buttons[action] = state
            if not full then UIManager:setDirty(nil, "ui", region:copy()) end
        end
    end
end

local function clueLabel(clues, index, slots, ellipsis)
    if #clues == 0 then return index == slots and "0" or nil end
    if ellipsis and index == 1 and #clues >= slots then return "…" end
    local clue = #clues + index - slots
    local value = clues[clue]
    return value and tostring(value), clue
end

local function paintClue(bb, face, region, clues, slot, slots, ellipsis, status)
    local label, clue = clueLabel(clues, slot, slots, ellipsis)
    if label then
        local done = status.complete or status[clue]
        text(bb, label, face, region, done and BB.COLOR_GRAY_6 or BB.COLOR_BLACK, false, done)
    end
end

function Game:freeBuffers()
    if self.buffers then
        for _, buffer in pairs(self.buffers) do buffer:free() end
        self.buffers = nil
    end
    self.frame = nil
end

function Game:makeBuffers()
    local l = self.layout
    local buffers = {
        screen = BB.new(l.width, l.height, BB.TYPE_BB8),
        cross = BB.new(l.cell, l.cell, BB.TYPE_BB8),
    }
    if l.leftSlots > 0 then buffers.rowClues = BB.new(l.gridX - l.pad, l.cell, BB.TYPE_BB8) end
    if l.topSlots > 0 then buffers.columnClues = BB.new(l.cell, l.gridY - l.headerY, BB.TYPE_BB8) end
    buffers.cross:fill(BB.COLOR_WHITE)
    text(buffers.cross, "×", self.markFace, rectangle(0, 0, l.cell, l.cell), BB.COLOR_GRAY_5)
    self.buffers = buffers
end

function Game:paintRowClues(bb, row, y)
    local l, board, strip = self.layout, self.board, self.buffers.rowClues
    local status = board.rows[row]
    strip:fill(row == self.selectedRow and BB.COLOR_GRAY_D or BB.COLOR_WHITE)
    for slot = 1, l.leftSlots do
        paintClue(strip, self.gridClueFace, rectangle((slot - 1) * l.step, 0, l.step, l.cell),
            board.puzzle.rowClues[row], slot, l.leftSlots, l.leftEllipsis, status)
    end
    if status.impossible then strip:paintRect(strip:getWidth() - 4, 2, 3, l.cell - 4, BB.COLOR_BLACK) end
    bb:blitFrom(strip, l.pad, y)
end

function Game:paintColumnClues(bb, column, x)
    local l, board, strip = self.layout, self.board, self.buffers.columnClues
    local status = board.columns[column]
    strip:fill(column == self.selectedColumn and BB.COLOR_GRAY_D or BB.COLOR_WHITE)
    for slot = 1, l.topSlots do
        paintClue(strip, self.gridClueFace, rectangle(0, (slot - 1) * l.step, l.cell, l.step),
            board.puzzle.columnClues[column], slot, l.topSlots, l.topEllipsis, status)
    end
    if status.impossible then strip:paintRect(2, strip:getHeight() - 4, l.cell - 4, 3, BB.COLOR_BLACK) end
    bb:blitFrom(strip, x, l.headerY)
end

local function horizontalLine(bb, x, y, width, major)
    bb:paintRect(x, y, width, major and 2 or 1, major and BB.COLOR_BLACK or BB.COLOR_GRAY_9)
end

local function verticalLine(bb, x, y, height, major)
    bb:paintRect(x, y, major and 2 or 1, height, major and BB.COLOR_BLACK or BB.COLOR_GRAY_9)
end

local function isMajorLine(offset, first, count)
    return offset == 0 or offset == count or (first + offset - 1) % 5 == 0
end

function Game:paintCell(bb, mark, r, c, full)
    local l = self.layout
    local x, y, cell = l.gridX + c * l.cell, l.gridY + r * l.cell, l.cell
    if mark == "x" then
        bb:blitFrom(self.buffers.cross, x, y)
    else
        if not full then bb:paintRect(x, y, cell, cell, BB.COLOR_WHITE) end
        if mark == "#" then bb:paintRect(x + 2, y + 2, cell - 3, cell - 3, BB.COLOR_BLACK) end
    end
    if not full then
        -- The leading grid lines belong to this cell and are erased with its mark.
        horizontalLine(bb, x, y, cell, isMajorLine(r, self.row, l.rows))
        verticalLine(bb, x, y, cell, isMajorLine(c, self.column, l.columns))
    end
end

function Game:paintGrid(bb, frame, full)
    local l, board = self.layout, self.board
    local gridX, gridY, cell = l.gridX, l.gridY, l.cell
    local clueStates = board.clueStates
    local firstRow, lastRow, firstColumn, lastColumn
    for r = 0, l.rows - 1 do
        local row = self.row + r
        if l.leftSlots > 0 and (full or frame.rows[row] ~= board.rows[row]
            or (row == frame.selectedRow) ~= (row == self.selectedRow)) then
            self:paintRowClues(bb, row, gridY + r * cell)
            frame.rows[row] = board.rows[row]
            firstRow, lastRow = firstRow or r, r
        end
        for c = 0, l.columns - 1 do
            local index = (row - 1) * board.width + self.column + c
            local mark = board.cells[index]
            local clueState = clueStates and clueStates[index]
            if full or frame.cells[index] ~= mark or (clueStates and frame.clueStates[index] ~= clueState) then
                self:paintCell(bb, mark, r, c, full, index, clueState)
                frame.cells[index] = mark
                if clueStates then frame.clueStates[index] = clueState end
            end
        end
    end
    for c = 0, l.columns - 1 do
        local column = self.column + c
        if l.topSlots > 0 and (full or frame.columns[column] ~= board.columns[column]
            or (column == frame.selectedColumn) ~= (column == self.selectedColumn)) then
            self:paintColumnClues(bb, column, gridX + c * cell)
            frame.columns[column] = board.columns[column]
            firstColumn, lastColumn = firstColumn or c, c
        end
    end
    frame.selectedRow, frame.selectedColumn = self.selectedRow, self.selectedColumn
    if full then
        for r = 0, l.rows do
            horizontalLine(bb, gridX, gridY + r * cell, l.columns * cell + 1, isMajorLine(r, self.row, l.rows))
        end
        for c = 0, l.columns do
            verticalLine(bb, gridX + c * cell, gridY, l.rows * cell + 1, isMajorLine(c, self.column, l.columns))
        end
    else
        if firstRow then
            UIManager:setDirty(nil, "ui", rectangle(l.pad, gridY + firstRow * cell, gridX - l.pad,
                (lastRow - firstRow + 1) * cell))
        end
        if firstColumn then
            UIManager:setDirty(nil, "ui", rectangle(gridX + firstColumn * cell, l.headerY,
                (lastColumn - firstColumn + 1) * cell, gridY - l.headerY))
        end
    end
end

function Game:paintOverview(bb)
    local l, board = self.layout, self.board
    local x, y, cell = l:overview()
    local function edge(index) return math.floor(index * cell) end
    for row = 1, board.height do
        local top, bottom = edge(row - 1), edge(row)
        if bottom > top then
            for column = 1, board.width do
                local mark = board:get(row, column)
                local left, right = edge(column - 1), edge(column)
                if mark ~= "." and right > left then
                    bb:paintRect(x + left, y + top, right - left, bottom - top,
                        mark == "#" and BB.COLOR_BLACK or BB.COLOR_LIGHT_GRAY)
                end
            end
        end
    end
    bb:paintBorder(x, y, math.max(1, edge(board.width)), math.max(1, edge(board.height)), 1, BB.COLOR_BLACK)
    local left, top = edge(self.column - 1), edge(self.row - 1)
    local width = math.max(1, edge(self.column + l.columns - 1) - left)
    local height = math.max(1, edge(self.row + l.rows - 1) - top)
    if width >= 8 and height >= 8 then bb:paintBorder(x + left, y + top, width, height, 4, BB.COLOR_WHITE) end
    bb:paintBorder(x + left, y + top, width, height, math.min(2, width, height), BB.COLOR_BLACK)
end

function Game:paintTo(bb)
    local l = self.layout
    if not self.buffers then self:makeBuffers() end
    local canvas, frame = self.buffers.screen, self.frame
    if canvas:getInverse() ~= bb:getInverse() then
        canvas:setInverse(bb:getInverse())
        frame = nil
    end
    local complete = self.board:isComplete()
    local full = self.overview or not frame or frame.overview
        or frame.row ~= self.row or frame.column ~= self.column or frame.complete ~= complete
    if full then
        frame = {
            row = self.row, column = self.column, overview = self.overview, complete = complete,
            cells = {}, rows = {}, columns = {}, clueStates = self.board.clueStates and {} or nil, buttons = {},
        }
        self.frame = frame
        canvas:fill(BB.COLOR_WHITE)
        local title = self.board.puzzle.title
        if complete then title = title .. "  ·  Solved" end
        text(canvas, title, self.titleFace, rectangle(l.pad, l.pad + l.button, l.width - 2 * l.pad, l.title),
            BB.COLOR_BLACK, true)
        local status = self.overview and "Tap the overview to jump to a location" or string.format(
            "Rows %d–%d of %d   ·   Columns %d–%d of %d", self.row, self.row + l.rows - 1, self.board.height,
            self.column, self.column + l.columns - 1, self.board.width)
        text(canvas, status, self.clueFace, rectangle(l.pad, l.footerY - l.status, l.width - 2 * l.pad, l.status),
            BB.COLOR_BLACK)
    end
    self:paintButtons(canvas, frame, full)
    if self.overview then self:paintOverview(canvas) else self:paintGrid(canvas, frame, full) end
    -- KOReader may repaint after dismissing a dialog. Copy the whole background so
    -- cached cells also replace pixels that were covered by an overlay.
    bb:blitFrom(canvas)
end

function Game:showClues(row, column)
    local lines = {}
    local function addLine(label, clues, status)
        lines[#lines + 1] = { label = label, clues = clues, status = status }
    end
    if row then addLine("Row " .. row, self.board.puzzle.rowClues[row], self.board.rows[row]) end
    if column then addLine("Column " .. column, self.board.puzzle.columnClues[column], self.board.columns[column]) end
    local dialog
    dialog = ButtonDialog:new{
        modal = true,
        width_factor = 0.72,
        buttons = {{ { text = "Close", callback = function() UIManager:close(dialog) end } }},
    }
    dialog:addWidget(CluePanel:new{ lines = lines, face = self.clueFace, width = dialog:getAddedWidgetAvailableWidth() })
    UIManager:show(dialog)
end

function Game:showCheckpoint()
    local checkpoint = self.store.records[self.board.puzzle.id].checkpoint
    local dialog
    dialog = ButtonDialog:new{
        title = "Puzzle checkpoint",
        modal = true,
        buttons = {
            {{ text = checkpoint and "Replace checkpoint" or "Save checkpoint", callback = function()
                UIManager:close(dialog)
                local ok, err = self.store:saveCheckpoint(self.board)
                UIManager:show(InfoMessage:new{ text = ok and "Checkpoint saved."
                    or ("Could not save the checkpoint.\n" .. tostring(err)) })
            end }},
            {{ text = "Restore checkpoint", enabled = checkpoint ~= nil, callback = function()
                UIManager:close(dialog)
                self:changed(self.board:restoreCheckpoint(checkpoint), true)
            end }},
            {{ text = "Cancel", callback = function() UIManager:close(dialog) end }},
        },
    }
    UIManager:show(dialog)
end

function Game:checkSolution()
    if not self.board.solution then return self.fetchSolution(self) end
    local errors, remaining = self.board:checkSolution()
    local message
    if errors == 1 then message = "1 marked cell disagrees with the solution."
    elseif errors > 1 then message = string.format("%d marked cells disagree with the solution.", errors)
    elseif remaining > 0 then message = "No mistakes so far."
    else message = "Your grid matches the solution." end
    UIManager:show(InfoMessage:new{ text = message })
end

function Game:save(silent)
    local ok, err = self.store:saveGame(self.board, self, self.pendingSave)
    if ok then
        self.pendingSave, self.pendingViewSave, self.reportedSaveError = nil, nil, nil
    else
        self.pendingViewSave = true
        if not silent and not self.reportedSaveError then
            self.reportedSaveError = true
            UIManager:show(InfoMessage:new{ text = "Progress could not be saved.\n" .. tostring(err) })
        end
    end
    return ok
end

function Game:changed(changes, refreshAll)
    if not changes then return end
    self.pendingSave = true
    local complete = self.board:isComplete()
    if self.suspended then
        self.wasComplete = complete
        return
    end
    local saved = self:save()
    if refreshAll or self.overview or complete ~= self.wasComplete then
        self.frame = nil
        UIManager:setDirty(self, "ui")
    else
        self:refreshStroke(changes)
    end
    if complete and not self.wasComplete and saved then
        UIManager:show(InfoMessage:new{ text = "Puzzle solved", timeout = 2 })
    end
    self.wasComplete = complete
end

function Game:refreshStroke(changes)
    local l, frame, board = self.layout, self.frame, self.board
    local lastVisibleRow, lastVisibleColumn = self.row + l.rows - 1, self.column + l.columns - 1
    local minRow, maxRow, minColumn, maxColumn = lastVisibleRow + 1, 0, lastVisibleColumn + 1, 0
    local radius = board.clueStates and 1 or 0
    local cluesChanged = false
    for _, change in ipairs(changes) do
        local rowVisible = change.row + radius >= self.row and change.row - radius <= lastVisibleRow
        local columnVisible = change.column + radius >= self.column and change.column - radius <= lastVisibleColumn
        if rowVisible and columnVisible then
            minRow, maxRow = math.min(minRow, change.row - radius), math.max(maxRow, change.row + radius)
            minColumn, maxColumn = math.min(minColumn, change.column - radius), math.max(maxColumn, change.column + radius)
        elseif frame then
            cluesChanged = cluesChanged or (rowVisible and frame.rows[change.row] ~= board.rows[change.row])
                or (columnVisible and frame.columns[change.column] ~= board.columns[change.column])
        end
    end
    if minRow <= maxRow then
        -- Refresh the committed cells even if a fast drag preview already painted them.
        self:preview(minRow, minColumn, maxRow, maxColumn, "ui")
    elseif not frame or cluesChanged or (frame.buttons.undo == "disabled") ~= (#board.undo == 0)
        or (frame.buttons.redo == "disabled") ~= (#board.redo == 0) then
        UIManager:setDirty(self)
    end
end

function Game:moveViewport(row, column)
    self.row, self.column = self.layout:origin(row, column)
    self:save()
    UIManager:setDirty(self, "ui")
end

function Game:onButton(action)
    if action == "library" then return self:onClose() end
    if action == "clues" then
        self:showClues(self.selectedRow or self.row, self.selectedColumn or self.column)
    elseif action == "checkpoint" then
        self:showCheckpoint()
    elseif action == "check" then
        self:checkSolution()
    elseif action == "overview" then
        self.overview = not self.overview
        UIManager:setDirty(self, "ui")
    elseif action == "undo" or action == "redo" then
        if action == "undo" then self:changed(self.board:undoStroke())
        else self:changed(self.board:redoStroke()) end
    elseif action == "minus" or action == "plus" then
        local zoom = self.zoom + (action == "plus" and 1 or -1)
        if zoom >= Viewport.minZoom and zoom <= Viewport.maxZoom then
            self.zoom = zoom
            self:resize()
            self:save()
            UIManager:setDirty(self, "ui")
        end
    elseif self.mode ~= action then
        self.mode = action
        UIManager:setDirty(self)
    end
    return true
end

function Game:markAt(row, column)
    if self.mode == "cycle" then return cycle[self.board:get(row, column)] end
    return marks[self.mode]
end

function Game:selectCell(row, column)
    if row == self.selectedRow and column == self.selectedColumn then return end
    self.selectedRow, self.selectedColumn = row, column
    UIManager:setDirty(self)
end

function Game:onGesture(gesture)
    if self.suspended or Device.screen_saver_mode then return true end
    return InputContainer.onGesture(self, gesture)
end

function Game:onTap(_, gesture)
    if self.motion then self:finishMotion() end
    local x, y, l = gesture.pos.x, gesture.pos.y, self.layout
    for action, button in pairs(self.buttons) do
        if contains(button, x, y) then return self:onButton(action) end
    end
    if self.overview then
        local startX, startY, cell = l:overview()
        if contains(rectangle(startX, startY, self.board.width * cell, self.board.height * cell), x, y) then
            self.overview = false
            self:moveViewport(math.floor((y - startY) / cell) + 1 - math.floor(l.rows / 2),
                math.floor((x - startX) / cell) + 1 - math.floor(l.columns / 2))
        end
        return true
    end
    local row, column = l:cellAt(x, y, self.row, self.column)
    if row then
        self:selectCell(row, column)
        local mark = self:markAt(row, column)
        if mark then
            self.board:beginStroke(row, column, mark)
            self:changed(self.board:finishStroke())
        end
    elseif x >= l.pad and x < l.gridX then
        local clueRow = l:cellAt(l.gridX, y, self.row, self.column)
        if clueRow then self:showClues(clueRow) end
    elseif y >= l.headerY and y < l.gridY then
        local _, clueColumn = l:cellAt(x, l.gridY, self.row, self.column)
        if clueColumn then self:showClues(nil, clueColumn) end
    end
    return true
end

function Game:onHold(_, gesture)
    if not self.overview then
        local row, column = self.layout:cellAt(gesture.pos.x, gesture.pos.y, self.row, self.column)
        if row then self:showClues(row, column) end
    end
    return true
end

function Game:beginMotion(start, multi)
    local l = self.layout
    if self.overview or not start or not contains(rectangle(l.pad, l.headerY,
        l.gridX - l.pad + l.columns * l.cell, l.gridY - l.headerY + l.rows * l.cell), start.x, start.y) then
        return
    end
    local row, column = l:cellAt(start.x, start.y, self.row, self.column)
    self.motion = { start = start, row = self.row, column = self.column, multi = multi,
        paint = not multi and row and self:markAt(row, column) }
    if self.motion.paint then
        self.motion.selectedRow, self.motion.selectedColumn = self.selectedRow, self.selectedColumn
        self:selectCell(row, column)
        if self.board:beginStroke(row, column, self.motion.paint) then
            self:preview(row, column, row, column)
        end
    end
end

function Game:preview(firstRow, firstColumn, lastRow, lastColumn, refreshType)
    if not firstRow then return end
    local l = self.layout
    firstRow, firstColumn = math.max(firstRow, self.row), math.max(firstColumn, self.column)
    lastRow = math.min(lastRow, self.row + l.rows - 1)
    lastColumn = math.min(lastColumn, self.column + l.columns - 1)
    if firstRow > lastRow or firstColumn > lastColumn then return end
    UIManager:setDirty(self, refreshType or "fast", rectangle(l.gridX + (firstColumn - self.column) * l.cell,
        l.gridY + (firstRow - self.row) * l.cell,
        (lastColumn - firstColumn + 1) * l.cell, (lastRow - firstRow + 1) * l.cell))
end

function Game:cancelEdgePan()
    if self.edgePan then
        UIManager:unschedule(self.edgePan)
        self.edgePan = nil
    end
end

function Game:edgeOrigin()
    local l, stroke, motion = self.layout, self.board.stroke, self.motion
    local pos = motion.position
    local row, column, margin = self.row, self.column, math.floor(l.cell / 3)
    -- A drag can reach the edge before it enters a second cell.
    local dx, dy = pos.x - motion.start.x, pos.y - motion.start.y
    local axis = stroke.axis or (math.abs(dx) >= math.abs(dy) and "row" or "column")
    if axis == "row" then
        if pos.x < l.gridX + margin and (stroke.axis or dx < 0) then
            column = column - 1
        elseif pos.x >= l.gridX + l.columns * l.cell - margin and (stroke.axis or dx > 0) then
            column = column + 1
        end
    else
        if pos.y < l.gridY + margin and (stroke.axis or dy < 0) then
            row = row - 1
        elseif pos.y >= l.gridY + l.rows * l.cell - margin and (stroke.axis or dy > 0) then
            row = row + 1
        end
    end
    return l:origin(row, column)
end

function Game:scheduleEdgePan(delay)
    local row, column = self:edgeOrigin()
    if row == self.row and column == self.column then
        self:cancelEdgePan()
    elseif not self.edgePan then
        local function pan()
            if self.edgePan ~= pan then return end
            self.edgePan = nil
            self.row, self.column = self:edgeOrigin()
            self.motion.scrolled = true
            local pos = self.motion.position
            self.board:extendStroke(self.layout:cellAt(pos.x, pos.y, self.row, self.column, true))
            UIManager:setDirty(self, "fast")
            self:scheduleEdgePan(0.22)
        end
        self.edgePan = pan
        UIManager:scheduleIn(delay, pan)
    end
end

function Game:onPan(_, gesture)
    if not self.motion then self:beginMotion(gesture.start_pos) end
    if self.motion and self.motion.paint then
        self.motion.position = gesture.pos
        local row, column = self.layout:cellAt(gesture.pos.x, gesture.pos.y, self.row, self.column, true)
        self:preview(self.board:extendStroke(row, column))
        self:scheduleEdgePan(0.38)
    end
    return true
end

local function round(value) return value >= 0 and math.floor(value + 0.5) or math.ceil(value - 0.5) end

function Game:panMotion(motion, position)
    local cell = self.layout.cell
    local row, column = self.layout:origin(motion.row + round((motion.start.y - position.y) / cell),
        motion.column + round((motion.start.x - position.x) / cell))
    if row ~= self.row or column ~= self.column then
        self.row, self.column = row, column
        motion.scrolled = true
        UIManager:setDirty(self, motion.multi and "fast" or "ui")
    end
end

function Game:finishMotion(position)
    self:cancelEdgePan()
    local motion = self.motion
    self.motion = nil
    if not motion then return end
    if motion.paint then
        if position then
            local row, column = self.layout:cellAt(position.x, position.y, self.row, self.column, true)
            self.board:extendStroke(row, column)
        end
        local changes = self.board:finishStroke()
        if changes then
            self:changed(changes, motion.scrolled)
            return
        end
    elseif position then
        self:panMotion(motion, position)
    end
    if motion.scrolled then
        self.pendingViewSave = true
        if not self.suspended then
            self:save()
            UIManager:setDirty(self, "ui")
        end
    end
end

function Game:cancelSingleTouch()
    local motion = self.motion
    if not motion or motion.multi then return end
    self:cancelEdgePan()
    self.motion = nil
    if motion.paint then
        self.board:cancelStroke()
        self.row, self.column = motion.row, motion.column
        self.selectedRow, self.selectedColumn = motion.selectedRow, motion.selectedColumn
        UIManager:setDirty(self, "ui")
    end
end

function Game:onPanRelease(_, gesture)
    if self.motion and not self.motion.multi then self:finishMotion(gesture.pos) end
    return true
end

function Game:onSwipe(_, gesture)
    if not self.motion then self:beginMotion(gesture.pos) end
    if self.motion and not self.motion.multi then self:finishMotion(gesture.end_pos) end
    return true
end
Game.onMultiSwipe = Game.onSwipe

function Game:onTwoFingerPan(_, gesture)
    self:cancelSingleTouch()
    if not self.motion then
        local start = gesture.start_pos
        if not start and gesture.relative then
            start = { x = gesture.pos.x - gesture.relative.x, y = gesture.pos.y - gesture.relative.y }
        end
        self:beginMotion(start, true)
    end
    if self.motion then self:panMotion(self.motion, gesture.pos) end
    return true
end
Game.onTwoFingerHoldPan = Game.onTwoFingerPan

function Game:onTwoFingerPanRelease(_, gesture)
    self:cancelSingleTouch()
    self:finishMotion(gesture.pos)
    return true
end
Game.onTwoFingerHoldPanRelease = Game.onTwoFingerPanRelease

function Game:onTwoFingerSwipe(_, gesture)
    self:cancelSingleTouch()
    if not self.motion then self:beginMotion(gesture.pos, true) end
    self:finishMotion(gesture.end_pos)
    return true
end

function Game:onInwardPan()
    self:cancelSingleTouch()
    return true
end
Game.onOutwardPan = Game.onInwardPan

function Game:onPinch()
    self:cancelSingleTouch()
    self:finishMotion()
    return true
end
Game.onSpread = Game.onPinch
Game.onRotate = Game.onPinch
Game.onTwoFingerTap = Game.onPinch

function Game:onSuspend()
    -- KOReader has already drawn the screensaver. Finish any stroke in memory.
    self.suspended = true
    self:finishMotion()
end

function Game:saveAfterWake()
    if not self.suspended and UIManager:isWidgetShown(self) and (self.pendingSave or self.pendingViewSave) then
        self:save(true)
    end
end

function Game:refreshAfterWake()
    if self.layout.width ~= screen:getWidth() or self.layout.height ~= screen:getHeight() then
        self:resize()
    end
    self.frame = nil
    UIManager:setDirty(self, "full")
end

function Game:onResume()
    self.suspended = false
    if not Device.screen_saver_mode then self:refreshAfterWake() end
    if self.pendingSave or self.pendingViewSave then
        -- Queue the save from the repaint phase so storage cannot delay the wake refresh.
        UIManager:setDirty(nil, function()
            UIManager:nextTick(self.saveAfterWake, self)
        end)
    end
end

function Game:onOutOfScreenSaver()
    -- A screensaver that waits for a tap or gesture closes after Resume.
    if not self.suspended then self:refreshAfterWake() end
end

function Game:onSetDimensions()
    if self.suspended or Device.screen_saver_mode then return end
    self:finishMotion()
    self:resize()
    UIManager:setDirty(self, "full")
end

function Game:onClose()
    self:finishMotion()
    if self:save() then
        UIManager:close(self, "full")
        self.onExit()
    end
    return true
end

function Game:onCloseWidget()
    self:cancelEdgePan()
    self:freeBuffers()
end

return Game
