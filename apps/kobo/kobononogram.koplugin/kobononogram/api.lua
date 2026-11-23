local JSON = require("json")
local Trapper = require("ui/trapper")
local http = require("socket.http")
local ltn12 = require("ltn12")
local socketutil = require("socketutil")
local url = require("socket.url")

local API = {}

function API.normalize(address)
    address = address:match("^%s*(.-)%s*$"):gsub("/+$", "")
    address = address:gsub("/api/v1$", "")
    local parsed = url.parse(address)
    if parsed and parsed.scheme == "http" and parsed.host and parsed.host ~= ""
        and not address:find("[@%?#%s]") then return address end
end

local function request(server, method, path, payload)
    local chunks = {}
    local headers = { accept = "application/json" }
    local body
    if payload then
        if payload.cachedUnstartedPuzzleIds then
            JSON.util.InitArray(payload.cachedUnstartedPuzzleIds)
        end
        body = JSON.encode(payload)
        headers["content-type"] = "application/json"
        headers["content-length"] = #body
    end
    socketutil:set_timeout(45, 90)
    local ok, _, status = pcall(http.request, {
        url = server .. "/api/v1" .. path, method = method, headers = headers,
        source = body and ltn12.source.string(body), sink = socketutil.table_sink(chunks), redirect = false,
    })
    socketutil:reset_timeout()
    if not ok then return nil, "The network request failed." end
    local code = tonumber(status)
    if not code then return nil, "Could not reach the server. " .. tostring(status or "") end
    local decoded, reply = pcall(JSON.decode, table.concat(chunks))
    if code < 200 or code >= 300 then
        local detail = decoded and type(reply) == "table" and (reply.detail or reply.title)
        return nil, "Server returned HTTP " .. status .. (type(detail) == "string" and (". " .. detail) or ""), code
    end
    if not decoded or type(reply) ~= "table" then return nil, "The server did not return a valid JSON response." end
    if (path == "/restore" or path:match("^/puzzles%?")) and not JSON.util.IsArray(reply) then
        return nil, "The server did not return an array."
    end
    return reply
end

function API.request(server, method, path, payload, message)
    local completed, reply, err, status = Trapper:dismissableRunInSubprocess(function()
        return request(server, method, path, payload)
    end, message)
    if not completed then return nil, "Request cancelled." end
    return reply, err or (not reply and "The network task could not finish." or nil), status
end

return API
