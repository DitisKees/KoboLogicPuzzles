local Blitbuffer = require("ffi/blitbuffer")
local BD = require("ui/bidi")
local Device = require("device")
local Font = require("ui/font")
local Geom = require("ui/geometry")
local HorizontalGroup = require("ui/widget/horizontalgroup")
local HorizontalSpan = require("ui/widget/horizontalspan")
local LeftContainer = require("ui/widget/container/leftcontainer")
local Menu = require("ui/widget/menu")
local Puzzle = require("kobologicpuzzles/puzzle")
local Slitherlink = require("kobologicpuzzles/slitherlinkboard")
local TextBoxWidget = require("ui/widget/textboxwidget")
local TextWidget = require("ui/widget/textwidget")
local Thumbnail = require("kobologicpuzzles/thumbnail")
local TitleBar = require("ui/widget/titlebar")
local VerticalGroup = require("ui/widget/verticalgroup")

local PuzzleMenu = Menu:extend{ modal = true, items_per_page = 10, single_line = true, covers_fullscreen = true }

function PuzzleMenu:init()
    self.custom_title_bar = TitleBar:new{
        width = Device.screen:getWidth(),
        fullscreen = true,
        title = self.title,
        left_icon = BD.mirroredUILayout() and "chevron.right" or "chevron.left",
        left_icon_tap_callback = function() self:onClose() end,
        show_parent = self,
    }
    Menu.init(self)
end

local function rating(value)
    if type(value) == "number" and value >= 1 and value <= 5 then return value .. "/5" end
    return "–"
end

function PuzzleMenu.details(metadata, game, author)
    metadata = type(metadata) == "table" and metadata or {}
    local source = Puzzle.sourceLabel(game, metadata.source, metadata.sourceName, metadata.sourceKind)
    if game == "hashi" or game == "nurikabe" then
        local details = {}
        if source then details[#details + 1] = source end
        if type(author) == "string" and author ~= "" and author ~= metadata.sourceName then
            details[#details + 1] = author
        end
        details[#details + 1] = Puzzle.difficultyNames[metadata.difficulty] or "Difficulty unknown"
        details[#details + 1] = Puzzle.hashiSolvabilityNames[metadata.solvability] or "Solvability unknown"
        return table.concat(details, " · ")
    end
    if game == "fill-a-pix" then
        local details = {}
        if type(author) == "string" and author ~= "" then details[#details + 1] = author end
        local source = Puzzle.fillAPixSources[metadata.source]
        if source then details[#details + 1] = source end
        if type(metadata.quality) == "number" then details[#details + 1] = "Quality " .. rating(metadata.quality) end
        if type(metadata.complexity) == "number" then details[#details + 1] = "Complexity " .. metadata.complexity .. "%" end
        if #details == 0 then return end
        return table.concat(details, " · ")
    end
    if game == "slitherlink" then
        local details = {}
        if Slitherlink.tilingNames[metadata.tiling] then details[#details + 1] = Slitherlink.tilingNames[metadata.tiling] end
        if Slitherlink.difficultyNames[metadata.difficulty] then details[#details + 1] = Slitherlink.difficultyNames[metadata.difficulty] end
        if source then
            if metadata.sourceKind == "generated" or metadata.source == "loopy" then table.insert(details, 1, source)
            else details[#details + 1] = source end
        end
        if type(author) == "string" and author ~= "" then details[#details + 1] = author end
        return #details > 0 and table.concat(details, " · ") or nil
    end
    local details = { source or "WebPBN" }
    if source and metadata.source ~= "webpbn" and type(author) == "string" and author ~= "" and author ~= source then
        details[#details + 1] = author
    end
    if metadata.difficulty ~= nil then details[#details + 1] = "Difficulty " .. rating(metadata.difficulty) end
    if metadata.quality ~= nil then details[#details + 1] = "Quality " .. rating(metadata.quality) end
    details[#details + 1] = Puzzle.nonogramSolvabilityNames[metadata.solvability] or "Solvability unknown"
    return table.concat(details, " · ")
end

function PuzzleMenu:onMenuSelect(item)
    if item.select_enabled ~= false and item.callback then item.callback() end
    return true
end

local function puzzleContent(row, previewSize, padding)
    local item = row.entry
    local height = row.dimen.h
    local width = row.content_width - previewSize - (previewSize > 0 and padding or 0)
    local fontSize = math.min(row.font_size, TextBoxWidget:getFontSizeToFitHeight(height - 2 * row.linesize, 2))
    local detailSize = math.min(row.infont_size, fontSize)
    local status = TextWidget:new{ text = item.mandatory, face = Font:getFace(row.infont, detailSize) }
    local title = TextWidget:new{
        text = row.text:gsub("%s+", " "),
        face = Font:getFace(row.font, fontSize),
        bold = row.bold,
        max_width = width - status:getWidth() - padding,
    }
    local details = TextWidget:new{
        text = item.details,
        face = Font:getFace(row.font, detailSize),
        fgcolor = Blitbuffer.COLOR_GRAY_4,
    }
    while details:getWidth() > width and detailSize > 10 do
        detailSize = detailSize - 1
        details:free()
        details.face = Font:getFace(row.font, detailSize)
    end
    if details:getWidth() > width then details:setMaxWidth(width) end

    local lines = VerticalGroup:new{
        align = "left",
        HorizontalGroup:new{
            align = "center",
            title,
            HorizontalSpan:new{ width = width - title:getWidth() - status:getWidth() },
            status,
        },
        details,
    }
    if previewSize > 0 then
        row.thumbnail = item.preview and Thumbnail:new{
            record = item.preview, dimen = Geom:new{ w = previewSize, h = previewSize },
        }
        lines = HorizontalGroup:new{
            align = "center",
            row.thumbnail or HorizontalSpan:new{ width = previewSize },
            HorizontalSpan:new{ width = padding },
            lines,
        }
    end
    return LeftContainer:new{ dimen = Geom:new{ w = row.content_width, h = height }, lines }
end

function PuzzleMenu:updateItems(selectNumber, noRecalculation)
    Menu.updateItems(self, selectNumber, noRecalculation)
    local screen = Device.screen
    local padding = screen:scaleBySize(8)
    local previewSize = 0
    for _, row in ipairs(self.item_group) do
        if row.entry.preview then
            previewSize = math.max(3, math.min(row.dimen.h - 2 * padding, screen:scaleBySize(64)))
            break
        end
    end
    for _, row in ipairs(self.item_group) do
        if row.entry.details then
            -- Keep Menu's focus, touch targets and separators around the two text lines.
            local underline = row._underline_container
            underline:clear()
            underline[1] = puzzleContent(row, previewSize, padding)
        end
    end
end

return PuzzleMenu
