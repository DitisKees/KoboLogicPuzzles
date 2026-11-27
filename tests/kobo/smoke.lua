-- Run with KOReader's LuaJIT from its Linux runtime directory and a temporary KO_HOME.
local pluginRoot = assert(arg[1], "Supply the path to kobologicpuzzles.koplugin.")
assert(os.getenv("KO_HOME"), "Set KO_HOME to a temporary directory.")
require("setupkoenv")
local home = require("datastorage"):getDataDir()
G_defaults = require("luadefaults"):open()
G_reader_settings = require("luasettings"):open(home .. "/settings.reader.lua")
G_reader_settings:saveSetting("sdl_window", { width = 1080, height = 1429 })
G_reader_settings:saveSetting("virtual_keyboard_enabled", true)
local device = require("device")
require("document/canvascontext"):init(device)
require("ui/bidi").setup()
package.path = pluginRoot .. "/?.lua;" .. package.path

local Board = require("kobologicpuzzles/board")
local FillAPix = require("kobologicpuzzles/fillapixboard")
local API = require("kobologicpuzzles/api")
local Event = require("ui/event")
local Geom = require("ui/geometry")
local Galaxies = require("kobologicpuzzles/galaxiesboard")
local Hashi = require("kobologicpuzzles/hashiboard")
local Nurikabe = require("kobologicpuzzles/nurikabeboard")
local Puzzle = require("kobologicpuzzles/puzzle")
local Store = require("kobologicpuzzles/store")
local Slitherlink = require("kobologicpuzzles/slitherlinkboard")
local Sync = require("kobologicpuzzles/sync")
local UI = require("ui/uimanager")
local Update = require("kobologicpuzzles/update")
local UpdateArchive = require("kobologicpuzzles/updatearchive")
local Viewport = require("kobologicpuzzles/viewport")

local function smallPuzzle(id)
    return { id = id, title = "Bricks & Steps", author = "Sample", width = 3, height = 2,
        rowClues = { { 1, 1 }, { 2 } }, columnClues = { { 1 }, { 1 }, { 2 } } }
end

local function wireNonogram(id)
    local puzzle = smallPuzzle(id)
    puzzle.id, puzzle.game, puzzle.schemaVersion = "nonogram-webpbn-" .. id, "nonogram", 1
    return puzzle
end

local function wireFillAPix(number, width, height)
    local id = "fill-a-pix-fillapixnet-" .. number
    local clues = { 2, 3, 1, false, 3, 1 }
    if width then
        clues = { 1 }
        for index = 2, width * height do clues[index] = false end
    end
    return { id = id, game = "fill-a-pix", schemaVersion = 1, title = "Picture " .. number, author = "Sample",
        width = width or 3, height = height or 2, clues = clues, metadata = { source = "fillapixnet" } }
end

