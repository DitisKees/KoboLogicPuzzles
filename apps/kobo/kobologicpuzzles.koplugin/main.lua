local API = require("kobologicpuzzles/api")
local Board = require("kobologicpuzzles/board")
local ButtonDialog = require("ui/widget/buttondialog")
local ConfirmBox = require("ui/widget/confirmbox")
local DataStorage = require("datastorage")
local Game = require("kobologicpuzzles/game")
local HashiGame = require("kobologicpuzzles/hashigame")
local InfoMessage = require("ui/widget/infomessage")
local InputDialog = require("ui/widget/inputdialog")
local NetworkMgr = require("ui/network/manager")
local Puzzle = require("kobologicpuzzles/puzzle")
local PuzzleMenu = require("kobologicpuzzles/puzzlemenu")
local Store = require("kobologicpuzzles/store")
local Sync = require("kobologicpuzzles/sync")
local Trapper = require("ui/trapper")
local UIManager = require("ui/uimanager")
local Update = require("kobologicpuzzles/update")
local UpdateArchive = require("kobologicpuzzles/updatearchive")
local Viewport = require("kobologicpuzzles/viewport")
local WidgetContainer = require("ui/widget/container/widgetcontainer")
local logger = require("logger")

local LogicPuzzles = WidgetContainer:extend{ name = "kobologicpuzzles", is_doc_only = false }

local function notice(text)
    UIManager:show(InfoMessage:new{ text = text })
end

local function backupLabel(timestamp)
    if type(timestamp) == "string" then
        local date, time = timestamp:match("^(%d%d%d%d%-%d%d%-%d%d)T(%d%d:%d%d)")
        if date then return "Last backup  " .. date .. " " .. time .. " UTC" end
    end
    return "No progress backup yet"
end

function LogicPuzzles:init()
    self.ui.menu:registerToMainMenu(self)
end

function LogicPuzzles:addToMainMenu(menu_items)
    menu_items.kobologicpuzzles = {
        text = "Kobo Logic Puzzles",
        sorting_hint = "tools",
        callback = function() self:showLibrary() end,
    }
end

function LogicPuzzles:libraryItems()
    local game = self.store.settings.lastGame
    local items = {
        { text = "Game  ·  " .. Puzzle.gameNames[game], callback = function() self:chooseGame() end },
        { text = "Sync now", callback = function() self:sync() end },
        { text = "Browse server catalog", callback = function() self:browse() end },
        { text = "Settings", callback = function() self:settings() end },
    }
    for _, record in ipairs(self.store:list(game)) do
        local puzzle = record.puzzle
        local marks = record.marks or ""
        local placed, total
        if game == "hashi" then
            placed, total = Puzzle.rules(puzzle.id).bridgeTotals(puzzle, marks)
        else
            local _, marked = marks:gsub("[#x]", "")
            placed, total = marked, puzzle.width * puzzle.height
        end
        local progress = math.min(100, math.floor(100 * placed / total))
        local progressLabel = placed > 0 and progress == 0 and "<1%" or progress .. "%"
        if record.completed then progressLabel = game == "hashi" and "Solved" or progressLabel .. "  Solved" end
        if game == "hashi" and not record.marks then progressLabel = "Not started" end
        items[#items + 1] = {
            text = Puzzle.title(puzzle),
            details = PuzzleMenu.details(puzzle.metadata, game),
            mandatory = string.format("%d×%d  %s", puzzle.width, puzzle.height, progressLabel),
            preview = record.marks and record,
            callback = function() self:showGame(puzzle.id) end,
        }
    end
    if #items == 4 then items[#items + 1] = { text = "Sync to download puzzles for offline play", select_enabled = false } end
    return items
end

function LogicPuzzles:libraryTitle()
    return Puzzle.gameNames[self.store.settings.lastGame] .. " library"
end

function LogicPuzzles:showLibrary()
    if not self.store then
        local store, err = Store.open(DataStorage:getDataDir() .. "/kobologicpuzzles")
        if not store then return notice("Could not open the local puzzle library.\n" .. tostring(err)) end
        self.store = store
    end
    if self.library then return end
    local library = PuzzleMenu:new{
        title = self:libraryTitle(),
        subtitle = backupLabel(self.store.settings.lastBackup),
        item_table = self:libraryItems(),
        is_borderless = true,
        is_popout = false,
        close_callback = function() self.library = nil end,
    }
    self.library = library
    UIManager:show(library)
    if self.store.skipped then
        notice(string.format("%d local puzzle files could not be read.", self.store.skipped))
        self.store.skipped = nil
    end
