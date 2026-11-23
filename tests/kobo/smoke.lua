-- Run with KOReader's LuaJIT from its Linux runtime directory and a temporary KO_HOME.
local pluginRoot = assert(arg[1], "Supply the path to kobononogram.koplugin.")
assert(os.getenv("KO_HOME"), "Set KO_HOME to a temporary directory.")
require("setupkoenv")
local home = require("datastorage"):getDataDir()
G_defaults = require("luadefaults"):open()
G_reader_settings = require("luasettings"):open(home .. "/settings.reader.lua")
G_reader_settings:saveSetting("sdl_window", { width = 1080, height = 1429 })
local device = require("device")
require("document/canvascontext"):init(device)
require("ui/bidi").setup()
package.path = pluginRoot .. "/?.lua;" .. package.path

local Board = require("kobononogram/board")
local Geom = require("ui/geometry")
local Store = require("kobononogram/store")
local Sync = require("kobononogram/sync")
local UI = require("ui/uimanager")

local function smallPuzzle(id)
    return { id = id, title = "Bricks & Steps", author = "Sample", width = 3, height = 2,
        rowClues = { { 1, 1 }, { 2 } }, columnClues = { { 1 }, { 1 }, { 2 } } }
end

local function testBoard()
    local puzzle = smallPuzzle(1)
    assert(Board.validPuzzle(puzzle))
    local board = Board:new(puzzle)
    board:beginStroke(1, 1, "#")
    board:finishStroke()
    board:beginStroke(1, 3, "#")
    board:finishStroke()
    board:beginStroke(2, 2, "#")
    board:extendStroke(2, 3)
    assert(#board:finishStroke() == 2 and board:isComplete())
    assert(board:marks() == "#.#.##")
    board:undoStroke()
    assert(board:marks() == "#.#..." and not board:isComplete())
    board:redoStroke()
    assert(board:isComplete())
    board:beginStroke(1, 1, "#")
    board:finishStroke()
    assert(not board:isComplete() and #board.redo == 0)
    assert(not Board.validMarks(puzzle, "#?!..."))
end

local function testSync()
    local root = home .. "/sync"
    local store = assert(Store.open(root))
    assert(store:updateSettings({ server = "http://example.test:8080", target = 3 }))
    assert(store:download(smallPuzzle(1)))
    assert(store:download(smallPuzzle(2)))
    local board = Board:new(store.records[1].puzzle, "#x....")
    assert(store:saveGame(board, { row = 1, column = 1, zoom = 2 }, true))
    local stamp = "2026-11-21T10:00:00+00:00"
    local calls = {}
    local result, err = Sync.run(store, function(method, path, payload)
        calls[#calls + 1] = method .. " " .. path
        if path == "/restore" then
            return {
                { puzzle = smallPuzzle(1), progress = { puzzleId = 1, marks = "......", completed = false } },
                { puzzle = smallPuzzle(3), progress = { puzzleId = 3, marks = "x.#...", completed = false } },
            }
        elseif path == "/progress" then
            assert(#payload.boards == 1 and payload.boards[1].marks == "#x....")
            return { savedCount = 1, backedUpAt = stamp }
        else
            assert(payload.targetCount == 3 and #payload.cachedUnstartedPuzzleIds == 1
                and payload.cachedUnstartedPuzzleIds[1] == 2)
            return nil, "Temporary queue failure"
        end
    end)
    assert(err == "Temporary queue failure" and result.backedUp == 1 and result.restored == 1)
    assert(table.concat(calls, ",") == "GET /restore,POST /progress,POST /offline-queue")
    store = assert(Store.open(root))
    assert(store.records[1].marks == "#x...." and not store.records[1].dirty)
    assert(store.records[3].marks == "x.#..." and store.settings.lastBackup == stamp)
    result, err = Sync.run(store, function(_, path)
        assert(path == "/offline-queue")
        return { puzzles = { smallPuzzle(4), smallPuzzle(5) } }
    end)
    assert(not err and result.added == 2 and store.records[4] and store.records[5])

    assert(store:updateSettings({ target = 30 }))
    local batches = 0
    result, err = Sync.run(store, function(_, path, payload)
        assert(path == "/offline-queue")
        batches = batches + 1
        if batches == 2 then
            assert(payload.targetCount == 30 and #payload.cachedUnstartedPuzzleIds == 18)
            return { puzzles = {} }
        end
        assert(payload.targetCount == 18 and #payload.cachedUnstartedPuzzleIds == 3)
        local puzzles = {}
        for id = 6, 20 do puzzles[#puzzles + 1] = smallPuzzle(id) end
        return { puzzles = puzzles }
    end)
    assert(not err and batches == 2 and result.added == 15)
end

local function squarePuzzle(size)
    local rows, columns = {}, {}
    local function fill(r, c) return math.floor((r + c) / 8) % 2 == 0 end
    local function clues(cells)
        local result, run = {}, 0
        for i = 1, size do
            if cells(i) then run = run + 1
            elseif run > 0 then result[#result + 1], run = run, 0 end
        end
        if run > 0 then result[#result + 1] = run end
        return result
    end
    for i = 1, size do
        rows[i] = clues(function(c) return fill(i, c) end)
        columns[i] = clues(function(r) return fill(r, i) end)
    end
    return { id = size, title = size .. "×" .. size .. " viewport check", author = "Sample",
        width = size, height = size, rowClues = rows, columnClues = columns }
end

local function testGameUI()
    local plugin = dofile(pluginRoot .. "/main.lua"):new{
        ui = { menu = { registerToMainMenu = function() end } },
    }
    plugin:showLibrary()
    assert(plugin.library and plugin.store)
    local menu = plugin.library
    plugin.settings = function() end
    menu:onMenuSelect(menu.item_table[3])
    assert(plugin.library == menu)
    UI:forceRePaint()
    for _, size in ipairs({ 40, 80 }) do
        assert(plugin.store:download(squarePuzzle(size)))
        plugin:updateLibrary()
        for _, item in ipairs(plugin.library.item_table) do
            if item.text == size .. "×" .. size .. " viewport check" then
                plugin.library:onMenuSelect(item)
                break
            end
        end
        local game = assert(plugin.game)
        assert(game.board.width == size)
        local layout = game.layout
        local function point(x, y) return Geom:new{ x = x, y = y, w = 0, h = 0 } end
        local function tap(x, y) assert(game:onGesture({ ges = "tap", pos = point(x, y) })) end
        local start = point(layout.gridX + 12, layout.gridY + 13)
        local finish = point(layout.gridX + 3 * layout.cell + 12, layout.gridY + 13)
        game:onGesture{ ges = "pan", start_pos = start, pos = finish }
        game:onGesture{ ges = "swipe", pos = start, end_pos = finish }
        assert(game.board:marks():sub(1, 4) == "####" and #game.board.undo == 1)
        local cross = game.buttons.cross
        tap(cross.x + 10, cross.y + 10)
        tap(layout.gridX + 12, layout.gridY + layout.cell + 13)
        assert(game.board:get(2, 1) == "x")
        UI:forceRePaint()
        device.screen.bb:writePNG(home .. "/grid-" .. size .. ".png")
        local pan = game.buttons.pan
        tap(pan.x + 10, pan.y + 10)
        local panStart = point(layout.gridX + 5 * layout.cell, layout.gridY + 13)
        local panEnd = point(layout.gridX + 2 * layout.cell, layout.gridY + 13)
        game:onGesture{ ges = "pan", start_pos = panStart, pos = panEnd }
        game:onGesture{ ges = "swipe", pos = panStart, end_pos = panEnd }
        assert(game.column == 4 and game.board:marks():sub(1, 4) == "####")
        local overview = game.buttons.overview
        tap(overview.x + 10, overview.y + 10)
        assert(game.overview)
        UI:forceRePaint()
        local x, y, cell = layout:overview()
        tap(x + (size - 5) * cell, y + (size - 5) * cell)
        assert(not game.overview and game.row > 1 and game.column > 1)
        game:onClose()
        assert(not plugin.game and plugin.library)
    end
    local reopened = assert(Store.open(plugin.store.root))
    local restored = reopened.records[80]
    assert(restored.marks:sub(1, 4) == "####" and restored.marks:sub(81, 81) == "x")
    assert(restored.row > 1 and restored.column > 1)
    UI:forceRePaint()
end

testBoard()
testSync()
testGameUI()
print("Kobo logic, sync recovery, and KOReader UI smoke checks passed. Screenshots in " .. home)
