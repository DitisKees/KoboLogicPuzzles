local Blitbuffer = require("ffi/blitbuffer")
local Device = require("device")
local Font = require("ui/font")
local Geom = require("ui/geometry")
local HorizontalGroup = require("ui/widget/horizontalgroup")
local HorizontalSpan = require("ui/widget/horizontalspan")
local LeftContainer = require("ui/widget/container/leftcontainer")
local Menu = require("ui/widget/menu")
local Puzzle = require("kobologicpuzzles/puzzle")
local TextBoxWidget = require("ui/widget/textboxwidget")
local TextWidget = require("ui/widget/textwidget")
local Thumbnail = require("kobologicpuzzles/thumbnail")
local VerticalGroup = require("ui/widget/verticalgroup")

local PuzzleMenu = Menu:extend{ modal = true, items_per_page = 10, single_line = true }
local function rating(value)
    if type(value) == "number" and value >= 1 and value <= 5 then return value .. "/5" end
    return "–"
end

function PuzzleMenu.details(metadata, game, author)
    metadata = type(metadata) == "table" and metadata or {}
    if game == "hashi" then
        return (Puzzle.difficultyNames[metadata.difficulty] or "Difficulty unknown") .. " · "
            .. (Puzzle.hashiSolvabilityNames[metadata.solvability] or "Solvability unknown")
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
    return string.format("Difficulty %s · Quality %s · %s",
        rating(metadata.difficulty), rating(metadata.quality),
        Puzzle.nonogramSolvabilityNames[metadata.solvability] or "Solvability unknown")
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
