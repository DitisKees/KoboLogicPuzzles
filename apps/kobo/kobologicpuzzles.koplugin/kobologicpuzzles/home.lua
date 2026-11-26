local Blitbuffer = require("ffi/blitbuffer")
local Button = require("ui/widget/button")
local Device = require("device")
local FocusManager = require("ui/widget/focusmanager")
local Font = require("ui/font")
local FrameContainer = require("ui/widget/container/framecontainer")
local Geom = require("ui/geometry")
local HorizontalGroup = require("ui/widget/horizontalgroup")
local HorizontalSpan = require("ui/widget/horizontalspan")
local LineWidget = require("ui/widget/linewidget")
local Puzzle = require("kobologicpuzzles/puzzle")
local ScrollableContainer = require("ui/widget/container/scrollablecontainer")
local TextWidget = require("ui/widget/textwidget")
local TitleBar = require("ui/widget/titlebar")
local UIManager = require("ui/uimanager")
local VerticalGroup = require("ui/widget/verticalgroup")
local VerticalSpan = require("ui/widget/verticalspan")
local Screen = Device.screen

local Home = FocusManager:extend{ modal = true, covers_fullscreen = true }

local function scale(size)
    return Screen:scaleBySize(size)
end

local function button(home, text, width, callback, options)
    return Button:new{
        id = options.id,
        text = text,
        width = width,
        align = options.align or "left",
        text_font_size = 20,
        text_font_bold = options.bold == true,
        avoid_text_truncation = false,
        bordersize = options.border or 0,
        radius = scale(5),
        padding_h = scale(12),
        padding_v = scale(12),
        show_parent = home,
        callback = callback,
    }
end

