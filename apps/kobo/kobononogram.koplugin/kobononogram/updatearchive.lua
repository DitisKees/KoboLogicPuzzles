local Archive = require("ffi/archiver")
local ffi = require("ffi")
local ffiutil = require("ffi/util")
local lfs = require("libs/libkoreader-lfs")
local util = require("util")

local UpdateArchive = {}
local root = "kobononogram.koplugin"

local function writeFile(path, contents)
    local file, err = io.open(path, "wb")
    if not file then return nil, err end
    local ok, writeError = file:write(contents)
    if ok then ok, writeError = file:flush() end
    if ok then ok, writeError = ffiutil.fsyncOpenedFile(file, true) end
    local closed, closeError = file:close()
    if not ok or not closed then return nil, writeError or closeError end
    return true
end

local function relativePath(path)
    local relative = path:match("^kobononogram%.koplugin/(.+)$")
    if not relative or #relative > 200 or relative:find("//", 1, true) then return nil end
    for part in relative:gmatch("[^/]+") do
        if not part:match("^[%w_][%w_.-]*$") or part:sub(-1) == "." then return nil end
    end
    return relative
end

local function unpackPlugin(reader, target, version)
    local seen, dirs, bytes, entries = {}, { [target] = true }, 0, 0
    for entry in reader:iterate() do
        entries = entries + 1
        local relative = relativePath(entry.path)
        if entries > 256 or (not relative and
            (entry.mode ~= "directory" or (entry.path ~= root and entry.path ~= root .. "/"))) then
            return nil, "The ZIP contains an unexpected path."
        end
        if entry.mode ~= "directory" then
            local key = relative:lower()
            if entry.mode ~= "file" or relative:sub(-1) == "/" or seen[key] or entry.size < 0 then
                return nil, "The ZIP contains an unsupported or duplicate file."
            end
            bytes = bytes + tonumber(entry.size)
            if bytes > 4 * 1024 * 1024 then return nil, "The extracted plugin is too large." end
            local contents = reader:extractToMemory(entry.index)
            if not contents then return nil, "Could not read a file in the update ZIP." end
            if relative:sub(-4) == ".lua" and (contents:sub(1, 1) == "\27" or not loadstring(contents)) then
                return nil, "The update contains invalid Lua source."
            end
            if relative == "_meta.lua" and contents:match('\n%s*version%s*=%s*"([%d.]+)"%s*,') ~= version then
                return nil, "The installed version would not match the release tag."
            end
            local path = target .. "/" .. relative
            local directory = ffiutil.dirname(path)
            local ok, err = util.makePath(directory)
            if not ok then return nil, err end
            ok, err = writeFile(path, contents)
            if not ok then return nil, err end
            dirs[directory], seen[key] = true, relative
        end
    end
    if reader.err or seen["main.lua"] ~= "main.lua" or seen["_meta.lua"] ~= "_meta.lua"
        or seen["ca-bundle.pem"] ~= "ca-bundle.pem" then
        return nil, "The ZIP does not contain a complete Kobo Nonogram plugin."
    end
    for directory in pairs(dirs) do
        local ok, err = ffiutil.fsyncDirectory(directory)
        if not ok then return nil, err end
    end
    return true
end

local function removeTree(path)
    if lfs.symlinkattributes(path, "mode") == "directory" then
        for entry in lfs.dir(path) do
            if entry ~= "." and entry ~= ".." then removeTree(path .. "/" .. entry) end
        end
    end
    os.remove(path)
end

local function replace(path, target, stage)
    local backup, older = path .. ".previous", stage .. "/older"
    local mode = lfs.symlinkattributes(backup, "mode")
    if mode and mode ~= "directory" then return nil, "The previous plugin backup path is in use." end
    if mode then
        local ok, err = os.rename(backup, older)
        if not ok then return nil, err end
    end
    local ok, err = os.rename(path, backup)
    if ok then
        ffiutil.fsyncDirectory(ffiutil.dirname(path))
        ok, err = os.rename(target, path)
        if not ok and not os.rename(backup, path) then
            return nil, "Could not restore the old plugin. It is in kobononogram.koplugin.previous."
        end
    end
    if not ok and mode then os.rename(older, backup) end
    ffiutil.fsyncDirectory(ffiutil.dirname(path))
    return ok, err
end

function UpdateArchive.install(pluginPath, zip, version)
    local path = ffiutil.realpath(pluginPath)
    if not path or ffiutil.basename(path) ~= root or lfs.attributes(path, "mode") ~= "directory" then
        return nil, "Could not find the installed plugin directory."
    end
    if zip:sub(1, 4) ~= "PK\3\4" then return nil, "The download is not a ZIP file." end
    local template = path .. ".update-XXXXXX"
    local buffer = ffi.new("char[?]", #template + 1, template)
    if ffi.C.mkdtemp(buffer) == nil then
        return nil, "Could not prepare the update beside the plugin. Check that the folder is writable."
    end
    local stage = ffi.string(buffer)
    local archivePath, target = stage .. "/plugin.zip", stage .. "/new"
    local reader = Archive.Reader:new()
    local ran, ok, err = pcall(function()
        local written, writeError = writeFile(archivePath, zip)
        if not written then return nil, writeError end
        if not reader:open(archivePath) then return nil, "Could not open the update ZIP." end
        local unpacked, unpackError = unpackPlugin(reader, target, version)
        reader:close()
        if not unpacked then return nil, unpackError end
        return replace(path, target, stage)
    end)
    reader:close()
    -- Keep a rotated backup if the filesystem prevented restoring it.
    if not ok and lfs.symlinkattributes(stage .. "/older") then
        os.remove(archivePath)
        pcall(removeTree, target)
    else
        pcall(removeTree, stage)
    end
    if not ran then return nil, "Could not install the update." end
    return ok, err
end

return UpdateArchive
