local BB = require("ffi/blitbuffer")
local Galaxies = require("kobologicpuzzles/galaxiesboard")
local GalaxiesRender = require("kobologicpuzzles/galaxiesrender")
local Hashi = require("kobologicpuzzles/hashiboard")
local HashiRender = require("kobologicpuzzles/hashirender")
local Nurikabe = require("kobologicpuzzles/nurikabeboard")
local RenderImage = require("ui/renderimage")
local Slitherlink = require("kobologicpuzzles/slitherlinkboard")
local SlitherlinkRender = require("kobologicpuzzles/slitherlinkrender")
local Widget = require("ui/widget/widget")

local Thumbnail = Widget:extend{}
local colors = { [string.byte("#")] = BB.COLOR_BLACK, [string.byte("x")] = BB.COLOR_GRAY_B }

function Thumbnail:render()
    local puzzle, marks = self.record.puzzle, self.record.marks
    if puzzle.game == "galaxies" then
        local board = Galaxies:new(puzzle, marks)
        local scale = (self.dimen.w - 4) / math.max(board.width, board.height)
        self.preview = BB.new(math.ceil(board.width * scale) + 2, math.ceil(board.height * scale) + 2, BB.TYPE_BB8)
        GalaxiesRender.paint(self.preview, board, 1, 1, scale, board:regions(),
            self.record.completed and puzzle.metadata.picture)
        return
    end
    if puzzle.game == "slitherlink" then
        local board = Slitherlink:new(puzzle, marks)
        local scale = (self.dimen.w - 4) / math.max(1, board.width, board.height)
        local width, height = math.ceil(board.width * scale) + 2, math.ceil(board.height * scale) + 2
        self.preview = BB.new(width, height, BB.TYPE_BB8)
        SlitherlinkRender.paint(self.preview, board, 1, 1, scale, nil, true)
        return
    end
    if puzzle.game == "hashi" then
        local size = self.dimen.w - 2
        local longest = math.max(puzzle.width, puzzle.height)
        local cell = math.max(1, math.floor(size / longest))
        local width, height = puzzle.width * cell, puzzle.height * cell
        local source = BB.new(width, height, BB.TYPE_BB8)
        HashiRender.paint(source, Hashi:new(puzzle, marks), 0, 0, cell, { x = 0, y = 0, w = width, h = height })
        if math.max(width, height) <= size then
            self.preview = source
        else
            self.preview = RenderImage:scaleBlitBuffer(source, math.max(1, math.floor(size * puzzle.width / longest)),
                math.max(1, math.floor(size * puzzle.height / longest)))
        end
        return
    end
    if puzzle.game == "nurikabe" and not marks then marks = Nurikabe.initialMarks(puzzle) end
    local source = BB.new(puzzle.width, puzzle.height, BB.TYPE_BB8)
    source:fill(BB.COLOR_WHITE)
    for index = 0, #marks - 1 do
        local color = colors[marks:byte(index + 1)]
        if color then source:setPixel(index % puzzle.width, math.floor(index / puzzle.width), color) end
    end

    local scale = (self.dimen.w - 2) / math.max(puzzle.width, puzzle.height)
    local width = math.max(1, math.floor(scale * puzzle.width + 0.5))
    local height = math.max(1, math.floor(scale * puzzle.height + 0.5))
    if scale > 1 then
        self.preview = source:scale(width, height)
        source:free()
    else
        self.preview = RenderImage:scaleBlitBuffer(source, width, height)
    end
end

function Thumbnail:paintTo(bb, x, y)
    if not self.preview then self:render() end
    local width, height = self.preview:getWidth(), self.preview:getHeight()
    x = x + math.floor((self.dimen.w - width) / 2)
    y = y + math.floor((self.dimen.h - height) / 2)
    bb:blitFrom(self.preview, x, y)
    bb:paintBorder(x - 1, y - 1, width + 2, height + 2, 1, BB.COLOR_GRAY_6)
end

function Thumbnail:free()
    if self.preview then
        self.preview:free()
        self.preview = nil
    end
end

return Thumbnail