function Home:familyCard(family, width)
    local border = math.max(1, scale(1))
    local inner = width - 2 * border
    local heading = TextWidget:new{
        text = family.title, face = Font:getFace("cfont", 21), bold = true,
        max_width = inner - 2 * scale(12),
    }
    local content = VerticalGroup:new{ align = "left", FrameContainer:new{
        bordersize = 0, padding = scale(12), heading,
    } }
    local rows = {}
    for _, game in ipairs(family.games) do
        content[#content + 1] = LineWidget:new{
            dimen = Geom:new{ w = inner, h = border }, background = Blitbuffer.COLOR_GRAY_B,
        }
        local count = self.counts[game] or 0
        local label = Puzzle.gameNames[game]
        if count > 0 then label = label .. "  ·  " .. count .. " offline" end
        local row = button(self, label, inner, function() self.openGame(game) end, { id = game })
        content[#content + 1], rows[#rows + 1] = row, row
    end
    return FrameContainer:new{
        bordersize = border, color = Blitbuffer.COLOR_GRAY_8, radius = scale(7), padding = 0, content,
    }, rows
end

function Home:init()
    if Device:hasKeys() then self.key_events.Close = { { Device.input.group.Back } } end
    self:rebuild()
end

function Home:rebuild(keepScroll)
    local width, height = Screen:getWidth(), Screen:getHeight()
    local margin, gap = scale(18), scale(12)
    local scrollbarWidth = ScrollableContainer:getScrollbarWidth()
    local contentWidth = math.min(width - 2 * margin - scrollbarWidth, scale(820))
    local columns = contentWidth >= scale(620) and 2 or 1
    local buttonWidth = math.floor((contentWidth - gap) / 2)
    local cardWidth = math.floor((contentWidth - gap * (columns - 1)) / columns)
    local previous = self:getFocusItem()
    local offset
    if self.cropping_widget then
        if keepScroll then offset = self.cropping_widget:getScrolledOffset() end
        self.cropping_widget:reset()
    end
    self:clear()
    self.dimen = Geom:new{ x = 0, y = 0, w = width, h = height }

    local title = TitleBar:new{
        width = width, fullscreen = true, title = "Kobo Logic Puzzles",
        subtitle = self.backup, close_callback = function() self:onClose() end, show_parent = self,
    }
    local actionStyle = { align = "center", bold = true, border = math.max(1, scale(1)) }
    local sync = button(self, "Sync now", buttonWidth, self.sync, actionStyle)
    local settings = button(self, "Settings", buttonWidth, self.settings, actionStyle)
    sync.id, settings.id = "sync", "settings"
    self.layout = { { sync, settings } }
    local content = VerticalGroup:new{ align = "left",
        HorizontalGroup:new{ sync, HorizontalSpan:new{ width = gap }, settings },
    }
    if self.resume then
        local record = self.resume
        local label = "Continue  ·  " .. Puzzle.title(record.puzzle)
        local resume = button(self, label, contentWidth, function() self.continueGame(record.puzzle.id) end,
            { id = "continue", bold = true, border = actionStyle.border })
        content[#content + 1] = VerticalSpan:new{ width = gap }
        content[#content + 1] = resume
        self.layout[#self.layout + 1] = { resume }
    end

    local families = {}
    for _, family in ipairs(Puzzle.families) do
        if #family.games > 0 then families[#families + 1] = family end
    end
    for first = 1, #families, columns do
        local group = HorizontalGroup:new{ align = "top" }
        local rows = {}
        for index = first, math.min(first + columns - 1, #families) do
            if index > first then group[#group + 1] = HorizontalSpan:new{ width = gap } end
            local card, buttons = self:familyCard(families[index], cardWidth)
            group[#group + 1] = card
            for row, game in ipairs(buttons) do
                rows[row] = rows[row] or {}
                rows[row][#rows[row] + 1] = game
            end
        end
        for _, row in ipairs(rows) do self.layout[#self.layout + 1] = row end
        content[#content + 1] = VerticalSpan:new{ width = gap }
        content[#content + 1] = group
    end

    local scrollHeight = math.max(1, height - title:getHeight() - 2 * margin)
    local overflow = math.max(0, content:getSize().h - scrollHeight)
    local scrollWidth = contentWidth + (overflow > 0 and scrollbarWidth or 0)
    self.cropping_widget = ScrollableContainer:new{
        dimen = Geom:new{ w = scrollWidth, h = scrollHeight }, show_parent = self,
        swipe_full_view = false, content,
    }
    if offset then
        offset.y = math.min(offset.y, overflow)
        self.cropping_widget:setScrolledOffset(offset)
    end
    self[1] = FrameContainer:new{
        background = Blitbuffer.COLOR_WHITE, bordersize = 0, padding = 0,
        width = width, height = height,
        VerticalGroup:new{
            title, VerticalSpan:new{ width = margin }, self.cropping_widget,
        },
    }
    local x, y = 1, 1
    if previous then
        for row, buttons in ipairs(self.layout) do
            for col, item in ipairs(buttons) do
                if item.id == previous.id then x, y = col, row end
            end
        end
    end
    self:moveFocusTo(x, y, self.NOT_UNFOCUS + self.FOCUS_ONLY_ON_NT)
end

function Home:onScreenResize()
    if self.dimen.w == Screen:getWidth() and self.dimen.h == Screen:getHeight() then return end
    self:rebuild()
    UIManager:setDirty(self, "full")
end

Home.onSetDimensions = Home.onScreenResize

function Home:onFocusMove(args)
    local handled = FocusManager.onFocusMove(self, args)
    local scroll, item = self.cropping_widget, self:getFocusItem()
    if not item or not scroll.dimen.y then return handled end
    local crop = scroll:getCropRegion()
    if crop.h then
        local top = item.dimen.y - crop.y
        local bottom = top + item.dimen.h - crop.h
        local delta = top < 0 and top or math.max(0, bottom)
        if delta ~= 0 then scroll:_scrollBy(0, delta) end
    end
    return handled
end

function Home:onClose()
    UIManager:close(self, "ui")
    return true
end

function Home:onCloseWidget()
    self.cropping_widget:reset()
    self:clear()
    self.onExit()
end

return Home