end

function LogicPuzzles:updateLibrary()
    if self.library then
        self.library:switchItemTable(self:libraryTitle(), self:libraryItems(), nil, nil, backupLabel(self.store.settings.lastBackup))
    end
end

function LogicPuzzles:showGame(id)
    local record = self.store.records[id]
    if not record or self.game then return end
    if self.catalog then
        UIManager:close(self.catalog)
        self.catalog = nil
    end
    if self.library then
        UIManager:close(self.library)
        self.library = nil
    end
    local game = Puzzle.game(id)
    local widget = game == "hashi" and HashiGame or Game
    local rules = Puzzle.rules(id)
    self.game = widget:new{
        board = rules:new(record.puzzle, record.marks),
        record = record,
        store = self.store,
        fetchSolution = game == "nonogram" and function(active) self:fetchSolution(active) end or nil,
        onExit = function()
            self.game = nil
            self:showLibrary()
        end,
    }
    UIManager:show(self.game, "full")
end

local function input(title, value, hint, save, numeric, description)
    local dialog
    dialog = InputDialog:new{
        title = title, input = tostring(value), input_hint = hint, description = description, modal = true,
        input_type = (numeric or type(value) == "number") and "number" or nil,
        buttons = {{
            { text = "Cancel", callback = function() UIManager:close(dialog) end },
            { text = "Save", is_enter_default = true, callback = function()
                local ok, err = save(dialog:getInputText())
                if ok then UIManager:close(dialog) else notice(tostring(err)) end
            end },
        }},
    }
    UIManager:show(dialog)
    dialog:onShowKeyboard()
end

function LogicPuzzles:editServer()
    input("Home server URL", self.store.settings.server, "http://192.168.1.10:8080", function(value)
        local address = API.normalize(value)
        if not address then return nil, "Enter the home server's HTTP address, including http://." end
        if address == self.store.settings.server then return true end
        local ok, err = self.store:updateSettings({ server = address, restoredV2Server = false, lastBackup = false })
        if ok then
            self.games = nil
            self:updateLibrary()
        end
        return ok, err
    end)
end

