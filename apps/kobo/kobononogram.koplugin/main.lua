local API = require("kobononogram/api")
local Board = require("kobononogram/board")
local ButtonDialog = require("ui/widget/buttondialog")
local ConfirmBox = require("ui/widget/confirmbox")
local DataStorage = require("datastorage")
local Game = require("kobononogram/game")
local InfoMessage = require("ui/widget/infomessage")
local InputDialog = require("ui/widget/inputdialog")
local Menu = require("ui/widget/menu")
local NetworkMgr = require("ui/network/manager")
local Store = require("kobononogram/store")
local Sync = require("kobononogram/sync")
local Trapper = require("ui/trapper")
local UIManager = require("ui/uimanager")
local Update = require("kobononogram/update")
local UpdateArchive = require("kobononogram/updatearchive")
local WidgetContainer = require("ui/widget/container/widgetcontainer")
local logger = require("logger")

local Nonogram = WidgetContainer:extend{ name = "kobononogram", is_doc_only = false }
local PuzzleMenu = Menu:extend{ modal = true }

function PuzzleMenu:onMenuSelect(item)
    if item.select_enabled ~= false and item.callback then item.callback() end
    return true
end

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

function Nonogram:init()
    self.ui.menu:registerToMainMenu(self)
end

function Nonogram:addToMainMenu(menu_items)
    menu_items.kobononogram = {
        text = "Kobo Nonogram",
        sorting_hint = "tools",
        callback = function() self:showLibrary() end,
    }
end

function Nonogram:libraryItems()
    local items = {
        { text = "Sync now", callback = function() self:sync() end },
        { text = "Browse server catalog", callback = function() self:browse() end },
        { text = "Settings", callback = function() self:settings() end },
    }
    for _, record in ipairs(self.store:list()) do
        local puzzle = record.puzzle
        items[#items + 1] = {
            text = puzzle.title,
            mandatory = string.format("%d×%d  %s", puzzle.width, puzzle.height,
                record.completed and "Solved" or (record.marks and "In progress" or "New")),
            callback = function() self:showGame(puzzle.id) end,
        }
    end
    if #items == 3 then items[#items + 1] = { text = "Sync to download puzzles for offline play", select_enabled = false } end
    return items
end

function Nonogram:showLibrary()
    if not self.store then
        local store, err = Store.open(DataStorage:getDataDir() .. "/kobononogram")
        if not store then return notice("Could not open the local puzzle library.\n" .. tostring(err)) end
        self.store = store
    end
    if self.library then return end
    local library = PuzzleMenu:new{
        title = "Kobo Nonogram",
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

function Nonogram:updateLibrary()
    if self.library then
        self.library:switchItemTable(nil, self:libraryItems(), nil, nil, backupLabel(self.store.settings.lastBackup))
    end
end

function Nonogram:showGame(id)
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
    self.game = Game:new{
        board = Board:new(record.puzzle, record.marks),
        record = record,
        store = self.store,
        fetchSolution = function(game) self:fetchSolution(game) end,
        onExit = function()
            self.game = nil
            self:showLibrary()
        end,
    }
    UIManager:show(self.game, "full")
end

local function input(title, value, hint, save)
    local dialog
    dialog = InputDialog:new{
        title = title, input = value, input_hint = hint, modal = true,
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

function Nonogram:editServer()
    input("Home server URL", self.store.settings.server, "http://192.168.1.10:8080", function(value)
        local address = API.normalize(value)
        if not address then return nil, "Enter the home server's HTTP address, including http://." end
        if address == self.store.settings.server then return true end
        local ok, err = self.store:updateSettings({ server = address, restoredServer = false, lastBackup = false })
        if ok then self:updateLibrary() end
        return ok, err
    end)
end

function Nonogram:settings()
    local dialog
    dialog = ButtonDialog:new{
        title = "Kobo Nonogram settings",
        modal = true,
        buttons = {
            {{ text = "Home server URL", callback = function()
                UIManager:close(dialog)
                self:editServer()
            end }},
            {{ text = "Offline queue size  (" .. self.store.settings.target .. ")", callback = function()
                UIManager:close(dialog)
                input("Unstarted puzzles to keep offline", tostring(self.store.settings.target), "0–100", function(value)
                    local target = tonumber(value)
                    if not target or target < 0 or target > 100 or target ~= math.floor(target) then
                        return nil, "Enter a whole number from 0 to 100."
                    end
                    return self.store:updateSettings({ target = target })
                end)
            end }},
            {{ text = "Update plugin", callback = function()
                UIManager:close(dialog)
                self:checkPluginUpdate()
            end }},
            {{ text = "Close", callback = function() UIManager:close(dialog) end }},
        },
    }
    UIManager:show(dialog)
end

function Nonogram:updateRequest(message, task, onResult)
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

function Nonogram:checkPluginUpdate()
    if self.updateInstalled then return UIManager:askForRestart("Restart KOReader to load the new plugin.") end
    if not self.path then return notice("Could not determine the installed plugin directory.") end
    self:updateRequest("Checking for a Kobo Nonogram update…", function()
        return Update.check(self.path, self.store.root, self.version)
    end, function(release, err)
        if release == false then return notice("Kobo Nonogram is up to date (" .. self.version .. ").") end
        if not release then return notice(err or "Could not check for an update.") end
        UIManager:show(ConfirmBox:new{
            text = string.format("Update Kobo Nonogram from %s to %s?", self.version, release.version),
            ok_text = "Install update",
            ok_callback = function() self:installPluginUpdate(release) end,
        })
    end)
end

function Nonogram:installPluginUpdate(release)
    self:updateRequest("Downloading the Kobo Nonogram update…", function()
        return Update.download(self.path, self.store.root, release)
    end, function(zip, err)
        if not zip then return notice(err or "Could not download the update ZIP.") end
        local ok, installError = UpdateArchive.install(self.path, zip, release.version)
        if not ok then return notice("Could not install the update.\n" .. tostring(installError)) end
        self.updateInstalled = true
        UIManager:askForRestart("Kobo Nonogram " .. release.version .. " was installed. Restart KOReader now?")
    end)
end

function Nonogram:online(operation)
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
                operation(request)
            end, debug.traceback)
            self.busy = false
            if not ok then
                logger.warn("Kobo Nonogram network operation failed", err)
                notice("The operation could not finish. Try again from the library.")
            end
        end)
    end)
