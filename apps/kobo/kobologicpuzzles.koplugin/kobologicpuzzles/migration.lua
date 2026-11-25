local Puzzle = require("kobologicpuzzles/puzzle")
local ffi = require("ffi")
local ffiutil = require("ffi/util")
local lfs = require("libs/libkoreader-lfs")

local Migration = {}

local function isLibraryFile(name)
    return name == "settings.json" or name == "github-update-token.txt" or Puzzle.fileId(name) ~= nil
end

local function copyFile(source, target)
    local input, err = io.open(source, "rb")
    if not input then return nil, err end
    local bytes, readError = input:read("*all")
    local closed, closeError = input:close()
    if not bytes or not closed then return nil, readError or closeError end

    local output
    output, err = io.open(target, "wb")
    if not output then return nil, err end
    local ok
    ok, err = output:write(bytes)
    if ok then ok, err = output:flush() end
    if ok then ok, err = ffiutil.fsyncOpenedFile(output, true) end
    closed, closeError = output:close()
    if not ok or not closed then return nil, err or closeError end
    return true
end

local function copyLibrary(legacy, stage, copied)
    for name in lfs.dir(legacy) do
        if isLibraryFile(name) then
            local source, target = legacy .. "/" .. name, stage .. "/" .. name
            if lfs.symlinkattributes(source, "mode") ~= "file" then return nil, "Expected a regular file for " .. name end
            copied[#copied + 1] = target
            local ok, err = copyFile(source, target)
            if not ok then return nil, "Could not copy " .. name .. ". " .. tostring(err) end
        end
    end
    return ffiutil.fsyncDirectory(stage)
end

function Migration.prepare(dataDir)
    local legacy, root = dataDir .. "/kobononogram", dataDir .. "/kobologicpuzzles"
    local currentMode, currentError, currentCode = lfs.symlinkattributes(root, "mode")
    if currentMode == "directory" then return root end
    if currentMode then return nil, "The Kobo Logic Puzzles data path is already in use." end
    if currentCode ~= 2 then return nil, currentError end

    local oldMode, oldError, oldCode = lfs.symlinkattributes(legacy, "mode")
    if not oldMode and oldCode == 2 then return root end
    if oldMode ~= "directory" then return nil, oldError or "The Kobo Nonogram data path is not a directory." end

    -- The new directory only becomes visible after every saved file has been written.
    local template = root .. ".migrate-XXXXXX"
    local buffer = ffi.new("char[?]", #template + 1, template)
    if ffi.C.mkdtemp(buffer) == nil then return nil, "Could not prepare a directory beside the old data." end
    local stage, copied = ffi.string(buffer), {}
    local ran, ok, err = pcall(copyLibrary, legacy, stage, copied)
    if ran and ok and not lfs.symlinkattributes(root) then
        ok, err = os.rename(stage, root)
        if ok then
            local synced, syncError = ffiutil.fsyncDirectory(dataDir)
            if not synced then return nil, syncError end
            return root
        end
    end
    for _, path in ipairs(copied) do os.remove(path) end
    lfs.rmdir(stage)
    if lfs.symlinkattributes(root, "mode") == "directory" then return root end
    if not ran then return nil, ok end
    return nil, err or "The Kobo Logic Puzzles data path is already in use."
end

return Migration