local function choose(title, choices, apply)
    local dialog
    local buttons = {}
    for index, option in ipairs(choices) do
        if index % 2 == 1 then buttons[#buttons + 1] = {} end
        local row = buttons[#buttons]
        row[#row + 1] = { text = option[1], callback = function()
            UIManager:close(dialog)
            apply(option[2])
        end }
    end
    buttons[#buttons + 1] = {{ text = "Close", callback = function() UIManager:close(dialog) end }}
    dialog = ButtonDialog:new{ title = title, modal = true, buttons = buttons }
    UIManager:show(dialog)
end

function LogicPuzzles:chooseGame()
    local function show(games, checked)
        local choices = {{ "Nonogram", "nonogram" }}
        local hasHashi = self.store:hasHashi() or (games and games.hashi)
        if hasHashi then
            choices[2] = { "Hashi", "hashi" }
        elseif not checked then
            choices[2] = { "Check for Hashi", "check" }
        end
        local title = checked and not hasHashi and "Hashi is unavailable on the home server." or "Choose a game"
        choose(title, choices, function(game)
            if game == "check" then return self:online(function(_, available) show(available, true) end) end
            local ok, err = self.store:updateSettings({ lastGame = game })
            if ok then self:updateLibrary() else notice(err) end
        end)
    end
    show(self.games)
end

local function filterOptions(game, key)
    local values, labels
    if key == "difficulty" and game == "hashi" then
        values, labels = Puzzle.difficulties, Puzzle.difficultyNames
    elseif key == "solvability" and game == "hashi" then
        values, labels = Puzzle.hashiSolvabilities, Puzzle.hashiSolvabilityNames
    elseif key == "solvability" then
        values, labels = {}, Puzzle.nonogramSolvabilityNames
        for index in ipairs(labels) do values[index] = index end
    end
    return values, labels
end

local function filterChecklist(title, values, labels, selected, save)
    local dialog
    local buttons = {}
    for _, value in ipairs(values) do
        buttons[#buttons + 1] = {{ text = labels[value], checked_func = function() return selected[value] end,
            callback = function() selected[value] = not selected[value] end }}
    end
    local function apply(expression)
        local ok, err = save(expression)
        if ok then UIManager:close(dialog) else notice(err) end
    end
    buttons[#buttons + 1] = {
        { text = "Any", callback = function() apply(false) end },
        { text = "Cancel", callback = function() UIManager:close(dialog) end },
        { text = "Save", callback = function()
            local choices = {}
            for _, value in ipairs(values) do
                if selected[value] then choices[#choices + 1] = tostring(value) end
            end
            apply(#choices > 0 and table.concat(choices, ",") or false)
        end },
    }
    dialog = ButtonDialog:new{ title = title, modal = true, buttons = buttons }
    UIManager:show(dialog)
end

function LogicPuzzles:filterSettings(game, onChange)
    local filters = self.store.settings.filters[game]
    local function save(key, expression)
        local patch = {}
        for name, value in pairs(filters) do patch[name] = value end
        patch[key] = expression
        if key == "size" then patch.width, patch.height = nil, nil end
        local ok, err = self.store:setFilters(game, patch)
        if ok and onChange then UIManager:nextTick(onChange) end
        return ok, err
    end

    local keys = game == "hashi" and { "size", "difficulty", "solvability" } or { "width", "difficulty", "height", "solvability" }
    local titles = { size = "Grid size", width = "Width", height = "Height", difficulty = "Difficulty", solvability = "Solvability" }
    local choices = {}
    for _, key in ipairs(keys) do
        local value = filters[key]
        if key == "size" then value = value or filters.width or filters.height end
        local values, labels = filterOptions(game, key)
        local _, selected = Puzzle.filterSelection(game, key, value)
        local summary = value or "Any"
        if selected and values then
            local count, single = 0, nil
            for _, option in ipairs(values) do
                if selected[option] then count, single = count + 1, labels[option] end
            end
            summary = count == 1 and single or count .. " selected"
        end
        choices[#choices + 1] = { titles[key] .. "  ·  " .. summary, key }
    end
    choose(Puzzle.gameNames[game] .. " catalog and queue filters", choices, function(key)
        local values, labels = filterOptions(game, key)
        if values then
            local _, selected = Puzzle.filterSelection(game, key, filters[key])
            filterChecklist(titles[key], values, labels, selected or {}, function(expression) return save(key, expression) end)
            return
        end

        local value = filters[key]
        if key == "size" then value = value or filters.width or filters.height end
        local hint = "Use whole numbers from 1 to 100, e.g. 20-35 or 25,40."
        if key == "size" then hint = "Use 20 to 50 in steps of 5, e.g. 20-30 or 25,40." end
        if key == "difficulty" then hint = "Use 1 to 5 in steps of 0.25, e.g. 3.5-4.25 or 2,4." end
        input(titles[key], tostring(value or ""), "Any", function(text)
            if text:match("^%s*$") then return save(key, false) end
            local expression = Puzzle.filterSelection(game, key, text)
            if not expression then return nil, hint end
            return save(key, expression)
        end, false, hint)
    end)
end

function LogicPuzzles:settings()
    local dialog
    local game = self.store.settings.lastGame
    local targetKey = game == "hashi" and "hashiTarget" or "target"
    local boardSetting
    if game == "hashi" then
        local guides = self.store.settings.hashiGuides
        boardSetting = { text = "Candidate bridges  ·  " .. (guides and "On" or "Off"), callback = function()
            UIManager:close(dialog)
            local ok, err = self.store:updateSettings({ hashiGuides = not guides })
            if not ok then notice(err) else self:settings() end
        end }
    else
        boardSetting = { text = "Clues to show  (" .. self.store.settings.clueLimit .. ")", callback = function()
            UIManager:close(dialog)
            local maximum = Viewport.maxClueLimit
            input("Clues shown per row or column", self.store.settings.clueLimit, "1–" .. maximum,
                function(value)
                    local limit = tonumber(value)
                    if not limit or limit < 1 or limit > maximum or limit ~= math.floor(limit) then
                        return nil, "Enter a whole number from 1 to " .. maximum .. "."
                    end
                    return self.store:updateSettings({ clueLimit = limit })
                end)
        end }
    end
    dialog = ButtonDialog:new{
        title = Puzzle.gameNames[game] .. " settings",
        modal = true,
        buttons = {
            {{ text = "Home server URL", callback = function()
                UIManager:close(dialog)
                self:editServer()
            end }},
            {{ text = "Offline queue size  (" .. self.store:target(game) .. ")", callback = function()
                UIManager:close(dialog)
                input("Unstarted puzzles to keep offline", self.store:target(game), "0–100", function(value)
                    local target = tonumber(value)
                    if not target or target < 0 or target > 100 or target ~= math.floor(target) then
                        return nil, "Enter a whole number from 0 to 100."
                    end
                    return self.store:updateSettings({ [targetKey] = target })
                end)
            end }},
            {{ text = "Catalog and queue filters", callback = function()
                UIManager:close(dialog)
                self:filterSettings(game)
            end }},
            { boardSetting },
            {{ text = "Update plugin", callback = function()
                UIManager:close(dialog)
                self:checkPluginUpdate()
            end }},
            {{ text = "Close", callback = function() UIManager:close(dialog) end }},
        },
    }
    UIManager:show(dialog)
end

function LogicPuzzles:updateRequest(message, task, onResult)
    if self.busy then return end
    NetworkMgr:runWhenConnected(function()
        if self.busy then return end
        self.busy = true
        Trapper:wrap(function()
            local ok, completed, result, err = pcall(Trapper.dismissableRunInSubprocess, Trapper, task, message)
            self.busy = false
            if not ok then
                notice("The plugin update could not finish.")
            elseif completed then
                onResult(result, err)
            end
        end)
    end)
end

function LogicPuzzles:checkPluginUpdate()
    if self.updateInstalled then return UIManager:askForRestart("Restart KOReader to load the new plugin.") end
    if not self.path then return notice("Could not determine the installed plugin directory.") end
    self:updateRequest("Checking for a Kobo Logic Puzzles update…", function()
        return Update.check(self.path, self.store.root, self.version)
    end, function(release, err)
        if release == false then return notice("Kobo Logic Puzzles is up to date (" .. self.version .. ").") end
        if not release then return notice(err or "Could not check for an update.") end
        UIManager:show(ConfirmBox:new{
            text = string.format("Update Kobo Logic Puzzles from %s to %s?", self.version, release.version),
            ok_text = "Install update",
            ok_callback = function() self:installPluginUpdate(release) end,
        })
    end)
end

function LogicPuzzles:installPluginUpdate(release)
    self:updateRequest("Downloading the Kobo Logic Puzzles update…", function()
        return Update.download(self.path, self.store.root, release)
    end, function(zip, err)
        if not zip then return notice(err or "Could not download the update ZIP.") end
        local ok, installError = UpdateArchive.install(self.path, zip, release.version)
        if not ok then return notice("Could not install the update.\n" .. tostring(installError)) end
        self.updateInstalled = true
        UIManager:askForRestart("Kobo Logic Puzzles " .. release.version .. " was installed. Restart KOReader now?")
    end)
end

function LogicPuzzles:online(operation)
    if self.busy then return end
    if self.store.settings.server == "" then return self:editServer() end
    NetworkMgr:runWhenConnected(function()
        if self.busy then return end
        self.busy = true
        local server = self.store.settings.server
        Trapper:wrap(function()
            local ok, err = xpcall(function()
                local function request(method, path, payload, message)
                    return API.request(server, method, path, payload, message or "Syncing with home server…")
                end
                local games, capabilityError = API.capabilities(request)
                if not games then return notice(capabilityError) end
                self.games = games
                operation(request, games)
            end, debug.traceback)
            self.busy = false
            if not ok then
                logger.warn("Kobo Logic Puzzles network operation failed", err)
                notice("The operation could not finish. Try again from the library.")
            end
        end)
    end)
end

function LogicPuzzles:fetchSolution(game)
    self:online(function(request)
        local id = game.board.puzzle.id
        local wire, err = request("GET", "/puzzles/" .. Puzzle.wireId(id) .. "?includeSolution=true",
            nil, "Looking for the puzzle solution…")
        if not wire then return notice(err) end
        local puzzle = Puzzle.fromWire(wire)
        if not puzzle or puzzle.id ~= id or not Board.validSolution(game.board.puzzle, puzzle.solution) then
            return notice("No known solution is available for this puzzle.")
        end
        local ok, saveError = self.store:saveSolution(id, puzzle.solution)
        if not ok then logger.warn("Kobo Logic Puzzles: could not cache the puzzle solution", saveError) end
        game.board.solution = puzzle.solution
        if self.game == game then game:checkSolution() end
    end)
end

function LogicPuzzles:sync()
    self:online(function(request, games)
        local result, err = Sync.run(self.store, request, games)
        if not err then
            local _, metadataError = Sync.refreshMetadata(self.store, request)
            err = metadataError
        end
        self:updateLibrary()
        local summary = string.format("%d backed up · %d restored · %d downloaded",
            result.backedUp, result.restored, result.added)
        for _, game in ipairs(Puzzle.games) do
            local missing = result.shortfalls[game]
            if missing and missing > 0 then
                summary = summary .. string.format("\n%d %s queue slots still open", missing, Puzzle.gameNames[game])
            end
        end
        if result.pending > 0 then summary = summary .. "\nHashi backups are waiting for server support." end
        notice(err and ("Sync incomplete\n" .. summary .. "\n\n" .. tostring(err)) or summary)
    end)
end

function LogicPuzzles:browse(beforeId)
    local game = self.store.settings.lastGame
    self:online(function(request, games)
        if not games[game] then return notice("The home server does not currently offer " .. Puzzle.gameNames[game] .. ".") end
        local filters = self.store.settings.filters[game]
        local query, advanced = Puzzle.catalogQuery(game, filters)
        if advanced and not games.advancedFilters then return notice(Puzzle.advancedFilterMessage) end
        local restored, restoreError = Sync.restore(self.store, request)
        if not restored then return notice(restoreError) end
        if restored > 0 then self:updateLibrary() end
        local matches = Puzzle.filterMatcher(game, filters)
        local page, err = request("GET", "/puzzles?limit=25&" .. query .. (beforeId and "&beforeId=" .. beforeId or ""),
            nil, "Loading the puzzle catalog…")
        if not page then return notice(err) end
        if type(page.puzzles) ~= "table" or #page.puzzles > 25 then return notice("The server returned an invalid catalog page.") end
        local items = {{ text = "Filters", callback = function() self:filterSettings(game, function() self:browse() end) end }}
        for _, puzzle in ipairs(page.puzzles) do
            local id = type(puzzle) == "table" and Puzzle.localId(puzzle.id)
            if not id or Puzzle.game(id) ~= game or puzzle.game ~= game
                or (game == "nonogram" and type(puzzle.title) ~= "string")
                or type(puzzle.width) ~= "number" or type(puzzle.height) ~= "number" then
                return notice("The server returned an invalid catalog entry.")
            end
            local record = self.store.records[id]
            local status = record and (record.completed and "  Solved" or (record.marks and "  In progress" or "  Offline"))
                or (puzzle.status == "solved" and "  Solved" or (puzzle.status == "started" and "  In progress" or ""))
            items[#items + 1] = {
                text = Puzzle.title(puzzle),
                details = PuzzleMenu.details(puzzle.metadata, game),
                mandatory = string.format("%d×%d%s", puzzle.width, puzzle.height, status),
                callback = function() self:downloadAndPlay(id, matches) end,
            }
        end
        local cursor = page.nextBeforeId
        if cursor ~= nil then
            local id = Puzzle.localId(cursor)
            if not id or Puzzle.game(id) ~= game then return notice("The server returned an invalid catalog cursor.") end
            items[#items + 1] = { text = "More puzzles", callback = function() self:browse(cursor) end }
        elseif #page.puzzles == 0 then
            items[#items + 1] = { text = "No matching puzzles available", select_enabled = false }
        end
        if beforeId then items[#items + 1] = { text = "Back to newest puzzles", callback = function() self:browse() end } end
        if self.catalog then
            self.catalog:switchItemTable(nil, items)
        else
            self.catalog = PuzzleMenu:new{
                title = Puzzle.gameNames[game] .. " catalog", item_table = items,
                is_borderless = true, is_popout = false,
                close_callback = function() self.catalog = nil end,
            }
            UIManager:show(self.catalog)
        end
    end)
end

function LogicPuzzles:downloadAndPlay(id, matches)
    if self.store.records[id] then return self:showGame(id) end
    self:online(function(request)
        local path = "/puzzles/" .. Puzzle.wireId(id)
        local wire, err, status = request("GET", path .. "?includeSolution=true", nil, "Downloading puzzle…")
        if not wire and Puzzle.game(id) == "nonogram" and (status == 502 or status == 504) then
            wire, err = request("GET", path, nil, "Downloading puzzle…")
        end
        if not wire then return notice(err) end
        local puzzle = Puzzle.fromWire(wire)
        if not puzzle or puzzle.id ~= id then return notice("The server returned an invalid puzzle definition.") end
        if matches and not matches(puzzle) then return notice("This puzzle no longer matches your filters.") end
        local ok, saveError = self.store:download(puzzle)
        if not ok then return notice(saveError) end
        self:showGame(id)
    end)
end

return LogicPuzzles
