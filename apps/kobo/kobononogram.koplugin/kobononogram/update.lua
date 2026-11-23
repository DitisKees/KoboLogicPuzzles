local JSON = require("json")
local https = require("ssl.https")
local http = require("socket.http")
local lfs = require("libs/libkoreader-lfs")
local socketutil = require("socketutil")

local Update = {}
local api = "https://api.github.com/repos/DitisKees/KoboNonogram"
local assetName = "kobononogram.koplugin.zip"
local maxBytes = 2 * 1024 * 1024

local function versionParts(value)
    if type(value) ~= "string" or #value > 24 then return nil end
    local major, minor, patch = value:match("^(%d+)%.(%d+)%.(%d+)$")
    if not major then return nil end
    return { tonumber(major), tonumber(minor), tonumber(patch) }
end

local function newer(a, b)
    for i = 1, 3 do
        if a[i] ~= b[i] then return a[i] > b[i] end
    end
    return false
end

local function certificateMatches(cert, host)
    local names = cert and cert:extensions()["2.5.29.17"]
    local suffix = host:match("^[^.]+(%..+)$")
    for _, dnsName in ipairs(names and names.dNSName or {}) do
        dnsName = dnsName:lower()
        if dnsName == host or (suffix and dnsName == "*" .. suffix) then return true end
    end
    return false
end

local function request(address, caFile, accept, token)
    local expectedHost = address:match("^https://([a-z.-]+)/")
    local chunks, size = {}, 0
    local headers = { accept = accept }
    if token then headers.authorization = "Bearer " .. token end
    local connectTLS = https.tcp{
        cafile = caFile,
        verify = "peer",
        options = { "all", "no_sslv2", "no_sslv3", "no_tlsv1", "no_tlsv1_1" },
    }
    socketutil:set_timeout(15, 45)
    local ok, result, status, responseHeaders = pcall(http.request, {
        url = address,
        headers = headers,
        redirect = false,
        create = function()
            local connection = connectTLS()
            local connect = connection.connect
            function connection:connect(host, port)
                if host ~= expectedHost or tonumber(port) ~= 443 then return nil, "Unexpected GitHub host" end
                connect(self, host, port)
                if not certificateMatches(self.sock:getpeercertificate(), host) then
                    return nil, "Certificate hostname mismatch"
                end
                return 1
            end
            return connection
        end,
        sink = function(chunk)
            if chunk then
                size = size + #chunk
                if size > maxBytes then return nil, "Update response exceeds the size limit" end
                chunks[#chunks + 1] = chunk
            end
            return 1
        end,
    })
    socketutil:reset_timeout()
    if not ok or not result then return nil, "Could not connect securely to GitHub." end
    return table.concat(chunks), tonumber(status), responseHeaders
end

local function readToken(dataDir)
    local path = dataDir .. "/github-update-token.txt"
    if not lfs.symlinkattributes(path) then return nil end
    local file = io.open(path, "rb")
    if not file then return nil, "Could not read github-update-token.txt." end
    local token = file:read(512) or ""
    file:close()
    token = token:match("^%s*(.-)%s*$")
    if not token:match("^github_pat_[A-Za-z0-9_]+$") then
        return nil, "Use a fine-grained GitHub token in github-update-token.txt."
    end
    return token
end

function Update.check(pluginPath, dataDir, current)
    local installed = versionParts(current)
    if not installed then return nil, "Could not determine the installed plugin version." end
    local token, err = readToken(dataDir)
    if err then return nil, err end
    local body, status = request(api .. "/releases?per_page=30", pluginPath .. "/ca-bundle.pem",
        "application/vnd.github+json", token)
    if not body then return nil, status end
    if status == 401 or status == 404 then
        return nil, "GitHub denied access. For a private repository, check github-update-token.txt."
    end
    if status ~= 200 then return nil, "GitHub returned HTTP " .. tostring(status) .. "." end
    local ok, releases = pcall(JSON.decode, body)
    if not ok or type(releases) ~= "table" or not JSON.util.IsArray(releases) then
        return nil, "GitHub returned an invalid release list."
    end

    local latest, latestParts
    for _, release in ipairs(releases) do
        if type(release) == "table" and not release.draft and not release.prerelease
            and type(release.tag_name) == "string" and type(release.assets) == "table" then
            local version = release.tag_name:match("^kobo%-v(.+)$")
            local parts = versionParts(version)
            if parts and (not latestParts or newer(parts, latestParts)) then
                for _, asset in ipairs(release.assets) do
                    if type(asset) == "table" and asset.name == assetName and asset.state == "uploaded" then
                        latest, latestParts = { version = version, asset = asset }, parts
                        break
                    end
                end
            end
        end
    end
    if not latest then return nil, "No Kobo plugin release is available yet." end
    if not newer(latestParts, installed) then return false end
    local asset = latest.asset
    local digest = type(asset.digest) == "string" and asset.digest:match("^sha256:(%x+)$")
    if not digest or #digest ~= 64 or type(asset.id) ~= "number" or asset.id < 1 or asset.id % 1 ~= 0
        or type(asset.size) ~= "number" or asset.size < 1 or asset.size > maxBytes then
        return nil, "GitHub returned invalid information about the update ZIP."
    end
    return { version = latest.version, id = asset.id, digest = digest:lower(), size = asset.size }
end

function Update.download(pluginPath, dataDir, release)
    local token, err = readToken(dataDir)
    if err then return nil, err end
    local caFile = pluginPath .. "/ca-bundle.pem"
    local address = string.format("%s/releases/assets/%.0f", api, release.id)
    local body, status, headers = request(address, caFile, "application/octet-stream", token)
    if status == 302 then
        local location = headers and headers.location
        local host = type(location) == "string" and location:match("^https://([a-z.-]+)/[^%s]+$")
        if host ~= "release-assets.githubusercontent.com" and host ~= "objects.githubusercontent.com"
            and host ~= "github-releases.githubusercontent.com" then
            return nil, "GitHub returned an unexpected download address."
        end
        -- The signed download URL already grants access to the asset.
        body, status = request(location, caFile, "application/octet-stream")
    end
    if not body then return nil, status end
    if status ~= 200 then return nil, "GitHub returned HTTP " .. tostring(status) .. " for the ZIP." end
    if #body ~= release.size or require("ffi/sha2").sha256(body) ~= release.digest then
        return nil, "The downloaded ZIP did not match GitHub's checksum."
    end
    return body
end

return Update