local function hashiFixture(size, suffix, points, usesEdge)
    local puzzle = { id = "hashi-local-" .. string.rep(suffix, 64), game = "hashi", schemaVersion = 1,
        width = size, height = size, islands = {}, edges = {}, solution = {},
        metadata = { difficulty = "hard", solvability = "advanced_logic" } }
    for _, p in ipairs(points) do
        puzzle.islands[#puzzle.islands + 1] = { x = p[1], y = p[2], clue = 0 }
    end
    for first, a in ipairs(puzzle.islands) do
        local horizontal, vertical = false, false
        for second = first + 1, #puzzle.islands do
            local b = puzzle.islands[second]
            if (a.y == b.y and not horizontal) or (a.x == b.x and not vertical) then
                horizontal, vertical = horizontal or a.y == b.y, vertical or a.x == b.x
                local count = usesEdge(a, b) and 1 or 0
                puzzle.edges[#puzzle.edges + 1] = { first = first - 1, second = second - 1 }
                puzzle.solution[#puzzle.solution + 1] = count
                a.clue, b.clue = a.clue + count, b.clue + count
            end
        end
    end
    assert(Hashi.validPuzzle(puzzle))
    return puzzle
end

local function hashiSquare(suffix)
    return hashiFixture(20, suffix, {{0,0}, {0,4}, {4,0}, {4,4}}, function() return true end)
end

local function testHashiRules()
    local puzzle = hashiSquare("a")
    local board = Hashi:new(puzzle, "2..2")
    assert(board.degreesMatch and not board:isComplete())
    assert(not Hashi.validMarks(puzzle, "2..2", true))
    assert(board:restart() and board:marks() == "...." and board:undoStroke() and board:marks() == "2..2")
    assert(board:restoreCheckpoint("1111") and board:isComplete() and board:checkSolution() == 0)
    assert(board:edit(1, 2, "noBridge") and board:marks() == "x111" and board:checkSolution() == 1)
    assert(board:edit(1, 2, "clear") and board:marks() == ".111" and board:checkSolution() == 0)
    assert(not board:edit(1, 4, "bridge"))
    local bad = Hashi.encodeMarks("1x..")
    assert(Hashi.decodeMarks(puzzle, bad, false) == "1x..")
    bad[2].edge = bad[1].edge
    assert(not Hashi.decodeMarks(puzzle, bad, false))

    puzzle.solution[1] = 2
    puzzle.islands[1].clue, puzzle.islands[2].clue = 3, 3
    assert(Hashi.validPuzzle(puzzle))
    local errors, complete = Hashi:new(puzzle, "1..."):checkSolution()
    assert(errors == 0 and not complete)
    assert(Hashi:new(puzzle, "x..."):checkSolution() == 1)
    assert(Hashi:new(puzzle, ".2.."):checkSolution() == 1)

    puzzle = hashiFixture(20, "b", {{0,0}, {0,2}, {0,4}, {2,0}, {2,4}, {4,0}, {4,2}, {4,4}},
        function(a, b) return (a.x == b.x and a.x ~= 2) or (a.y == b.y and a.y ~= 2) end)
    board = Hashi:new(puzzle)
    assert(board:edit(2, 7, "bridge") and board:checkSolution() == 1)
    local unchanged = board:marks()
    local changes, err = board:edit(4, 5, "bridge")
    assert(not changes and err and board:marks() == unchanged and #board.undo == 1)
    assert(not board:edit(1, 3, "bridge"))
    assert(board:edit(2, 7, "noBridge") and board:checkSolution() == 0)
    puzzle.edges[1].second = 2
    assert(not Hashi.validPuzzle(puzzle))
end

local function testHashiSync()
    local root = home .. "/mixed-sync"
    local store = assert(Store.open(root))
    local games = assert(API.capabilities(function()
        return { apiVersion = 2, schemaVersion = 1, games = { "nonogram", "hashi" } }
    end))
    local unsupported, capabilityError = API.capabilities(function() return nil, "Not found", 404 end)
    assert(not unsupported and capabilityError:find("puzzle API v2", 1, true))
    local nonogramOnly = assert(API.capabilities(function()
        return { apiVersion = 2, schemaVersion = 1, games = { "nonogram" } }
    end))

    assert(store:updateSettings({ server = "http://example.test:8080",
        target = 1, hashiTarget = 3, lastGame = "hashi" }))
    assert(store:setFilters("hashi", { width = 20, height = 20, difficulty = "hard", solvability = "advanced_logic" }))
    assert(store:setFilters("nonogram", { width = 3 }))
    local localHashi, savedHashi, queuedHashi = hashiSquare("c"), hashiSquare("d"), hashiSquare("e")
    assert(store:download(smallPuzzle(7)) and store:download(localHashi))
    local view = { row = 3, column = 4, zoom = 3 }
    local board = Hashi:new(localHashi, "x...")
    assert(store:saveGame(board, view, true) and store:saveCheckpoint(board))
    board = Board:new(store.records[7].puzzle, "#x....")
    assert(store:saveGame(board, view, true) and store:saveCheckpoint(board))
    assert(io.open(root .. "/7.json", "rb")):close()
    local stamp = "2026-11-22T10:00:00+00:00"
    local result, err = Sync.run(store, function(_, path)
        if path == "/restore" then
            return {{ puzzle = savedHashi, progress = { schemaVersion = 1, puzzleId = savedHashi.id,
                marks = Hashi.encodeMarks("1111"), completed = true, backedUpAt = stamp } }}
        end
        assert(path == "/progress")
        return nil, "Connection interrupted"
    end, games)
    assert(err == "Connection interrupted" and result.restored == 1)
    store = assert(Store.open(root))
    assert(store.records[localHashi.id].dirty and store.records[7].dirty)
    assert(store.records[localHashi.id].checkpoint == "x..." and store.records[7].checkpoint == "#x....")
    assert(store.records[localHashi.id].column == view.column and store.records[7].row == view.row)
    assert(store.records[savedHashi.id].completed and store.settings.lastBackup == stamp)
    local uploaded, queues = {}, {}
    result, err = Sync.run(store, function(_, path, payload)
        if path == "/progress" then
            for _, upload in ipairs(payload.boards) do uploaded[upload.puzzleId] = upload end
            return { savedCount = 2, backedUpAt = stamp }
        end
        assert(path == "/offline-queue")
        queues[payload.game] = payload
        if payload.game == "hashi" then return { puzzles = { queuedHashi }, remaining = 2 } end
        return { puzzles = { wireNonogram(8) }, remaining = 0 }
    end, games)
    assert(not err and result.backedUp == 2 and result.added == 2 and result.shortfalls.hashi == 2)
    assert(uploaded["nonogram-webpbn-7"].marks == "#x....")
    assert(uploaded[localHashi.id].marks[1].noBridge and uploaded[localHashi.id].marks[1].edge == 0)
    assert(queues.nonogram.targetCount == 1 and queues.nonogram.width == 3 and not queues.nonogram.difficulty)
    assert(queues.hashi.targetCount == 3 and queues.hashi.width == 20 and queues.hashi.height == 20
        and queues.hashi.difficulty == "hard" and queues.hashi.solvability == "advanced_logic")
    assert(#queues.nonogram.excludedPuzzleIds == 1 and #queues.hashi.excludedPuzzleIds == 2)
    store = assert(Store.open(root))
    assert(store.settings.lastGame == "hashi" and #store:list("hashi") == 3 and #store:list("nonogram") == 2)
    assert(not store.records[7].dirty and not store.records[localHashi.id].dirty)
    assert(store:saveGame(Hashi:new(localHashi, "...."), view, true))
    result, err = Sync.run(store, function() error("An unsupported game was sent to the server") end, nonogramOnly)
    assert(not err and result.pending == 1 and store.records[localHashi.id].dirty)

    store = assert(Store.open(home .. "/fresh-restore"))
    local server = "http://example.test:8080"
    local fillAPix = wireFillAPix(1)
    assert(store:updateSettings({ server = server, restoredV2Server = server, restoredV2Games = "nonogram,hashi",
        target = 0, hashiTarget = 0 }))
    result, err = Sync.run(store, function(_, path)
        assert(path == "/restore")
        return {
            { puzzle = wireNonogram(7), progress = { schemaVersion = 1, puzzleId = "nonogram-webpbn-7",
                marks = "#x....", completed = false } },
            { puzzle = fillAPix, progress = { schemaVersion = 1, puzzleId = fillAPix.id, marks = "#xx#x#", completed = true } },
            { puzzle = { game = "future-game" } },
            { puzzle = localHashi, progress = { schemaVersion = 1, puzzleId = localHashi.id,
                marks = Hashi.encodeMarks("x..."), completed = false } },
        }
    end, games)
    assert(not err and result.restored == 3 and result.backedUp == 0)
    store = assert(Store.open(store.root))
    assert(store.records[7].marks == "#x...." and store.records[localHashi.id].marks == "x...")
    assert(store.records[fillAPix.id].completed and store.records[fillAPix.id].marks == "#xx#x#")
    assert(#store:list() == 3 and store.settings.restoredV2Games == "nonogram,hashi,fill-a-pix,slitherlink,nurikabe,galaxies")
end

local function testFillAPixRulesAndSync()
    local puzzle = wireFillAPix(2)
    local board = FillAPix:new(puzzle)
    local zero = FillAPix:new({ width = 1, height = 1, clues = { 0 } })
    assert(zero:isComplete() and zero.clueStates[1] == "matched")
    zero:beginStroke(1, 1, "x")
    zero:finishStroke()
    assert(zero:isComplete() and zero.clueStates[1] == "satisfied")
    assert(Puzzle.fromWire(puzzle) and not Puzzle.localId("fill-a-pix-fillapixnet-02"))
    assert(FillAPix.validSolution(puzzle, "#..#.#") and not FillAPix.validSolution(puzzle, "#.#..#"))
    puzzle.clues[1] = 5
    assert(not Puzzle.fromWire(puzzle))
    puzzle.clues[1] = 2
    board:beginStroke(1, 1, "#")
    board:extendStroke(2, 1)
    board:finishStroke()
    assert(board:marks() == "#..#.." and board.clueStates[1] == "matched" and not board:isComplete())
    local checkpoint = board:marks()
    board:beginStroke(1, 3, "#")
    board:finishStroke()
    assert(board:isComplete() and board:marks() == "#.##..")
    board:beginStroke(2, 3, "#")
    board:finishStroke()
    assert(board.clueStates[3] == "conflict" and not board:isComplete())
    assert(board:undoStroke() and board:isComplete())
    assert(board:redoStroke() and board.conflicts > 0)
    assert(board:restoreCheckpoint(checkpoint) and board.conflicts == 0)
    assert(not Puzzle.decodeProgress(puzzle, { schemaVersion = 1, puzzleId = puzzle.id, marks = checkpoint, completed = true }))

    local root = home .. "/fillapix-sync"
    local store = assert(Store.open(root))
    local games = assert(API.capabilities(function()
        return { apiVersion = 2, schemaVersion = 1, advancedFilters = true, games = { "nonogram", "fill-a-pix" } }
    end))
    assert(games["fill-a-pix"] and store:target("fill-a-pix") == 5)
    assert(store:updateSettings({ server = "http://example.test:8080", target = 0, fillAPixTarget = 1 }))
    assert(store:download(puzzle) and store:hasGame("fill-a-pix") and not store:hasGame("hashi"))
    assert(store:saveGame(board, { row = 1, column = 1, zoom = 2 }, true) and store:saveCheckpoint(board))
    assert(store:setFilters("fill-a-pix", { width = "100-2147483647", height = 118, difficulty = "easy" }))
    local large = wireFillAPix(3, 100, 118)
    local result, err = Sync.run(store, function(_, path, payload)
        if path == "/restore" then return {} end
        if path == "/progress" then
            local sent = payload.boards[1]
            assert(sent.puzzleId == puzzle.id and sent.marks == checkpoint and not sent.completed)
            return { savedCount = 1, backedUpAt = "2026-11-26T12:00:00Z" }
        end
        assert(path == "/offline-queue" and payload.game == "fill-a-pix" and payload.targetCount == 1)
        assert(payload.widthFilter == "100-2147483647" and payload.height == 118 and not payload.difficulty)
        assert(payload.excludedPuzzleIds[1] == puzzle.id)
        return { puzzles = { large }, remaining = 0 }
    end, games)
    assert(not err and result.backedUp == 1 and result.added == 1)
    assert(store:saveMetadata(large.id, { source = "fillapixnet", complexity = 17 }))
    store = assert(Store.open(root))
    assert(store.records[large.id].puzzle.clues[11800] == false and store.records[large.id].puzzle.metadata.complexity == 17)
    assert(not store.records[puzzle.id].dirty and store.records[puzzle.id].checkpoint == checkpoint)
end

local function saveFilterInput(title, value)
    for widget in UI:topdown_widgets_iter() do
        if widget.title == title and widget.setInputText then
            widget:setInputText(value)
            widget.buttons[1][2].callback()
            return
        end
    end
    error("Missing filter input: " .. title)
end

local function testQueueFilters()
    local plugin = dofile(pluginRoot .. "/main.lua"):new{
        ui = { menu = { registerToMainMenu = function() end } },
        store = assert(Store.open(home .. "/queue-filters")),
    }
    local function enterFilter(column, title, value)
        plugin:filterSettings("nonogram")
        UI:getTopmostVisibleWidget().buttons[1][column].callback()
        saveFilterInput(title, value)
    end
    enterFilter(1, "Width", "3-4, 6")
    enterFilter(2, "Difficulty", "3.5-4.25")
    plugin:filterSettings("nonogram")
    UI:getTopmostVisibleWidget().buttons[2][2].callback()
    local checklist = UI:getTopmostVisibleWidget()
    checklist.buttons[2][1].callback()
    checklist.buttons[4][1].callback()
    checklist.buttons[7][3].callback()

    local store = plugin.store
    assert(store:updateSettings({ server = "http://example.test:8080", target = 2, hashiTarget = 2 }))
    assert(store:setFilters("hashi", { size = "20-25", difficulty = "medium,hard", solvability = "basic_logic,advanced_logic" }))
    local cached = smallPuzzle(7)
    cached.metadata = { difficulty = 3, solvability = 2 }
    local played, nextHashi = hashiSquare("c"), hashiSquare("d")
    local nextNonogram = wireNonogram(8)
    nextNonogram.metadata = { difficulty = 4.25, solvability = 4 }
    assert(store:download(cached) and store:download(played))
    assert(store:saveGame(Hashi:new(played, "x..."), { row = 1, column = 1, zoom = 2 }, true))
    store = assert(Store.open(store.root))
    local stamp = "2026-11-22T10:00:00+00:00"

    local result, err = Sync.run(store, function(_, path, payload)
        if path == "/restore" then return {} end
        assert(path == "/progress" and payload.boards[1].puzzleId == played.id)
        return { savedCount = 1, backedUpAt = stamp }
    end, { nonogram = true, hashi = true })
    assert(result.backedUp == 1 and result.added == 0 and err == Puzzle.advancedFilterMessage)

    local games = assert(API.capabilities(function()
        return { apiVersion = 2, schemaVersion = 1, games = { "nonogram", "hashi" }, advancedFilters = true }
    end))
    assert(games.advancedFilters)
    result, err = Sync.run(store, function(method, path, payload)
        if method == "GET" then
            assert(path == "/puzzles/metadata?id=nonogram-webpbn-7")
            return { ["nonogram-webpbn-7"] = { difficulty = 3.5, solvability = 2 } }
        end
        assert(path == "/offline-queue" and payload.targetCount == 2)
        if payload.game == "nonogram" then
            assert(payload.widthFilter == "3-4,6" and payload.difficultyFilter == "3.5-4.25" and payload.solvabilityFilter == "2,4")
            assert(payload.cachedUnstartedPuzzleIds[1] == "nonogram-webpbn-7")
            return { puzzles = { nextNonogram }, remaining = 0 }
        end
        assert(payload.sizeFilter == "20-25" and payload.difficultyFilter == "medium,hard"
            and payload.solvabilityFilter == "basic_logic,advanced_logic" and payload.excludedPuzzleIds[1] == played.id)
        return { puzzles = { nextHashi }, remaining = 1 }
    end, games)
    assert(not err and result.added == 2 and result.shortfalls.nonogram == 0 and result.shortfalls.hashi == 1)

    local wrong = hashiSquare("e")
    wrong.metadata.difficulty = "expert"
    result, err = Sync.run(store, function(_, path)
        assert(path == "/offline-queue")
        return { puzzles = { wrong }, remaining = 0 }
    end, { hashi = true, advancedFilters = true })
    assert(result.added == 0 and not store.records[wrong.id] and err:find("invalid puzzle", 1, true))
end

local function testApiTransport()
    local http = require("socket.http")
    local json = require("json")
    local socket = require("socket")
    local trapper = require("ui/trapper")
    local originalRequest = http.request
    local server = "http://example.test:8080"
    local loopId = "slitherlink-" .. string.rep("a", 64)
    http.request = function(options)
        local prefix = server .. "/api/v2"
        assert(options.url:sub(1, #prefix) == prefix)
        local path = options.url:sub(#prefix + 1)
        local reply
        if path == "/puzzles?game=hashi" then
            reply = '{"puzzles":[],"nextBeforeId":null}'
        elseif path == "/restore" then
            reply = "[]"
        elseif path == "/offline-queue" then
            reply = '{"remaining":0,"puzzles":[{"schemaVersion":1,"game":"fill-a-pix","id":"fill-a-pix-janko-1",'
                .. '"title":"Blank clues","width":3,"height":2,"clues":[null,0,null,0,null,null],"solution":null}]}'
        elseif path == "/puzzles/" .. loopId then
            reply = '{"schemaVersion":1,"game":"slitherlink","id":"' .. loopId .. '","width":1,"height":1,'
                .. '"title":"Blank face","vertices":[{"x":0,"y":0},{"x":0,"y":2},{"x":2,"y":0},{"x":2,"y":2}],'
                .. '"edges":[{"first":0,"second":1},{"first":0,"second":2},{"first":1,"second":3},{"first":2,"second":3}],'
                .. '"faces":[{"x":1,"y":1,"edges":[0,1,2,3],"clue":null}],'
                .. '"metadata":{"tiling":"square","source":"loopy","sourceName":"Loopy","faceCount":1},"solution":"####"}'
        else
            assert(path == "/progress" and options.method == "POST")
            local payload = json.decode(assert(options.source()))
            assert(payload.boards[1].puzzleId == "hashi-local-" .. string.rep("a", 64))
            assert(json.util.IsArray(payload.boards[1].marks))
            reply = '{"savedCount":1,"backedUpAt":"2026-11-22T10:00:00Z"}'
        end
        options.sink(reply)
        return 1, 200
    end
    local passed, failure
    trapper:wrap(function()
        passed, failure = pcall(function()
            local page = assert(API.request(server, "GET", "/puzzles?game=hashi"))
            assert(page.nextBeforeId == nil and #page.puzzles == 0)
            assert(#assert(API.request(server, "GET", "/restore")) == 0)
            local queue = assert(API.request(server, "POST", "/offline-queue", { game = "fill-a-pix", targetCount = 1 }))
            local picture = assert(Puzzle.fromWire(queue.puzzles[1]))
            assert(picture.solution == nil and #picture.clues == 6 and picture.clues[1] == false
                and picture.clues[2] == 0 and picture.clues[3] == false and picture.clues[6] == false)
            local loop = assert(Puzzle.fromWire(assert(API.request(server, "GET", "/puzzles/" .. loopId))))
            assert(loop.faces[1].clue == nil and loop.solution == "####")
            local receipt = assert(API.request(server, "POST", "/progress", { schemaVersion = 1,
                boards = { Puzzle.encodeProgress({ puzzleId = hashiSquare("a").id, marks = "....", completed = false }) } }))
            assert(receipt.savedCount == 1)
        end)
    end)
    for _ = 1, 160 do
        if passed ~= nil then break end
        UI:_checkTasks()
        socket.sleep(0.025)
    end
    http.request = originalRequest
    assert(passed, failure or "API subprocess did not return")
end

local function testBoard()
    local puzzle = smallPuzzle(1)
    assert(Board.validPuzzle(puzzle))
    local board = Board:new(puzzle)
    board:beginStroke(1, 1, "#")
    board:finishStroke()
    assert(board:marks() == "#..x.." and not board:isComplete())
    board:beginStroke(1, 3, "#")
    board:finishStroke()
    assert(board:marks() == "#x#x..")
    board:undoStroke()
    assert(board:marks() == "#.#x.." and board.rows[1].complete)
    board:undoStroke()
    assert(board:marks() == "#..x.." and not board.rows[1].complete)
    board:redoStroke()
    assert(board:marks() == "#.#x.." and board.rows[1].complete)
    board:redoStroke()
    assert(board:marks() == "#x#x..")
    board:beginStroke(2, 2, "#")
    board:extendStroke(2, 3)
    assert(#board:finishStroke() == 2 and board:isComplete())
    assert(board:marks() == "#x#x##")
    board:undoStroke()
    assert(board:marks() == "#x#x.." and not board:isComplete())
    board:redoStroke()
    assert(board:isComplete())
    board:beginStroke(1, 1, "#")
    board:finishStroke()
    assert(not board:isComplete() and #board.redo == 0)
    assert(not Board.validMarks(puzzle, "#?!..."))

    local twoLines = { width = 2, height = 2, rowClues = { { 1 }, { 1 } }, columnClues = { { 1 }, { 1 } } }
    local intersecting = Board:new(twoLines, "...x")
    intersecting:beginStroke(1, 1, "#")
    intersecting:finishStroke()
    assert(intersecting:marks() == "#xxx" and #intersecting.undo == 2)
    assert(intersecting.rows[2].impossible and intersecting.columns[2].impossible)
    intersecting:undoStroke()
    assert(intersecting:marks() == "#..x" and intersecting.rows[1].complete and intersecting.columns[1].complete)
    assert(not intersecting.rows[2].impossible and not intersecting.columns[2].impossible)
    intersecting:beginStroke(1, 2, "x")
    intersecting:finishStroke()
    assert(intersecting:marks() == "#x.x")
    intersecting:undoStroke()
    assert(intersecting:marks() == "#..x")
    intersecting:restoreCheckpoint("...x")
    intersecting:restoreCheckpoint("#..x")
    assert(intersecting:marks() == "#..x")

    local lastMove = Board:new(twoLines, "#...")
    lastMove:beginStroke(2, 2, "#")
    lastMove:finishStroke()
    assert(lastMove:marks() == "#xx#" and lastMove:isComplete())
    lastMove:undoStroke()
    assert(lastMove:marks() == "#..#" and lastMove:isComplete())

    local emptyLines = { width = 2, height = 2, rowClues = { {}, { 1 } }, columnClues = { { 1 }, {} } }
    local blank = Board:new(emptyLines)
    assert(blank:marks() == "xx.x")
    blank:undoStroke()
    assert(blank:marks() == "...." and Board:new(emptyLines, blank:marks()):marks() == "....")

    local linePuzzle = { width = 7, height = 1, rowClues = { { 2, 1 } },
        columnClues = { {}, { 1 }, { 1 }, {}, {}, { 1 }, {} } }
    local partial = Board:new(linePuzzle, ".##x...")
    assert(partial.rows[1][1] and not partial.rows[1][2] and not partial.rows[1].complete)
    assert(not partial.rows[1].impossible)
    partial:restoreCheckpoint(".##xxxx")
    assert(partial.rows[1].impossible and partial.columns[6].impossible)
    partial:undoStroke()
    assert(partial:marks() == ".##x..." and not partial.rows[1].impossible)
    partial:redoStroke()
    assert(partial:marks() == ".##xxxx" and partial.rows[1].impossible)
    assert(Board:new(linePuzzle, ".##..#.").rows[1].complete)

    puzzle.solution = "#.#.##"
    local checked = Board:new(puzzle, "#x....")
    local errors, remaining = checked:checkSolution()
    assert(errors == 0 and remaining == 3)
    checked:restoreCheckpoint("x##.##")
    errors, remaining = checked:checkSolution()
    assert(errors == 2 and remaining == 0)
    assert(not Board.validSolution(puzzle, "######"))
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
    local games = { nonogram = true }
    local result, err = Sync.run(store, function(method, path, payload)
        calls[#calls + 1] = method .. " " .. path
        if path == "/restore" then
            return {
                { puzzle = wireNonogram(1), progress = { schemaVersion = 1, puzzleId = "nonogram-webpbn-1", marks = "......", completed = false } },
                { puzzle = wireNonogram(3), progress = { schemaVersion = 1, puzzleId = "nonogram-webpbn-3", marks = "x.#...", completed = false } },
            }
        elseif path == "/progress" then
            assert(payload.schemaVersion == 1 and #payload.boards == 1
                and payload.boards[1].puzzleId == "nonogram-webpbn-1" and payload.boards[1].marks == "#x....")
            return { savedCount = 1, backedUpAt = stamp }
        else
            assert(payload.targetCount == 3 and #payload.cachedUnstartedPuzzleIds == 1
                and payload.cachedUnstartedPuzzleIds[1] == "nonogram-webpbn-2")
            return nil, "Temporary queue failure"
        end
    end, games)
    assert(err == "Temporary queue failure" and result.backedUp == 1 and result.restored == 1)
    assert(table.concat(calls, ",") == "GET /restore,POST /progress,POST /offline-queue")
    store = assert(Store.open(root))
    assert(store.records[1].marks == "#x...." and not store.records[1].dirty)
    assert(store.records[3].marks == "x.#..." and store.settings.lastBackup == stamp)
    result, err = Sync.run(store, function(_, path)
        assert(path == "/offline-queue")
        return { puzzles = { wireNonogram(4), wireNonogram(5) }, remaining = 0 }
    end, games)
    assert(not err and result.added == 2 and store.records[4] and store.records[5])

    assert(store:updateSettings({ target = 30 }))
    local batches = 0
    result, err = Sync.run(store, function(_, path, payload)
        assert(path == "/offline-queue")
        batches = batches + 1
        if batches == 2 then
            assert(payload.targetCount == 30 and #payload.cachedUnstartedPuzzleIds == 18)
            return { puzzles = {}, remaining = 12 }
        end
        assert(payload.targetCount == 18 and #payload.cachedUnstartedPuzzleIds == 3)
        local puzzles = {}
        for id = 6, 20 do puzzles[#puzzles + 1] = wireNonogram(id) end
        return { puzzles = puzzles, remaining = 0 }
    end, games)
    assert(not err and batches == 2 and result.added == 15 and result.shortfalls.nonogram == 12)

    assert(Sync.refreshMetadata(store, function(method, path)
        assert(method == "GET" and path:find("^/puzzles/metadata%?") and path:match("[?&]id=nonogram%-webpbn%-1&"))
        return { ["nonogram-webpbn-1"] = { difficulty = 3.75, quality = 4.25, solvability = 3 } }
    end))
    store = assert(Store.open(root))
    assert(store.records[1].puzzle.metadata.difficulty == 3.75 and store.records[1].marks == "#x....")
    assert(store.records[1].zoom == 2 and not store.records[1].dirty)
    assert(not Sync.refreshMetadata(store, function() return nil, "Not found", 404 end))
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

local function testClueLayout()
    local clues = {}
    for i = 1, 50 do clues[i] = 1 end
    local puzzle = { width = 100, height = 100, rowClues = { clues }, columnClues = { clues } }
    local limited = Viewport:new(600, 360, puzzle, Viewport.maxZoom, 1)
    assert(limited.leftSlots == 2 and limited.leftEllipsis)
    assert(limited.topSlots == 2 and limited.topEllipsis)
    local dense = Viewport:new(600, 360, puzzle, Viewport.maxZoom, Viewport.maxClueLimit)
    assert(dense.leftSlots > limited.leftSlots and dense.topSlots > limited.topSlots)
    assert(dense.rows >= 4 and dense.columns >= 4)
    assert(dense.gridY + dense.rows * dense.cell < dense.footerY - dense.status)
end

local function testPluginDiscovery()
    local lfs = require("libs/libkoreader-lfs")
    local plugins = home .. "/plugin-discovery"
    assert(lfs.mkdir(plugins))
    assert(lfs.mkdir(plugins .. "/kobologicpuzzles.koplugin.previous"))
    assert(lfs.link(pluginRoot, plugins .. "/kobologicpuzzles.koplugin", true))
    local extra = G_reader_settings:readSetting("extra_plugin_paths")
    G_reader_settings:saveSetting("extra_plugin_paths", { plugins })
    local found = {}
    for _, plugin in ipairs(require("pluginloader"):_discover()) do
        if plugin.path:sub(1, #plugins + 1) == plugins .. "/" then found[#found + 1] = plugin.name end
    end
    G_reader_settings:saveSetting("extra_plugin_paths", extra)
    assert(#found == 1 and found[1] == "kobologicpuzzles")
    local plugin = dofile(pluginRoot .. "/main.lua")
    local menu = {}
    plugin:addToMainMenu(menu)
    assert(next(menu) == "kobologicpuzzles" and next(menu, "kobologicpuzzles") == nil)
    assert(menu.kobologicpuzzles.text == "Kobo Logic Puzzles")
end

local function testPluginUpdate()
    local lfs = require("libs/libkoreader-lfs")
    local Archive = require("ffi/archiver")
    local JSON = require("json")
    local http = require("socket.http")
    local function read(path)
        local file = assert(io.open(path, "rb"))
        local contents = file:read("*all")
        file:close()
        return contents
    end
    local function write(path, contents)
        local file = assert(io.open(path, "wb"))
        assert(file:write(contents))
        assert(file:close())
    end
    local installed = home .. "/kobologicpuzzles.koplugin"
    assert(lfs.mkdir(installed))
    write(installed .. "/main.lua", "return 'old'")
    local store = assert(Store.open(home .. "/update-data"))
    assert(store:updateSettings({ server = "http://example.test:8080" }))
    assert(store:download(smallPuzzle(1)))
    local board = Board:new(store.records[1].puzzle, "#x....")
    assert(store:saveGame(board, { row = 1, column = 1, zoom = 2 }, true))
    assert(store:saveCheckpoint(board))
    write(store.root .. "/github-update-token.txt", "github_pat_test\n")

    local archivePath = home .. "/update.zip"
    local function archive(extra)
        local writer = Archive.Writer:new()
        assert(writer:open(archivePath))
        assert(writer:addFileFromMemory("kobologicpuzzles.koplugin/_meta.lua", 'return {\n    version = "1.1.0",\n}'))
        assert(writer:addFileFromMemory("kobologicpuzzles.koplugin/main.lua", "return 'new'"))
        assert(writer:addFileFromMemory("kobologicpuzzles.koplugin/ca-bundle.pem", "CA bundle fixture"))
        assert(writer:addFileFromMemory("kobologicpuzzles.koplugin/kobologicpuzzles/extra.lua", "return true"))
        if extra then assert(writer:addFileFromMemory(extra, "should not be written")) end
        writer:close()
        return read(archivePath)
    end
    local zip = archive()
    local asset = { name = "kobologicpuzzles.koplugin.zip", state = "uploaded", id = 42, size = #zip,
        digest = "sha256:" .. require("ffi/sha2").sha256(zip) }
    local releases = JSON.encode({
        { tag_name = "kobo-v1.10.0", prerelease = true, assets = { asset } },
        { tag_name = "server-v2.0.0", assets = { asset } },
        { tag_name = "kobo-v1.9.0", assets = { { name = "unrelated.koplugin.zip", state = "uploaded" } } },
        { tag_name = "kobo-v1.1.0", assets = { asset } },
    })
    local realRequest = http.request
    local content = zip
    http.request = function(options)
        if options.url:find("api.github.com", 1, true) then
            assert(options.url:find("https://api.github.com/repos/DitisKees/KoboLogicPuzzles/releases", 1, true) == 1)
            assert(options.headers.authorization == "Bearer github_pat_test")
            if options.url:match("/assets/42$") then
                return 1, 302, { location = "https://release-assets.githubusercontent.com/path?signature=test" }
            end
            assert(options.sink(releases))
        else
            assert(options.url == "https://release-assets.githubusercontent.com/path?signature=test")
            assert(not options.headers.authorization)
            assert(options.sink(content))
        end
        return 1, 200, {}
    end
    local release = assert(Update.check(installed, store.root, "1.0.0"))
    assert(release.version == "1.1.0" and Update.check(installed, store.root, "1.1.0") == false)
    assert(Update.download(installed, store.root, release) == zip)
    content = string.rep("0", #zip)
    local badDownload, downloadError = Update.download(installed, store.root, release)
    assert(not badDownload and downloadError:find("checksum"))
    http.request = realRequest

    assert(not UpdateArchive.install(installed, archive("kobologicpuzzles.koplugin/../unexpected"), "1.1.0"))
    assert(not UpdateArchive.install(installed, archive("unrelated.koplugin/extra.lua"), "1.1.0"))
    assert(not UpdateArchive.install(installed, zip, "1.2.0"))
    assert(read(installed .. "/main.lua") == "return 'old'" and not lfs.attributes(installed .. ".previous"))
    assert(UpdateArchive.install(installed, zip, release.version))
    assert(read(installed .. "/main.lua") == "return 'new'")
    assert(read(installed .. ".previous/main.lua") == "return 'old'")
    assert(read(installed .. "/kobologicpuzzles/extra.lua") == "return true")
    local rename = os.rename
    os.rename = function(from, to)
        if to == installed and from:sub(-4) == "/new" then return nil, "simulated storage error" end
        return rename(from, to)
    end
    local installedAgain = UpdateArchive.install(installed, zip, release.version)
    os.rename = rename
    assert(not installedAgain and read(installed .. "/main.lua") == "return 'new'")
    assert(read(installed .. ".previous/main.lua") == "return 'old'")
    store = assert(Store.open(store.root))
    assert(store.settings.server == "http://example.test:8080" and store.records[1].marks == "#x...."
        and store.records[1].checkpoint == "#x....")
end

local function testSuspendResume()
    local Game = require("kobologicpuzzles/game")
    local Event = require("ui/event")
    local Screensaver = require("ui/screensaver")
    local ScreenSaverWidget = require("ui/widget/screensaverwidget")
    local Widget = require("ui/widget/widget")
    local time = require("ui/time")
    local store = assert(Store.open(home .. "/suspend"))
    local function showGame(puzzle, marks)
        assert(store:download(puzzle))
        local board = Board:new(puzzle, marks)
        assert(store:saveGame(board, { row = 1, column = 1, zoom = 2 }, marks ~= nil))
        local game = Game:new{ board = board, record = store.records[puzzle.id], store = store,
            onExit = function() end }
        UI:show(game)
        return game
    end
    local function showScreensaver()
        local saver = ScreenSaverWidget:new{ widget = Widget:new{ dimen = Geom:new{} }, modal = true,
            covers_fullscreen = true }
        Screensaver.screensaver_widget = saver
        device.screen_saver_mode = true
        UI:show(saver)
        UI:forceRePaint()
        return saver
    end
    local function advanceUI()
        for _ = 1, 3 do
            UI:_checkTasks()
            UI:forceRePaint()
        end
    end
    local function point(x, y) return Geom:new{ x = x, y = y, w = 0, h = 0 } end
    local game = showGame(smallPuzzle(1), "#x#x..")
    local layout = game.layout
    local start = point(layout.gridX + layout.cell + 5, layout.gridY + layout.cell + 5)
    local finish = point(start.x + layout.cell, start.y)
    game:onGesture{ ges = "pan", start_pos = start, pos = finish }
    assert(game.motion and game.board.stroke)
    local saver = showScreensaver()
    device:_beforeSuspend()
    assert(UI:getTopmostVisibleWidget() == saver, "A puzzle dialog covered the screensaver during suspend")
    assert(store.records[1].marks == "#x#x..", "Suspend saved the interrupted stroke synchronously")
    assert(not game.motion and not game.board.stroke and game.board:isComplete())
    device:_afterResume()
    assert(UI:getTopmostVisibleWidget() == saver and store.records[1].marks == "#x#x..")
    local undo = { ges = "tap", pos = point(game.buttons.undo.x + 5, game.buttons.undo.y + 5) }
    game:onGesture(undo)
    assert(game.board:isComplete())
    advanceUI()
    assert(UI:getTopmostVisibleWidget() == saver and store.records[1].completed)
    UI:sendEvent(Event:new("Gesture", undo))
    UI:forceRePaint()
    assert(UI:getTopmostVisibleWidget() == game and not device.screen_saver_mode)
    assert(assert(Store.open(store.root)).records[1].marks == "#x#x##")
    UI:sendEvent(Event:new("Gesture", undo))
    assert(game.board:marks() == "#x#x.." and not store.records[1].completed)
    game:onClose()

    game = showGame(squarePuzzle(40))
    layout = game.layout
    start = point(layout.gridX + layout.cell + 5, layout.gridY + 5)
    finish = point(layout.gridX + layout.columns * layout.cell - 1, start.y)
    game:onGesture{ ges = "pan", start_pos = start, pos = finish }
    local edgePan = assert(game.edgePan)
    saver = showScreensaver()
    device:_beforeSuspend()
    assert(not game.motion and not game.edgePan and not UI:unschedule(edgePan))
    edgePan()
    assert(not store.records[40].marks)
    saver:onClose()
    device:_afterResume()
    advanceUI()
    assert(game.column == 1 and store.records[40].marks == game.board:marks())
    UI:sendEvent(Event:new("Gesture", { ges = "tap", pos = point(game.buttons.cross.x + 5, game.buttons.cross.y + 5) }))
    assert(game.mode == "cross")

    game:onGesture{ ges = "two_finger_pan", start_pos = start, pos = point(start.x - layout.cell, start.y) }
    assert(game.column == 2 and store.records[40].column == 1)
    device:_beforeSuspend()
    assert(not game.motion and store.records[40].column == 1)
    UI:forceRePaint()
    local painted
    function game:paintTo(...)
        painted = true
        Game.paintTo(self, ...)
    end
    local actualNow, wakeTime = time.now, time.now()
    time.now = function() return wakeTime end
    device:_afterResume()
    UI:_checkTasks()
    time.now = actualNow
    assert(store.records[40].column == 1, "The pending save ran before the wake redraw")
    UI:forceRePaint()
    assert(painted and store.records[40].column == 1)
    device:_beforeSuspend()
    advanceUI()
    assert(store.records[40].column == 1)
    device:_afterResume()
    advanceUI()
    assert(store.records[40].column == 2 and UI:getTopmostVisibleWidget() == game)
    game:onClose()
end

local function testMoveRedraw()
    local BB = require("ffi/blitbuffer")
    local Game = require("kobologicpuzzles/game")
    local store = assert(Store.open(home .. "/redraw"))
    local puzzle = smallPuzzle(1)
    assert(store:download(puzzle))
    local game = Game:new{ board = Board:new(puzzle), record = store.records[1], store = store,
        onExit = function() end }
    UI:show(game, "full")
    UI:forceRePaint()

    local cellsPainted = 0
    function game:paintCell(...)
        cellsPainted = cellsPainted + 1
        Game.paintCell(self, ...)
    end
    local function verifyRedraw(expected, action)
        UI:forceRePaint()
        assert(cellsPainted == expected, action .. " repainted the wrong number of cells")
        local pixels = BB.tostring(device.screen.bb)
        game.frame = nil
        UI:setDirty(game, "ui")
        UI:forceRePaint()
        assert(BB.tostring(device.screen.bb) == pixels, action .. " differs from a full redraw")
        cellsPainted = 0
    end
    local function point(row, column)
        local l = game.layout
        return Geom:new{ x = l.gridX + (column - 1) * l.cell + 5, y = l.gridY + (row - 1) * l.cell + 5 }
    end
    game:onGesture{ ges = "tap", pos = point(1, 1) }
    assert(game.board:marks() == "#..x..")
    verifyRedraw(2, "Automatic cross")
    game:onGesture{ ges = "tap", pos = point(1, 3) }
    assert(game.board:marks() == "#x#x..")
    verifyRedraw(2, "Selection change and row completion")
    game:onButton("undo")
    verifyRedraw(1, "Undo")
    game:onButton("redo")
    verifyRedraw(1, "Redo")

    game:onButton("cross")
    verifyRedraw(0, "Mode change")
    local start, finish = point(2, 2), point(2, 3)
    game:onGesture{ ges = "pan", start_pos = start, pos = finish }
    verifyRedraw(2, "Drag preview")
    game:onGesture{ ges = "pan_release", pos = finish }
    verifyRedraw(0, "Drag commit after preview")

    local pixels = BB.tostring(device.screen.bb)
    game:onButton("clues")
    UI:forceRePaint()
    UI:close(UI:getTopmostVisibleWidget())
    UI:forceRePaint()
    assert(cellsPainted == 0 and BB.tostring(device.screen.bb) == pixels,
        "Dismissed dialog left pixels on the cached board")
    local canvas = game.buffers.screen
    game:onClose()
    assert(not game.buffers and canvas:getAllocated() == 0)
end

local function homeButton(plugin, id)
    for _, row in ipairs(plugin.home.layout) do
        for _, button in ipairs(row) do
            if button.id == id then return button end
        end
    end
end

local function tapWidget(parent, widget)
    UI:forceRePaint()
    local dimen = widget.dimen
    assert(parent:handleEvent(Event:new("Gesture", { ges = "tap", pos = Geom:new{
        x = dimen.x + math.floor(dimen.w / 2), y = dimen.y + math.floor(dimen.h / 2),
    } })))
end

local function testHomeUI()
    local root = home .. "/home-ui"
    local store = assert(Store.open(root))
    local nonogram = smallPuzzle(1)
    local fill = assert(Puzzle.fromWire(wireFillAPix(1)))
    local hashi = hashiSquare("c")
    assert(store:restore(nonogram, { marks = "#.....", completed = false }))
    assert(store:restore(fill, { marks = "#.....", completed = false }))
    assert(store:updateSettings({ target = 0, hashiTarget = 1, fillAPixTarget = 0 }))
    local plugin = dofile(pluginRoot .. "/main.lua"):new{
        ui = { menu = { registerToMainMenu = function() end } },
        store = store,
    }
    plugin:showHome()
    assert(plugin.home.resume.puzzle.id == nonogram.id)
    tapWidget(plugin.home, homeButton(plugin, "fill-a-pix"))
    plugin.library.item_table[3].callback()
    assert(plugin.game.board:marks() == "#.....")
    local library = plugin.library
    plugin.game:onClose()
    assert(plugin.library == library and UI:getTopmostVisibleWidget() == library)
    tapWidget(library, library.title_bar.left_button)
    assert(not plugin.library and plugin.home.resume.puzzle.id == fill.id)
    plugin.home:onClose()

    plugin.store = assert(Store.open(root))
    assert(plugin.store:updateSettings({ lastGame = "nonogram" }))
    plugin:showHome()
    plugin.online = function(_, operation)
        operation(function(method, path, payload)
            if method == "GET" and path == "/restore" then return {} end
            if method == "GET" and path:match("^/puzzles/metadata%?") then return {} end
            assert(method == "POST" and path == "/offline-queue" and payload.game == "hashi")
            return { puzzles = { hashi }, remaining = 0 }
        end, { nonogram = true, hashi = true, ["fill-a-pix"] = true })
    end
    tapWidget(plugin.home, homeButton(plugin, "sync"))
    assert(UI:getTopmostVisibleWidget().text == "0 backed up · 0 restored · 1 downloaded")
    UI:close(UI:getTopmostVisibleWidget())
    assert(plugin.home.counts.hashi == 1 and plugin.home.resume.puzzle.id == fill.id)
    UI:forceRePaint()
    local nonogramButton, hashiButton = homeButton(plugin, "nonogram"), homeButton(plugin, "hashi")
    assert(nonogramButton.dimen.x == hashiButton.dimen.x and nonogramButton.dimen.y < hashiButton.dimen.y)
    local screen = device.screen
    screen.bb:writePNG(home .. "/home-portrait.png")

    local rotation = screen:getRotationMode()
    screen:setRotationMode(screen.DEVICE_ROTATED_CLOCKWISE)
    plugin.home:handleEvent(Event:new("ScreenResize", screen:getSize()))
    UI:forceRePaint()
    nonogramButton, hashiButton = homeButton(plugin, "nonogram"), homeButton(plugin, "hashi")
    assert(nonogramButton.dimen.x < hashiButton.dimen.x and nonogramButton.dimen.y == hashiButton.dimen.y)
    screen.bb:writePNG(home .. "/home-landscape.png")

    plugin.online = function() error("Continue attempted a network request") end
    tapWidget(plugin.home, homeButton(plugin, "continue"))
    assert(plugin.game.board.puzzle.id == fill.id and plugin.library.title == "Fill-a-Pix library")
    plugin.game:onClose()
    assert(plugin.store:saveGame(FillAPix:new(fill, "#..#.#"), { row = 1, column = 1, zoom = 0 }, true))
    assert(plugin.store.records[fill.id].completed)
    plugin.library:onClose()
    assert(plugin.home.resume.puzzle.id == nonogram.id)
    plugin.home:onClose()
    screen:setRotationMode(rotation)
end

local function testGameUI()
    local plugin = dofile(pluginRoot .. "/main.lua"):new{
        ui = { menu = { registerToMainMenu = function() end } },
    }
    local tools = {}
    plugin:addToMainMenu(tools)
    tools.kobologicpuzzles.callback()
    assert(plugin.home and plugin.store and not plugin.library)
    tapWidget(plugin.home, homeButton(plugin, "settings"))
    local settings = UI:getTopmostVisibleWidget()
    assert(settings.title == "Settings")
    local function enterValue(title, value)
        local input
        for widget in UI:topdown_widgets_iter() do
            if widget.title == title then
                input = widget
                break
            end
        end
        assert(input and input:isKeyboardVisible())
        input._input_widget:setText(value)
        input.buttons[1][2].callback()
    end
    settings.buttons[1][1].callback()
    enterValue("Home server URL", "http://example.test:8080")
    assert(plugin.store.settings.server == "http://example.test:8080" and UI:getTopmostVisibleWidget() == plugin.home)
    assert(plugin.store.settings.clueLimit == Viewport.defaultClueLimit)
    local checked
    plugin.checkPluginUpdate = function() checked = true end
    homeButton(plugin, "settings").callback()
    UI:getTopmostVisibleWidget().buttons[2][1].callback()
    assert(checked and UI:getTopmostVisibleWidget() == plugin.home)
    tapWidget(plugin.home, homeButton(plugin, "nonogram"))
    local menu = assert(plugin.library)
    assert(menu.title == "Nonogram library")
    menu:onMenuSelect(menu.item_table[2])
    settings = UI:getTopmostVisibleWidget()
    assert(settings.title == "Nonogram settings")
    settings.buttons[3][1].callback()
    enterValue("Clues shown per row or column", "2")
    assert(assert(Store.open(plugin.store.root)).settings.clueLimit == 2)

    assert(plugin.store:download(smallPuzzle(1)))
    plugin:showGame(1)
    plugin.online = function(_, operation)
        operation(function(method, path)
            assert(method == "GET" and path == "/puzzles/nonogram-webpbn-1?includeSolution=true")
            local puzzle = wireNonogram(1)
            puzzle.solution = "#.#.##"
            return puzzle
        end)
    end
    plugin.game:onButton("check")
    local check = UI:getTopmostVisibleWidget()
    assert(check.text == "No mistakes so far." and plugin.store.records[1].puzzle.solution == "#.#.##")
    UI:close(check)
    plugin.online = nil
    local game = plugin.game
    local layout = game.layout
    game:onGesture{ ges = "tap", pos = Geom:new{ x = layout.gridX + 12, y = layout.gridY + 12, w = 0, h = 0 } }
    assert(game.board:marks() == "#..x.." and plugin.store.records[1].marks == "#..x..")
    game:onButton("clues")
    local popup = UI:getTopmostVisibleWidget()
    local popupLines = popup._added_widgets[1].lines
    assert(popupLines[1].label == "Row 1" and popupLines[1].status[1] and not popupLines[1].status[2])
    assert(popupLines[2].label == "Column 1" and popupLines[2].status.complete)
    UI:forceRePaint()
    device.screen.bb:writePNG(home .. "/clues.png")
    UI:close(popup)
    game:onButton("undo")
    assert(game.board:marks() == "#....." and plugin.store.records[1].marks == "#.....")
    game:onClose()
    plugin:showGame(1)
    assert(plugin.game.board:marks() == "#....." and plugin.game.board.columns[1].complete)
    plugin.game:onClose()

    local solved, unstarted = smallPuzzle(2), smallPuzzle(3)
    solved.title, unstarted.title = "Solved fixture", "Unstarted fixture"
    assert(plugin.store:download(solved))
    assert(plugin.store:download(unstarted))
    assert(plugin.store:saveGame(Board:new(solved, "#x#.##"), { row = 1, column = 1, zoom = 2 }, true))
    plugin:updateLibrary()
    UI:forceRePaint()
    local items = plugin.library.item_table
    assert(items[3].mandatory == "3×2  16%" and plugin.library.item_group[3].thumbnail.preview)
    assert(items[4].mandatory == "3×2  0%" and not plugin.library.item_group[4].thumbnail)
    assert(items[5].mandatory == "3×2  83%  Solved" and plugin.library.item_group[5].thumbnail.preview)
    local metadata = { difficulty = 3.75, quality = 4.25, solvability = 3 }
    local details = "WebPBN · Difficulty 3.75/5 · Quality 4.25/5 · Moderate lookahead"
    assert(items[3].details:find("Solvability unknown", 1, true))
    assert(plugin.store:saveMetadata(1, metadata))
    plugin:updateLibrary()
    assert(plugin.library.item_table[3].details == details)
    local libraryRow = plugin.library.item_group[3]
    local libraryLines = libraryRow._underline_container[1][1][3]
    assert(libraryLines[2].text == details and not libraryLines[2]:isTruncated())
    assert(libraryLines:getSize().h <= libraryRow.dimen.h)

    plugin.online = function(_, operation)
        operation(function(method, path)
            assert(method == "GET")
            if path == "/restore" then return {} end
            assert(path == "/puzzles?limit=25&game=nonogram")
            local puzzle = wireNonogram(1)
            puzzle.metadata = metadata
            return { puzzles = { puzzle } }
        end, { nonogram = true })
    end
    plugin:browse()
    UI:forceRePaint()
    local catalogRow = plugin.catalog.item_group[2]
    local catalogLines = catalogRow._underline_container[1][1]
    assert(catalogRow.entry.mandatory == "3×2  In progress")
    assert(catalogLines[2].text == details and not catalogLines[2]:isTruncated())
    assert(catalogLines:getSize().h <= catalogRow.dimen.h)
    device.screen.bb:writePNG(home .. "/catalog.png")
    tapWidget(plugin.catalog, plugin.catalog.title_bar.left_button)
    assert(not plugin.catalog and UI:getTopmostVisibleWidget() == menu)
    plugin.online = nil
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
        if size == 40 then
            local original = game.board:marks()
            game:onButton("checkpoint")
            local checkpoint = UI:getTopmostVisibleWidget()
            assert(checkpoint.buttons[2][1].enabled == false)
            checkpoint.buttons[1][1].callback()
            UI:close(UI:getTopmostVisibleWidget())
            assert(assert(Store.open(plugin.store.root)).records[size].checkpoint == original)

            local edgeStart = point(layout.gridX + layout.cell + 12, layout.gridY + 2 * layout.cell + 12)
            local edge = point(edgeStart.x, layout.gridY + layout.cell * layout.rows - 1)
            game:onGesture{ ges = "pan", start_pos = edgeStart, pos = edge }
            for expectedRow = 2, 3 do
                local scheduled = assert(game.edgePan)
                assert(UI:unschedule(scheduled))
                scheduled()
                assert(game.row == expectedRow and game.board:get(expectedRow + layout.rows - 1, 2) == "x")
            end
            local pending = game.edgePan
            game:onGesture{ ges = "pan_release", pos = edge }
            assert(not game.edgePan and (not pending or not UI:unschedule(pending)))
            local guessed = game.board:marks()
            assert(guessed ~= original and #game.board.undo == 3)
            game:onButton("checkpoint")
            checkpoint = UI:getTopmostVisibleWidget()
            assert(checkpoint.buttons[2][1].enabled)
            checkpoint.buttons[2][1].callback()
            assert(game.board:marks() == original)
            game:onButton("undo")
            assert(game.board:marks() == guessed)
            game:onButton("redo")
            assert(game.board:marks() == original)
            game:moveViewport(1, 1)

            game:onButton("fill")
            local right = layout.gridX + layout.columns * layout.cell
            local fillStart = point(right - layout.cell + 4, layout.gridY + 12)
            local fillEdge = point(right - 1, fillStart.y)
            game:onGesture{ ges = "pan", start_pos = fillStart, pos = fillEdge }
            local scheduled = assert(game.edgePan)
            assert(UI:unschedule(scheduled))
            scheduled()
            game:onGesture{ ges = "pan_release", pos = fillEdge }
            assert(game.column == 2 and game.board:get(1, layout.columns + 1) == "#")
            game:onButton("undo")
            assert(game.board:marks() == original)
            game:moveViewport(1, 1)
            game:onButton("cross")

            local panStart = point(layout.gridX + 5 * layout.cell, layout.gridY + 2 * layout.cell)
            local panEnd = point(panStart.x - 3 * layout.cell, panStart.y)
            game:onGesture{ ges = "pan", start_pos = panStart, pos = panEnd }
            assert(game.board:marks() ~= original)
            game:onGesture{ ges = "two_finger_pan", start_pos = panStart, pos = panEnd }
            assert(game.column == 4 and game.mode == "cross" and game.board:marks() == original)
            game:onGesture{ ges = "two_finger_pan_release", pos = panEnd }
            assert(not game.motion and plugin.store.records[size].column == 4)
            game:moveViewport(1, 1)
            game:onGesture{ ges = "two_finger_swipe", pos = panStart, end_pos = panEnd }
            assert(game.column == 4 and game.board:marks() == original)
            game:moveViewport(1, 1)

            local cycle = game.buttons.cycle
            tap(cycle.x + 10, cycle.y + 10)
            local cycleStart = point(layout.gridX + 2 * layout.cell + 12, layout.gridY + 2 * layout.cell + 12)
            for _, mark in ipairs({ "#", "x", "." }) do
                tap(cycleStart.x, cycleStart.y)
                assert(game.board:get(3, 3) == mark)
            end
            local cycleEnd = point(cycleStart.x + 2 * layout.cell, cycleStart.y)
            game:onGesture{ ges = "pan", start_pos = cycleStart, pos = cycleEnd }
            game:onGesture{ ges = "pan_release", pos = cycleEnd }
            assert(game.board:marks():sub(83, 85) == "###")
            game:onButton("undo")
            assert(game.board:marks() == original)
        end
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
    assert(restored.row > 1 and restored.column > 1 and restored.zoom == 2)
    plugin:showGame(80)
    local game = plugin.game
    assert(game.layout.cell == math.floor(29 * game.layout.scale))
    assert(game.layout.leftSlots == 3 and game.layout.leftEllipsis)
    assert(game.layout.topSlots == 3 and game.layout.topEllipsis)
    for zoom = 1, Viewport.minZoom, -1 do
        local before = game.layout.cell
        game:onButton("minus")
        assert(game.zoom == zoom and game.layout.cell < before)
    end
    game:onButton("minus")
    assert(game.zoom == Viewport.minZoom)
    UI:forceRePaint()
    device.screen.bb:writePNG(home .. "/zoomed-out.png")
    game:onClose()
    plugin:showGame(80)
    assert(plugin.game.zoom == Viewport.minZoom)
    plugin.game:onClose()
    UI:forceRePaint()
    assert(plugin.library.item_table[3].mandatory == "80×80  <1%")
    device.screen.bb:writePNG(home .. "/library.png")
    plugin.library:onClose()
    assert(UI:getTopmostVisibleWidget() == plugin.home)
    plugin.home:onClose()
end

local function testHashiUI()
    local BB = require("ffi/blitbuffer")
    local Render = require("kobologicpuzzles/hashirender")
    local plugin = dofile(pluginRoot .. "/main.lua"):new{
        ui = { menu = { registerToMainMenu = function() end } },
        store = assert(Store.open(home .. "/hashi-ui")),
    }
    local small = hashiSquare("a")
    small.solution[1] = 2
    small.islands[1].clue, small.islands[2].clue = 3, 3
    local points = {}
    for x = 0, 49, 3 do
        for y = 0, 49, 3 do points[#points + 1] = { x, y } end
    end
    local large = hashiFixture(50, "f", points, function(a, b) return a.y == b.y or a.x == 0 end)
    assert(plugin.store:download(small) and plugin.store:download(smallPuzzle(1)))
    plugin:showHome()
    tapWidget(plugin.home, homeButton(plugin, "hashi"))
    assert(plugin.store.settings.lastGame == "hashi" and plugin.library.item_table[3].text == Puzzle.title(small))
    assert(#plugin.library.item_table == 3 and plugin.library.item_table[3].details == "Generated by Bridges · Hard · Advanced logic")
    assert(plugin.library.item_table[3].mandatory == "20×20  Not started")
    plugin:filterSettings("hashi")
    UI:getTopmostVisibleWidget().buttons[1][1].callback()
    saveFilterInput("Grid size", "40-50")
    plugin:filterSettings("hashi")
    UI:getTopmostVisibleWidget().buttons[1][2].callback()
    local checklist = UI:getTopmostVisibleWidget()
    checklist.buttons[2][1].callback()
    checklist.buttons[3][1].callback()
    checklist.buttons[5][3].callback()

    local catalogPath = "/puzzles?limit=25&game=hashi&sizeFilter=40-50&difficultyFilter=medium,hard"
    local hashiSummary = { id = large.id, game = "hashi", width = 50, height = 50, metadata = large.metadata }
    local calls = {}
    plugin.online = function(_, operation)
        operation(function(method, path)
            assert(method == "GET")
            calls[#calls + 1] = path
            if path == "/restore" then return {} end
            if path == catalogPath then return { puzzles = { hashiSummary }, nextBeforeId = large.id } end
            if path == catalogPath .. "&beforeId=" .. large.id then return { puzzles = { hashiSummary } } end
            assert(path == "/puzzles/" .. large.id .. "?includeSolution=true")
            return large
        end, { nonogram = true, hashi = true, advancedFilters = true })
    end
    assert(plugin.store:updateSettings({ server = "http://example.test:8080" }))
    plugin:browse()
    plugin.catalog.item_table[3].callback()
    assert(plugin.catalog.item_table[3].text == "Back to newest puzzles")
    plugin.catalog.item_table[2].callback()
    assert(#calls == 4 and calls[1] == "/restore" and plugin.store.records[large.id])
    local game = assert(plugin.game)
    assert(game.grid.x > game.layout.pad and game.layout.width - game.grid.x - game.grid.w > game.layout.pad)
    local function point(x, y)
        local l = game.layout
        return Geom:new{ x = l.gridX + (x - game.column + 1) * l.cell + math.floor(l.cell / 2),
            y = l.gridY + (y - game.row + 1) * l.cell + math.floor(l.cell / 2) }
    end
    local function tap(x, y) assert(game:onGesture{ ges = "tap", pos = point(x, y) }) end
    UI:forceRePaint()
    local function verifyPatch(action)
        local patchArea = 0
        local paint = Render.paint
        Render.paint = function(bb, board, x, y, cell, region, ...)
            patchArea = patchArea + region.w * region.h
            return paint(bb, board, x, y, cell, region, ...)
        end
        UI:forceRePaint()
        Render.paint = paint
        assert(patchArea > 0 and patchArea < game.grid.w * game.grid.h, action .. " redrew the whole board")
        local pixels = BB.tostring(device.screen.bb)
        game.frame = nil
        UI:setDirty(game, "ui")
        UI:forceRePaint()
        assert(BB.tostring(device.screen.bb) == pixels, action .. " differs from a full redraw")
    end
    tap(0, 0)
    assert(game.selectedIsland == 1 and not plugin.store.records[large.id].marks)
    verifyPatch("Island selection")
    tap(0, 3)
    assert(game.board.states[1] == "1" and plugin.store.records[large.id].dirty)
    verifyPatch("Single bridge")
    tap(0, 3)
    assert(game.board.states[1] == "2" and game.board.degree[1] == large.islands[1].clue)
    verifyPatch("Double bridge")
    local firstIsland = point(0, 0)
    assert(game.buffers.screen:getPixel(firstIsland.x, firstIsland.y) == BB.COLOR_GRAY_6)
    tap(3, 0)
    assert(game.board.degree[1] > large.islands[1].clue)
    verifyPatch("Overfilled island")
    game:onButton("undo")
    verifyPatch("Undo")
    tap(0, 3)
    assert(game.board.states[1] == ".")
    verifyPatch("Bridge removal")
    game:onButton("noBridge")
    tap(0, 3)
    assert(game.board.states[1] == "x")
    verifyPatch("Explicit no bridge")
    game:onButton("check")
    assert(UI:getTopmostVisibleWidget().text == "1 marked edge disagrees with the solution.")
    UI:close(UI:getTopmostVisibleWidget())
    game:onButton("checkpoint")
    UI:getTopmostVisibleWidget().buttons[1][1].callback()
    UI:close(UI:getTopmostVisibleWidget())
    game:onButton("clear")
    tap(0, 3)
    assert(game.board.states[1] == ".")
    game:onButton("checkpoint")
    UI:getTopmostVisibleWidget().buttons[2][1].callback()
    assert(game.board.states[1] == "x")
    game:onButton("undo")
    assert(game.board.states[1] == ".")
    game:onButton("redo")
    assert(game.board.states[1] == "x")
    game:onButton("restart")
    assert(game.board.states[1] == "x")
    local confirm = UI:getTopmostVisibleWidget()
    confirm.ok_callback()
    UI:close(confirm)
    assert(game.board.states[1] == ".")
    game:onButton("undo")
    assert(game.board.states[1] == "x")
    local marks = game.board:marks()
    local cell = game.layout.cell
    local function offset(pos, dx, dy) return Geom:new{ x = pos.x + dx, y = pos.y + dy } end
    local neighbor = point(0, 3)
    game:onGesture{ ges = "swipe", pos = firstIsland, end_pos = offset(firstIsland, 0, -cell) }
    assert(game.row == 1 and game.column == 1 and game.board:marks() == marks)
    local nearIsland = offset(firstIsland, -math.floor(cell * 0.75), 0)
    assert(nearIsland.x < game.grid.x)
    local shortSouth = offset(nearIsland, math.floor(cell / 3), cell)
    game:onGesture{ ges = "pan", start_pos = nearIsland, pos = shortSouth }
    assert(game.row == 1 and game.column == 1 and game.board:marks() == marks)
    game:onGesture{ ges = "pan_release", pos = shortSouth }
    assert(game.board.states[1] == "1")
    game:onGesture{ ges = "swipe", pos = neighbor, end_pos = offset(neighbor, 0, -cell) }
    assert(game.board.states[1] == "2")
    game:onGesture{ ges = "multiswipe", pos = firstIsland, end_pos = point(0, 6) }
    assert(game.board.states[1] == ".")
    for _, mark in ipairs({ "2", "1", "x" }) do
        game:onButton("undo")
        assert(game.board.states[1] == mark)
    end

    -- KOReader can classify small movements as taps or hold releases.
    local short = device.screen:scaleByDPI(12)
    game:onGesture{ ges = "touch", pos = firstIsland }
    game:onGesture{ ges = "tap", pos = offset(firstIsland, 1, 0) }
    assert(game.selectedIsland == 1 and game.board:marks() == marks)
    game:onGesture{ ges = "touch", pos = firstIsland }
    game:onGesture{ ges = "tap", pos = offset(firstIsland, short, 0) }
    assert(game.board.states[1] == "x" and game.board.states[2] == "1")
    game:moveViewport(1, 4)
    local westSource = point(3, 0)
    local westEnd = offset(westSource, -cell, 0)
    game:onGesture{ ges = "touch", pos = westSource }
    game:onGesture{ ges = "hold", pos = westSource }
    game:onGesture{ ges = "hold_pan", start_pos = westSource, pos = westEnd }
    game:onGesture{ ges = "hold_release", pos = westEnd }
    assert(game.column == 4 and game.board.states[2] == "2")
    game:onButton("undo")
    game:onButton("undo")
    assert(game.board:marks() == marks)
    game:moveViewport(1, 1)

    local emptyStart, emptyEnd = point(13, 13), point(7, 7)
    game:onGesture{ ges = "pan", start_pos = emptyStart, pos = emptyEnd }
    game:onGesture{ ges = "pan_release", pos = emptyEnd }
    assert(game.row == 7 and game.column == 7 and game.board:marks() == marks)
    game:moveViewport(1, 1)

    local start, finish = point(12, 12), point(6, 6)
    game:onGesture{ ges = "pan", start_pos = start, pos = point(12, 9) }
    game:onGesture{ ges = "two_finger_pan", start_pos = start, pos = finish }
    game:onGesture{ ges = "two_finger_pan_release", pos = finish }
    assert(not game.motion and game.row == 7 and game.column == 7 and game.board:marks() == marks)
    assert(plugin.store.records[large.id].row == 7 and plugin.store.records[large.id].column == 7)
    game:onButton("plus")
    game:onButton("overview")
    UI:forceRePaint()
    device.screen.bb:writePNG(home .. "/hashi-50-overview.png")
    local x, y, cell = game.layout:overview()
    game:onGesture{ ges = "tap", pos = Geom:new{ x = x + 48 * cell, y = y + 48 * cell } }
    assert(not game.overview and game.row > 7 and game.column > 7)
    local row, column, zoom = game.row, game.column, game.zoom
    game:onClose()
    plugin.store = assert(Store.open(plugin.store.root))
    plugin.online = function() error("Offline play attempted a network request") end
    plugin:showGame(large.id)
    assert(plugin.game.row == row and plugin.game.column == column and plugin.game.zoom == zoom)
    assert(plugin.game.board:marks() == marks and plugin.store.records[large.id].checkpoint == marks)
    plugin.game:onClose()
    UI:forceRePaint()
    assert(plugin.library.item_group[3].thumbnail.preview)
    plugin:showGame(small.id)
    game = assert(plugin.game)
    local function verifyLibraryProgress(expected)
        for _, item in ipairs(plugin:libraryItems()) do
            if item.text == Puzzle.title(small) then
                assert(item.mandatory == "20×20  " .. expected)
                return
            end
        end
        error("The Hashi puzzle is missing from the library")
    end
    tap(0, 0)
    game:onButton("noBridge")
    tap(0, 4)
    verifyLibraryProgress("0%")
    game:onButton("bridge")
    tap(0, 4)
    verifyLibraryProgress("20%")
    tap(0, 4)
    verifyLibraryProgress("40%")
    game:changed(game.board:restoreCheckpoint("222."), true)
    verifyLibraryProgress("100%")
    game:onButton("undo")

    tap(4, 0); tap(4, 4); tap(4, 0); tap(0, 4); tap(4, 4)
    assert(game.board:marks() == "2111" and plugin.store.records[small.id].completed)
    verifyLibraryProgress("Solved")
    UI:close(UI:getTopmostVisibleWidget())
    UI:forceRePaint()
    device.screen.bb:writePNG(home .. "/hashi-20-solved.png")
    game:onClose()
    plugin.library:onClose()
    plugin.home:onClose()
end

local function testFillAPixUI()
    local BB = require("ffi/blitbuffer")
    local plugin = dofile(pluginRoot .. "/main.lua"):new{
        ui = { menu = { registerToMainMenu = function() end } },
        store = assert(Store.open(home .. "/fillapix-ui")),
    }
    assert(plugin.store:updateSettings({ server = "http://example.test:8080" }))
    local large, small = wireFillAPix(1, 118, 100), wireFillAPix(2)
    local onlineCalls = 0
    plugin.online = function(_, operation)
        onlineCalls = onlineCalls + 1
        local games = { nonogram = true, ["fill-a-pix"] = true, advancedFilters = true }
        operation(function(method, path)
            assert(method == "GET")
            if path == "/restore" then return {} end
            if path == "/puzzles?limit=25&game=fill-a-pix&widthFilter=101-125" then return { puzzles = { large } } end
            assert(path == "/puzzles/" .. large.id .. "?includeSolution=true")
            return large
        end, games)
    end
    plugin:showHome()
    tapWidget(plugin.home, homeButton(plugin, "fill-a-pix"))
    assert(plugin.store.settings.lastGame == "fill-a-pix" and onlineCalls == 0)
    assert(plugin.library.item_table[3].select_enabled == false)
    plugin.library.item_table[2].callback()
    local settings = UI:getTopmostVisibleWidget()
    assert(settings.buttons[1][1].text:find("(5)", 1, true))
    UI:close(settings)
    plugin:filterSettings("fill-a-pix")
    UI:getTopmostVisibleWidget().buttons[1][1].callback()
    saveFilterInput("Width", "101-125")
    plugin:browse()
    assert(plugin.catalog.item_table[2].details == "Sample · Fill-a-Pix.net")
    plugin.catalog.item_table[2].callback()
    local game = assert(plugin.game)
    assert(game.board.width == 118 and game.layout.leftSlots == 0 and game.layout.topSlots == 0)
    game:onButton("plus")
    game:onButton("overview")
    local x, y, cell = game.layout:overview()
    game:onTap(nil, { pos = { x = x + 117 * cell, y = y + 99 * cell } })
    assert(not game.overview and game.column + game.layout.columns - 1 == 118)
    local row, column, zoom = game.row, game.column, game.zoom
    game:onClose()
    plugin.online = function() error("Offline play attempted a network request") end
    plugin:showGame(large.id)
    game = assert(plugin.game)
    assert(game.row == row and game.column == column and game.zoom == zoom)
    game:onClose()

    assert(plugin.store:download(small))
    plugin:showGame(small.id)
    game = assert(plugin.game)
    local function tap(r, c)
        local layout = game.layout
        assert(game:onGesture{ ges = "tap", pos = Geom:new{
            x = layout.gridX + (c - 0.5) * layout.cell, y = layout.gridY + (r - 0.5) * layout.cell,
        } })
    end
    UI:forceRePaint()
    tap(1, 1)
    tap(2, 1)
    UI:forceRePaint()
    assert(game.frame.clueStates[1] == "matched")
    local pixels = BB.tostring(device.screen.bb)
    game.frame = nil
    UI:setDirty(game, "ui")
    UI:forceRePaint()
    assert(BB.tostring(device.screen.bb) == pixels, "Fill-a-Pix changed clue differs from a full redraw")
    game:onHold(nil, { pos = { x = game.layout.gridX + 2, y = game.layout.gridY + 2 } })
    assert(UI:getTopmostVisibleWidget().text:find("2 filled · 2 undecided", 1, true))
    UI:close(UI:getTopmostVisibleWidget())
    game:onButton("checkpoint")
    UI:getTopmostVisibleWidget().buttons[1][1].callback()
    UI:close(UI:getTopmostVisibleWidget())
    game:onButton("cross")
    tap(1, 2)
    game:onButton("check")
    assert(UI:getTopmostVisibleWidget().text:find("No conflicts", 1, true))
    UI:close(UI:getTopmostVisibleWidget())
    game:onButton("undo")
    assert(game.board:marks() == "#..#..")
    game:onButton("redo")
    assert(game.board:marks() == "#x.#..")
    game:onButton("checkpoint")
    UI:getTopmostVisibleWidget().buttons[2][1].callback()
    assert(game.board:marks() == "#..#..")
    game:onButton("fill")
    tap(2, 3)
    assert(game.board:isComplete() and plugin.store.records[small.id].completed)
    UI:close(UI:getTopmostVisibleWidget())
    game:onButton("restart")
    local confirm = UI:getTopmostVisibleWidget()
    UI:close(confirm)
    confirm.ok_callback()
    assert(game.board:marks() == "......" and not plugin.store.records[small.id].completed)
    game:onButton("undo")
    assert(game.board:isComplete())
    UI:close(UI:getTopmostVisibleWidget())
    game:onClose()
    UI:forceRePaint()
    local last = #plugin.library.item_table
    assert(plugin.library.item_table[last].mandatory == "3×2  Solved" and plugin.library.item_group[last].thumbnail.preview)
    plugin.library:onClose()
    plugin.home:onClose()
end

local function testSlitherlinkUI()
    local puzzle = {
        id = "slitherlink-" .. string.rep("a", 64), game = "slitherlink", schemaVersion = 1,
        width = 1, height = 2, title = "Polygon fixture",
        metadata = { tiling = "snub-square", source = "test-library", sourceName = "Test Library", faceCount = 2,
            difficulty = "tricky" },
        vertices = { {x = 0, y = 0}, {x = 0, y = 40}, {x = 40, y = 0}, {x = 40, y = 40}, {x = 75, y = 20} },
        edges = { {first = 0, second = 1}, {first = 0, second = 2}, {first = 1, second = 3},
            {first = 2, second = 3}, {first = 2, second = 4}, {first = 3, second = 4} },
        faces = { {x = 20, y = 20, clue = 1, edges = { 0, 1, 2, 3 }}, {x = 52, y = 20, clue = 3, edges = { 3, 4, 5 }} },
        solution = "...###",
    }
    assert(Slitherlink.validSolution(puzzle, puzzle.solution) and not Slitherlink.validSolution(puzzle, "####.."))
    local store = assert(Store.open(home .. "/slitherlink-ui"))
    assert(store:updateSettings({ server = "http://example.test:8080", slitherlinkTarget = 0 }))
    local plugin = dofile(pluginRoot .. "/main.lua"):new{
        ui = { menu = { registerToMainMenu = function() end } },
        store = store,
    }
    plugin.slitherlinkSources = { ["test-library"] = "Test Library" }
    plugin:showHome()
    tapWidget(plugin.home, homeButton(plugin, "slitherlink"))
    local library = plugin.library
    library.item_table[2].callback()
    UI:getTopmostVisibleWidget().buttons[2][1].callback()
    local filter = UI:getTopmostVisibleWidget()
    assert(filter.buttons[1][2].text == "Source  ·  Any")
    filter.buttons[1][2].callback()
    local source = UI:getTopmostVisibleWidget()
    assert(source.buttons[2][1].text == "Test Library")
    source.buttons[2][1].callback()
    source.buttons[#source.buttons][3].callback()
    assert(store.settings.filters.slitherlink.source == "test-library")
    assert(store:setFilters("slitherlink", { source = "test-library", tiling = "snub-square", difficulty = "tricky" }))
    assert(Puzzle.catalogQuery("slitherlink", store.settings.filters.slitherlink) ==
        "game=slitherlink&difficulty=tricky&tiling=snub-square&source=test-library")
    plugin.online = function(_, operation)
        operation(function(method, path)
            assert(method == "GET")
            if path == "/restore" then return {} end
            if path == "/puzzles?limit=25&game=slitherlink&difficulty=tricky&tiling=snub-square&source=test-library" then
                return { puzzles = { puzzle } }
            end
            assert(path == "/puzzles/" .. puzzle.id .. "?includeSolution=true")
            return puzzle
        end, { nonogram = true, slitherlink = true, advancedFilters = true })
    end
    plugin:browse()
    assert(plugin.catalog.item_table[2].details == "Snub-Square · Tricky · Test Library")
    plugin.catalog.item_table[2].callback()
    local game = assert(plugin.game)
    local function edgeAt(i)
        local edge = puzzle.edges[i]
        local a, b = puzzle.vertices[edge.first + 1], puzzle.vertices[edge.second + 1]
        local x, y, scale = game:graphOrigin(false)
        return Geom:new{ x = game.grid.x + x + (a.x + b.x) * scale / 2,
            y = game.grid.y + y + (a.y + b.y) * scale / 2 }
    end
    local function tapEdge(i)
        assert(game:onGesture{ ges = "tap", pos = edgeAt(i) })
    end
    UI:forceRePaint()
    tapEdge(4)
    tapEdge(5)
    assert(game.board:marks() == "...##.")
    game:onButton("undo")
    assert(game.board:marks() == "...#..")
    game:onButton("redo")
    assert(game.board:marks() == "...##.")
    game:onButton("cross")
    tapEdge(1)
    game:onButton("check")
    assert(UI:getTopmostVisibleWidget().text == "No mistakes so far.")
    UI:close(UI:getTopmostVisibleWidget())
    assert(store:saveCheckpoint(game.board))
    game:onButton("draw")
    tapEdge(6)
    assert(game.board:isComplete() and store.records[puzzle.id].completed)
    UI:close(UI:getTopmostVisibleWidget())
    UI:forceRePaint()
    device.screen.bb:writePNG(home .. "/slitherlink-solved.png")
    game:onClose()
    assert(library.item_table[3].mandatory == "2 cells  Solved")
    library:onClose()
    plugin.home:onClose()

    local renamed = assert(Store.open(home .. "/slitherlink-ui"))
    assert(renamed.records[puzzle.id].checkpoint == "x..##.")
    assert(renamed.settings.filters.slitherlink.source == "test-library")
    local fresh = assert(Store.open(home .. "/slitherlink-restore"))
    assert(Sync.restore(fresh, function(method, path)
        assert(method == "GET" and path == "/restore")
        return { { puzzle = puzzle, progress = {
            puzzleId = puzzle.id, schemaVersion = 1, marks = renamed.records[puzzle.id].marks, completed = true,
        } } }
    end) == 1)
    assert(fresh.records[puzzle.id].completed)
end

local function testNurikabe()
    local prefix = "nurikabe-"
    local puzzle = { id = prefix .. string.rep("a", 64), game = "nurikabe", schemaVersion = 1,
        title = "Island fixture", width = 2, height = 2, clues = { 1, 0, 0, 0 }, solution = "x###",
        metadata = { source = "local", sourceName = "KoboLogicPuzzles", sourceKind = "generated",
            difficulty = "easy", solvability = "basic_logic" } }
    assert(Puzzle.fromWire(puzzle))
    assert(not Nurikabe.validMarks(puzzle, ".###", true))
    assert(not Nurikabe.validMarks(puzzle, "xxxx", true))
    assert(Nurikabe.validMarks(puzzle, "x###", true))
    assert(Puzzle.sourceLabel("slitherlink", "loopy", "Loopy") == "Generated by Loopy")
    assert(Puzzle.sourceLabel("hashi") == "Generated by Bridges")

    local large = { id = prefix .. string.rep("b", 64), game = "nurikabe", schemaVersion = 1,
        title = "Large Nurikabe", width = 50, height = 50, clues = {}, metadata = puzzle.metadata }
    local solution = {}
    for row = 1, 50 do
        for col = 1, 50 do
            local index = (row - 1) * 50 + col
            local island = row % 2 == 1 and col % 2 == 1
            large.clues[index] = island and 1 or 0
            solution[index] = island and "x" or "#"
        end
    end
    large.solution = table.concat(solution)
    assert(Puzzle.fromWire(large))
    local store = assert(Store.open(home .. "/nurikabe-ui"))
    assert(store:updateSettings({ server = "http://example.test:8080", target = 0, nurikabeTarget = 2 }))
    assert(store:setFilters("nurikabe", { source = "local", width = "2-50" }))
    local result = Sync.run(store, function(method, path, payload)
        if path == "/restore" then return {} end
        assert(method == "POST" and path == "/offline-queue" and payload.game == "nurikabe")
        assert(payload.source == "local" and payload.widthFilter == "2-50")
        return { puzzles = { puzzle, large }, remaining = 0 }
    end, { nurikabe = true, advancedFilters = true })
    assert(result.added == 2)
    local plugin = dofile(pluginRoot .. "/main.lua"):new{
        ui = { menu = { registerToMainMenu = function() end } }, store = store,
    }
    plugin:showHome()
    tapWidget(plugin.home, homeButton(plugin, "nurikabe"))
    local function tap(game, row, col)
        local l = game.layout
        game:onTap(nil, { pos = { x = l.gridX + (col - game.column + 0.5) * l.cell,
            y = l.gridY + (row - game.row + 0.5) * l.cell } })
    end
    plugin:showGame(large.id)
    local game = assert(plugin.game)
    UI:forceRePaint()
    tap(game, 1, 1)
    assert(game.board.cells[1] == "x" and not store.records[large.id].marks)
    tap(game, 1, 2)
    game:onButton("undo")
    game:onButton("redo")
    assert(game.board.cells[2] == "#")
    game:onButton("overview")
    UI:forceRePaint()
    assert(game.overview)
    game:onButton("overview")
    game:onButton("plus")
    game:moveViewport(17, 17)
    assert(game.row > 1 and game.column > 1)
    game:onClose()
    local reopened = assert(Store.open(home .. "/nurikabe-ui"))
    assert(reopened.records[large.id].marks:sub(1, 2) == "x#")
    assert(reopened.records[large.id].row == 17 and reopened.records[large.id].zoom == 3)

    plugin:showGame(puzzle.id)
    game = assert(plugin.game)
    UI:forceRePaint()
    tap(game, 1, 2)
    game:onButton("check")
    assert(UI:getTopmostVisibleWidget().text:find("Undecided cells: 2", 1, true))
    UI:close(UI:getTopmostVisibleWidget())
    assert(store:saveCheckpoint(game.board))
    tap(game, 2, 1)
    tap(game, 2, 2)
    UI:close(UI:getTopmostVisibleWidget())
    assert(game.board:isComplete() and store.records[puzzle.id].completed)
    game:onButton("restart")
    local confirm = UI:getTopmostVisibleWidget()
    UI:close(confirm)
    confirm.ok_callback()
    assert(game.board:marks() == "x..." and not game.board:isComplete())
    game:onButton("undo")
    assert(game.board:isComplete())
    UI:close(UI:getTopmostVisibleWidget())
    game:onClose()
    plugin.library:onClose()
    plugin.home:onClose()

    local fresh = assert(Store.open(home .. "/nurikabe-restore"))
    assert(Sync.restore(fresh, function(method, path)
        assert(method == "GET" and path == "/restore")
        return { { puzzle = puzzle, progress = {
            puzzleId = puzzle.id, schemaVersion = 1, marks = "x###", completed = true,
        } } }
    end) == 1)
    assert(fresh.records[puzzle.id].completed)
end

local function testGalaxies()
    local function id(symbol) return "galaxies-" .. string.rep(symbol, 64) end
    local metadata = { picture = true, source = "galaxies-pictures", sourceName = "Kobo Logic Puzzles Pictures",
        difficulty = "hard", solvability = "advanced_logic" }
    local small = { id = id("a"), game = "galaxies", schemaVersion = 1, title = "Picture fixture",
        width = 3, height = 2, metadata = metadata, solution = "xxx#x#x",
        centers = { { x = 1, y = 2, black = true }, { x = 4, y = 2, black = false } } }
    assert(Puzzle.fromWire(small))
    assert(not Galaxies.validMarks(small, "x#x#x#x", true))
    assert(not Galaxies.validMarks(small, ".......", true))
    assert(Galaxies.validMarks(small, "...#.#.", true))

    local file = assert(io.open((arg[0]:gsub("tests/kobo/smoke%.lua$", "apps/server/Galaxies/galaxies-pictures.json")), "rb"))
    local entries = require("json").decode(file:read("*all"))
    file:close()
    local entry = entries[#entries]
    local large = { id = id("b"), game = "galaxies", schemaVersion = 1, title = "Large picture",
        width = entry.width, height = entry.height, metadata = metadata, solution = entry.solution, centers = {} }
    for _, center in ipairs(entry.centers) do
        large.centers[#large.centers + 1] = { x = center[1], y = center[2], black = center[3] }
    end
    assert(large.width == 80 and Puzzle.fromWire(large))
    local shade = Galaxies:new(large, large.solution):regions()
    assert(next(shade) and not shade[1])

    local root = home .. "/galaxies-ui"
    local store = assert(Store.open(root))
    assert(store:updateSettings({ server = "http://example.test:8080", target = 0, galaxiesTarget = 2 }))
    assert(store:setFilters("galaxies", { source = "galaxies-pictures", width = "3-80" }))
    local result = Sync.run(store, function(method, path, payload)
        if path == "/restore" then return {} end
        assert(method == "POST" and path == "/offline-queue" and payload.game == "galaxies")
        assert(payload.source == "galaxies-pictures" and payload.widthFilter == "3-80")
        return { puzzles = { small, large }, remaining = 0 }
    end, { galaxies = true, advancedFilters = true })
    assert(result.added == 2)

    local plugin = dofile(pluginRoot .. "/main.lua"):new{
        ui = { menu = { registerToMainMenu = function() end } }, store = store,
    }
    plugin.online = function() error("Offline Galaxies attempted to call the server") end
    plugin:showHome()
    tapWidget(plugin.home, homeButton(plugin, "galaxies"))
    plugin:showGame(large.id)
    local function pos(game, x, y)
        local ox, oy, scale = game:graphOrigin(false)
        return Geom:new{ x = game.grid.x + ox + x * scale, y = game.grid.y + oy + y * scale }
    end
    local game = assert(plugin.game)
    UI:forceRePaint()
    game:onTap(nil, { pos = pos(game, 1, 0.5) })
    assert(game.graphClip and game.graphClip.w < game.grid.w)
    UI:forceRePaint()
    local initial = game.board:marks()
    assert(initial:find("#", 1, true))
    device.screen.bb:writePNG(home .. "/galaxies-large-grid.png")
    game:onButton("overview")
    UI:forceRePaint()
    assert(game.overview)
    game:onButton("overview")
    game:onButton("plus")
    game:moveViewport(19, 23)
    game:onClose()
    local reopened = assert(Store.open(root))
    assert(reopened.records[large.id].marks == initial and reopened.records[large.id].zoom == 3)
    assert(reopened.records[large.id].row == 19 and reopened.records[large.id].column == 23)

    plugin:showGame(small.id)
    game = assert(plugin.game)
    UI:forceRePaint()
    game:onButton("check")
    assert(UI:getTopmostVisibleWidget().text:find("No mistakes", 1, true))
    UI:close(UI:getTopmostVisibleWidget())
    local start, finish = pos(game, 1, 0.5), pos(game, 1, 1.5)
    game:onPan(nil, { start_pos = start, pos = finish })
    game:onPanRelease(nil, { pos = finish })
    assert(game.board:marks() == "...#.#." and store.records[small.id].completed)
    UI:close(UI:getTopmostVisibleWidget())
    assert(store:saveCheckpoint(game.board))
    local pixels = game.board:regions()
    assert(pixels[1] and pixels[4] and not pixels[2])
    game:onButton("undo")
    assert(game.board:marks() == ".......")
    game:onButton("redo")
    assert(game.board:isComplete())
    UI:close(UI:getTopmostVisibleWidget())
    game:onButton("restart")
    local dialog = UI:getTopmostVisibleWidget()
    UI:close(dialog)
    dialog.ok_callback()
    assert(not game.board:isComplete())
    game:changed(game.board:restoreCheckpoint(store.records[small.id].checkpoint), true)
    assert(game.board:isComplete())
    UI:close(UI:getTopmostVisibleWidget())
    game:onClose()
    plugin.library:onClose()
    plugin.home:onClose()

    local backups
    assert(store:updateSettings({ galaxiesTarget = 0 }))
    result = Sync.run(store, function(method, path, payload)
        assert(method == "POST" and path == "/progress")
        backups = payload.boards
        return { savedCount = #backups, backedUpAt = "2026-11-27T00:00:00Z" }
    end, { galaxies = true, advancedFilters = true })
    assert(result.backedUp == 2)
    local fresh = assert(Store.open(home .. "/galaxies-restore"))
    assert(Sync.restore(fresh, function(method, path)
        assert(method == "GET" and path == "/restore")
        local records = {}
        for _, progress in ipairs(backups) do
            progress.schemaVersion = 1
            records[#records + 1] = { puzzle = progress.puzzleId == small.id and small or large, progress = progress }
        end
        return records
    end) == 2)
    assert(fresh.records[small.id].completed and fresh.records[large.id].marks == initial)

    assert(store:saveGame(Galaxies:new(large, large.solution), { row = 19, column = 23, zoom = 3 }, true))
    plugin:showGame(large.id)
    game = assert(plugin.game)
    assert(game.board:isComplete())
    game:onButton("overview")
    UI:forceRePaint()
    device.screen.bb:writePNG(home .. "/galaxies-picture.png")
    game:onClose()
    if plugin.library then plugin.library:onClose() end
end

local function testExtraCollections()
    local nonogram = { id = "nonogram-collection-" .. string.rep("a", 64), game = "nonogram",
        schemaVersion = 1, width = 3, height = 2, title = "Small Nonogram", author = "Publisher",
        rowClues = { { 3 }, { 1 } }, columnClues = { { 1 }, { 1 }, { 2 } }, solution = "###..#",
        metadata = { source = "world-at-play", sourceName = "The World at Play", sourceKind = "collection", solvability = 2 } }
    local hashi = { id = "hashi-local-" .. string.rep("b", 64), game = "hashi",
        schemaVersion = 1, width = 5, height = 3, title = "Small Hashi", author = "Sen Ltd",
        islands = { { x = 0, y = 0, clue = 1 }, { x = 4, y = 0, clue = 1 } },
        edges = { { first = 0, second = 1 } }, solution = { 1 },
        metadata = { source = "sen-ltd", sourceName = "Sen Ltd", sourceKind = "generated", solvability = "basic_logic" } }
    assert(Puzzle.fromWire(nonogram) and Puzzle.fromWire(hashi))
    assert(Puzzle.title(hashi) == "Small Hashi")
    assert(Puzzle.sourceLabel("hashi", "sen-ltd", "Sen Ltd", "generated") == "Generated by Sen Ltd")
    assert(Puzzle.sourceLabel("hashi", "janko", "Janko", "collection") == "Janko")
    local store = assert(Store.open(home .. "/extra-collections"))
    assert(store:updateSettings({ server = "http://example.test:8080", target = 1, hashiTarget = 1 }))
    assert(store:setFilters("nonogram", { source = "world-at-play" }))
    assert(store:setFilters("hashi", { source = "sen-ltd", width = 5, height = 3 }))
    local games = { nonogram = true, hashi = true, advancedFilters = true, extraCollections = true }
    local result, err = Sync.run(store, function(method, path, payload)
        if path == "/restore?includeCollections=true" then return {} end
        assert(method == "POST" and path == "/offline-queue" and payload.includeCollections == true)
        if payload.game == "nonogram" then
            assert(payload.source == "world-at-play")
            return { puzzles = { nonogram }, remaining = 0 }
        end
        assert(payload.game == "hashi" and payload.source == "sen-ltd" and payload.width == 5 and payload.height == 3)
        return { puzzles = { hashi }, remaining = 0 }
    end, games)
    assert(not err and result.added == 2)
    assert(store:saveGame(Board:new(nonogram, "###xx#"), { row = 1, column = 1, zoom = 2 }, true))
    assert(store:saveGame(Hashi:new(hashi, "1"), { row = 1, column = 1, zoom = 2 }, true))
    store = assert(Store.open(home .. "/extra-collections"))
    assert(store.records[nonogram.id].completed and store.records[hashi.id].completed)
    local plugin = dofile(pluginRoot .. "/main.lua"):new{
        ui = { menu = { registerToMainMenu = function() end } }, store = store,
    }
    plugin:showHome()
    tapWidget(plugin.home, homeButton(plugin, "hashi"))
    assert(plugin.library.item_table[3].details:find("Generated by Sen Ltd", 1, true))
    plugin:showGame(hashi.id)
    assert(plugin.game and plugin.game.board:isComplete())
    UI:forceRePaint()
    plugin.game:onClose()
    plugin.library:onClose()
    tapWidget(plugin.home, homeButton(plugin, "nonogram"))
    assert(plugin.library.item_table[3].details:find("The World at Play", 1, true))
    plugin:showGame(nonogram.id)
    assert(plugin.game and plugin.game.board:isComplete())
    UI:forceRePaint()
    plugin.game:onClose()
    plugin.library:onClose()
    plugin.home:onClose()
    local fresh = assert(Store.open(home .. "/extra-restore"))
    assert(Sync.restore(fresh, function(method, path)
        assert(method == "GET" and path == "/restore?includeCollections=true")
        return {
            { puzzle = nonogram, progress = { schemaVersion = 1, puzzleId = nonogram.id, marks = "###xx#", completed = true } },
            { puzzle = hashi, progress = { schemaVersion = 1, puzzleId = hashi.id,
                marks = { { edge = 0, count = 1, noBridge = false } }, completed = true } },
        }
    end, true) == 2)
    assert(fresh.records[nonogram.id].completed and fresh.records[hashi.id].completed)
end

testBoard()
testSync()
testHashiRules()
testHashiSync()
testFillAPixRulesAndSync()
testQueueFilters()
testApiTransport()
testPluginDiscovery()
testPluginUpdate()
testClueLayout()
testSuspendResume()
testMoveRedraw()
testGameUI()
testHashiUI()
testFillAPixUI()
testSlitherlinkUI()
testNurikabe()
testGalaxies()
testExtraCollections()
testHomeUI()
print("Kobo logic, sync recovery, and KOReader UI smoke checks passed. Screenshots in " .. home)
