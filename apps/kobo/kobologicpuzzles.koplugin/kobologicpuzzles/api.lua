local JSON = require("json")
local Trapper = require("ui/trapper")
local http = require("socket.http")
local ltn12 = require("ltn12")
local socketutil = require("socketutil")
local url = require("socket.url")

local API = {}
-- Decode optional null fields as nil so KOReader can serialize the response from its subprocess.
local decodeResponse = JSON.decode.getDecoder({ initialObject = true, others = { allowUndefined = false, null = false } })

function API.normalize(address)
    address = address:match("^%s*(.-)%s*$"):gsub("/+$", "")
    address = address:gsub("/api/v[12]$", "")
    local parsed = url.parse(address)
    if parsed and parsed.scheme == "http" and parsed.host and parsed.host ~= ""
        and not address:find("[@%?#%s]") then return address end
end

local function request(server, method, path, payload)
    local chunks = {}
    local headers = { accept = "application/json" }
    local body
    if payload then
        for _, key in ipairs({ "cachedUnstartedPuzzleIds", "excludedPuzzleIds", "boards" }) do
            if payload[key] then JSON.util.InitArray(payload[key]) end
        end
        for _, board in ipairs(payload.boards or {}) do
            if type(board.marks) == "table" then JSON.util.InitArray(board.marks) end
        end
        body = JSON.encode(payload)
        headers["content-type"] = "application/json"
        headers["content-length"] = #body
    end
    socketutil:set_timeout(45, 90)
    local ok, _, status = pcall(http.request, {
        url = server .. "/api/v2" .. path, method = method, headers = headers,
        source = body and ltn12.source.string(body), sink = socketutil.table_sink(chunks), redirect = false,
    })
    socketutil:reset_timeout()
    if not ok then return nil, "The network request failed." end
    local code = tonumber(status)
    if not code then return nil, "Could not reach the server. " .. tostring(status or "") end
    local decoded, reply = pcall(decodeResponse, table.concat(chunks))
    if code < 200 or code >= 300 then
        local detail = decoded and type(reply) == "table" and (reply.detail or reply.title)
        return nil, "Server returned HTTP " .. status .. (type(detail) == "string" and (". " .. detail) or ""), code
    end
    if not decoded or type(reply) ~= "table" then return nil, "The server did not return a valid JSON response." end
    if path == "/restore" and not JSON.util.IsArray(reply) then
        return nil, "The server did not return an array."
    end
    if (path == "/offline-queue" or path:match("^/puzzles%?")) and
        (type(reply.puzzles) ~= "table" or not JSON.util.IsArray(reply.puzzles)) then
        return nil, "The server did not return a puzzle list."
    end
    return reply
end

function API.capabilities(request)
    local reply, err, status = request("GET", "/capabilities", nil, "Checking puzzle server…")
    if not reply and status ~= 404 then return nil, err end
    local unsupportedServer = "The home server must support puzzle API v2. Check its URL and configuration."
    if not reply or reply.apiVersion ~= 2 or reply.schemaVersion ~= 1 or type(reply.games) ~= "table" then
        return nil, unsupportedServer
    end
    local games = { advancedFilters = reply.advancedFilters == true }
    for _, game in ipairs(reply.games) do
        if game == "nonogram" or game == "hashi" then games[game] = true end
    end
    if not games.nonogram then return nil, unsupportedServer end
    return games
end

function API.request(server, method, path, payload, message)
    local completed, reply, err, status = Trapper:dismissableRunInSubprocess(function()
        return request(server, method, path, payload)
    end, message)
    if not completed then return nil, "Request cancelled." end
    return reply, err or (not reply and "The network task could not finish." or nil), status
end

return API