end

function Nonogram:fetchSolution(game)
    self:online(function(request)
        local id = game.board.puzzle.id
        local puzzle, err = request("GET", string.format("/puzzles/%d?includeSolution=true", id),
            nil, "Looking for the puzzle solution…")
        if not puzzle then return notice(err) end
        if puzzle.id ~= id or not Board.validSolution(game.board.puzzle, puzzle.solution) then
            return notice("No known solution is available for this puzzle.")
        end
        local ok, saveError = self.store:saveSolution(id, puzzle.solution)
        if not ok then logger.warn("Kobo Nonogram: could not cache the puzzle solution", saveError) end
        game.board.solution = puzzle.solution
        if self.game == game then game:checkSolution() end
    end)
end

function Nonogram:sync()
    self:online(function(request)
        local result, err = Sync.run(self.store, request)
        self:updateLibrary()
        local summary = string.format("%d backed up · %d restored · %d downloaded",
            result.backedUp, result.restored, result.added)
        notice(err and ("Sync incomplete\n" .. summary .. "\n\n" .. tostring(err)) or summary)
    end)
end

function Nonogram:browse(beforeId)
    self:online(function(request)
        local restored, restoreError = Sync.restore(self.store, request)
        if not restored then return notice(restoreError) end
        if restored > 0 then self:updateLibrary() end
        local page, err = request("GET", "/puzzles?limit=25" .. (beforeId and "&beforeId=" .. beforeId or ""),
            nil, "Loading the puzzle catalog…")
        if not page then return notice(err) end
        local items = {}
        for _, puzzle in ipairs(page) do
            if type(puzzle) ~= "table" or type(puzzle.id) ~= "number" or type(puzzle.title) ~= "string"
                or type(puzzle.width) ~= "number" or type(puzzle.height) ~= "number" then
                return notice("The server returned an invalid catalog entry.")
            end
            items[#items + 1] = {
                text = puzzle.title,
                mandatory = string.format("%d×%d%s", puzzle.width, puzzle.height,
                    self.store.records[puzzle.id] and "  Offline" or ""),
                callback = function() self:downloadAndPlay(puzzle.id) end,
            }
        end
        if #page == 25 then
            items[#items + 1] = { text = "More puzzles", callback = function() self:browse(page[#page].id) end }
        elseif #page == 0 then
            items[#items + 1] = { text = "No puzzles on this page", select_enabled = false }
        end
        if beforeId then items[#items + 1] = { text = "Back to newest puzzles", callback = function() self:browse() end } end
        if self.catalog then
            self.catalog:switchItemTable(nil, items)
        else
            self.catalog = PuzzleMenu:new{
                title = "Server catalog", item_table = items,
                is_borderless = true, is_popout = false,
                close_callback = function() self.catalog = nil end,
            }
            UIManager:show(self.catalog)
        end
    end)
end

function Nonogram:downloadAndPlay(id)
    if self.store.records[id] then return self:showGame(id) end
    self:online(function(request)
        local puzzle, err = request("GET", string.format("/puzzles/%d", id), nil, "Downloading puzzle…")
        if not puzzle then return notice(err) end
        if puzzle.id ~= id then return notice("The server returned a different puzzle.") end
        local ok, saveError = self.store:download(puzzle)
        if not ok then return notice(saveError) end
        self:showGame(id)
    end)
end

return Nonogram
