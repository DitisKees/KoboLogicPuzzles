local Board = require("kobologicpuzzles/board")
local FillAPix = require("kobologicpuzzles/fillapixboard")
local Hashi = require("kobologicpuzzles/hashiboard")
local Nurikabe = require("kobologicpuzzles/nurikabeboard")
local Slitherlink = require("kobologicpuzzles/slitherlinkboard")

local Puzzle = {}
Puzzle.games = { "nonogram", "hashi", "fill-a-pix", "slitherlink", "nurikabe" }
Puzzle.gameNames = { nonogram = "Nonogram", hashi = "Hashi", ["fill-a-pix"] = "Fill-a-Pix", slitherlink = "Slitherlink", nurikabe = "Nurikabe" }
Puzzle.families = {
    { title = "Cell shading", games = { "nonogram", "fill-a-pix", "nurikabe" } },
    { title = "Line drawing", games = { "hashi", "slitherlink" } },
    { title = "Region partitioning", games = {} },
    { title = "Object placement", games = {} },
}
Puzzle.fillAPixSources = { janko = "Janko", grandgames = "GrandGames", fillapixnet = "Fill-a-Pix.net" }
local rules = { nonogram = Board, hashi = Hashi, ["fill-a-pix"] = FillAPix, slitherlink = Slitherlink, nurikabe = Nurikabe }
Puzzle.difficulties = { "easy", "medium", "hard", "expert" }
Puzzle.difficultyNames = { easy = "Easy", medium = "Medium", hard = "Hard", expert = "Expert" }
Puzzle.hashiSolvabilities = { "basic_logic", "advanced_logic", "bounded_lookahead", "requires_search", "unknown" }
Puzzle.hashiSolvabilityNames = { basic_logic = "Basic logic", advanced_logic = "Advanced logic",
    bounded_lookahead = "Bounded lookahead", requires_search = "Requires search", unknown = "Solvability unknown" }
Puzzle.nonogramSolvabilityNames = { "Trivial", "Line logic", "Moderate lookahead", "Deep lookahead", "Some guessing", "Much guessing" }
Puzzle.advancedFilterMessage = "Update the home server to use ranges and multiple filter values."

function Puzzle.localId(value)
    if type(value) ~= "string" then return end
    local number = value:match("^nonogram%-webpbn%-([1-9]%d*)$")
    if number and #number <= 10 and tonumber(number) <= 2147483647 then return tonumber(number) end
    local hash = value:match("^nonogram%-collection%-([0-9a-f]+)$")
    if hash and #hash == 64 then return value end
    hash = value:match("^hashi%-local%-([0-9a-f]+)$")
    if hash and #hash == 64 then return value end
    hash = value:match("^slitherlink%-([0-9a-f]+)$")
    if hash and #hash == 64 then return value end
    hash = value:match("^nurikabe%-([0-9a-f]+)$")
    if hash and #hash == 64 then return value end
    local source, upstreamId = value:match("^fill%-a%-pix%-([a-z0-9]+)%-([1-9]%d*)$")
    if Puzzle.fillAPixSources[source] and #upstreamId <= 10 and tonumber(upstreamId) <= 2147483647 then return value end
end

function Puzzle.isId(id)
    if type(id) == "number" then return id >= 1 and id <= 2147483647 and id == math.floor(id) end
    return type(id) == "string" and Puzzle.localId(id) == id
end

function Puzzle.fileId(name)
    local key = name:match("^([a-z0-9-]+)%.json$")
    local id = key and (tonumber(key:match("^%d+$")) or Puzzle.localId(key))
    if Puzzle.isId(id) and tostring(id) == key then return id end
end

function Puzzle.wireId(id)
    if type(id) == "number" then return "nonogram-webpbn-" .. id end
    return id
end

function Puzzle.game(id)
    if type(id) == "number" or type(id) == "string" and id:sub(1, 20) == "nonogram-collection-" then return "nonogram" end
    if type(id) == "string" and id:sub(1, 11) == "fill-a-pix-" then return "fill-a-pix" end
    if type(id) == "string" and id:sub(1, 12) == "slitherlink-" then return "slitherlink" end
    if type(id) == "string" and id:sub(1, 9) == "nurikabe-" then return "nurikabe" end
    return "hashi"
end

function Puzzle.rules(id) return rules[Puzzle.game(id)] end

function Puzzle.valid(puzzle)
    return type(puzzle) == "table" and Puzzle.isId(puzzle.id)
        and (puzzle.game == nil or puzzle.game == Puzzle.game(puzzle.id)) and Puzzle.rules(puzzle.id).validPuzzle(puzzle)
end

function Puzzle.fromWire(puzzle)
    if type(puzzle) ~= "table" or puzzle.schemaVersion ~= 1 then return end
    local id = Puzzle.localId(puzzle.id)
    if not id or Puzzle.game(id) ~= puzzle.game then return end
    local decoded = {}
    for key, value in pairs(puzzle) do decoded[key] = value end
    decoded.id = id
    if not Puzzle.valid(decoded) then return end
    if puzzle.game ~= "hashi" and puzzle.solution ~= nil and not Puzzle.rules(id).validSolution(decoded, puzzle.solution) then return end
    return decoded
end

function Puzzle.decodeProgress(puzzle, progress)
    if type(progress) ~= "table" or progress.schemaVersion ~= 1 or progress.puzzleId ~= Puzzle.wireId(puzzle.id)
        or type(progress.completed) ~= "boolean" then return end
    local marks
    if Puzzle.game(puzzle.id) == "hashi" then
        marks = Hashi.decodeMarks(puzzle, progress.marks, progress.completed)
        if not marks then return end
    else
        if not Puzzle.rules(puzzle.id).validMarks(puzzle, progress.marks, progress.completed) then return end
        marks = progress.marks
    end
    return { marks = marks, completed = progress.completed, backedUpAt = progress.backedUpAt }
end

function Puzzle.encodeProgress(progress)
    local id = progress.puzzleId
    return { puzzleId = Puzzle.wireId(id), completed = progress.completed,
        marks = Puzzle.game(id) == "hashi" and Hashi.encodeMarks(progress.marks) or progress.marks }
end

function Puzzle.sourceLabel(game, source, name, kind)
    if type(name) ~= "string" or name == "" then name = nil end
    local legacy = game == "hashi" and (source == nil or source == "bridges") and "Bridges" or game == "slitherlink" and source == "loopy" and "Loopy"
    if kind == "generated" or legacy then
        local generator = name or legacy
        return generator and "Generated by " .. generator or "Generated"
    end
    return name
end

function Puzzle.title(puzzle)
    if type(puzzle.title) == "string" or Puzzle.game(puzzle.id) ~= "hashi" then return puzzle.title end
    return string.format("Hashi %s", puzzle.id:sub(-6))
end

local filterKeys = { "width", "height", "size", "difficulty", "solvability", "tiling", "source" }
local dimension = { min = 1, max = 100, step = 1 }
local nurikabeDimension = { min = 1, max = 50, step = 1 }
local hashiDimension = nurikabeDimension
local fillDimension = { min = 1, max = 2147483647, step = 1 }
local nurikabeLogic = { basic_logic = "Basic logic", advanced_logic = "Advanced logic",
    bounded_lookahead = "Bounded lookahead", requires_search = "Requires search" }
Puzzle.nurikabeSolvabilities = { "basic_logic", "advanced_logic", "bounded_lookahead", "requires_search" }
local filterDomains = {
    nonogram = {
        width = dimension, height = dimension,
        difficulty = { min = 1, max = 5, step = 0.25 }, solvability = { min = 1, max = 6 },
        source = { valid = Slitherlink.validSource },
    },
    hashi = {
        width = hashiDimension, height = hashiDimension, size = { min = 20, max = 50, step = 5 },
        difficulty = Puzzle.difficultyNames, solvability = Puzzle.hashiSolvabilityNames,
        source = { valid = Slitherlink.validSource },
    },
    nurikabe = { width = nurikabeDimension, height = nurikabeDimension, difficulty = Puzzle.difficultyNames,
        solvability = nurikabeLogic, source = { valid = Slitherlink.validSource } },
    ["fill-a-pix"] = { width = fillDimension, height = fillDimension },
    slitherlink = { width = dimension, height = dimension, size = dimension, difficulty = Slitherlink.difficultyNames,
        tiling = Slitherlink.tilingNames, source = { valid = Slitherlink.validSource } },
}

local function filterValue(domain, token)
    if domain.valid then return domain.valid(token) and token end
    if not domain.min then return domain[token] and token end
    local pattern = domain.step == 0.25 and "^[%d.]+$" or "^%d+$"
    local number = token:match(pattern) and tonumber(token)
    if number and number >= domain.min and number <= domain.max and (number - domain.min) % (domain.step or 1) == 0 then
        return number
    end
end

function Puzzle.filterSelection(game, key, value)
    local domain = filterDomains[game][key]
    if not domain then return end
    if type(value) == "number" then value = tostring(value) end
    if type(value) ~= "string" or #value == 0 or #value > 1024 then return end

    local terms, selected, ranges = {}, {}, {}
    for term in (value .. ","):gmatch("(.-),") do
        local first, last = term:match("^%s*([%d.]+)%s*%-%s*([%d.]+)%s*$")
        if first then
            if not domain.step then return end
            first, last = filterValue(domain, first), filterValue(domain, last)
            if not first or not last or first > last then return end
            ranges[#ranges + 1] = { first, last }
            terms[#terms + 1] = first == last and tostring(first) or first .. "-" .. last
        else
            local entry = filterValue(domain, term:match("^%s*(.-)%s*$"))
            if not entry then return end
            selected[entry] = true
            terms[#terms + 1] = tostring(entry)
        end
    end
    if #ranges > 0 then
        setmetatable(selected, { __index = function(_, number)
            if type(number) ~= "number" then return end
            for _, range in ipairs(ranges) do
                if number >= range[1] and number <= range[2] and (number - range[1]) % domain.step == 0 then
                    return true
                end
            end
        end })
    end
    return table.concat(terms, ","), selected
end

function Puzzle.filters(game, value)
    value = type(value) == "table" and value or {}
    local filters = {}
    for _, key in ipairs(filterKeys) do
        local expression = Puzzle.filterSelection(game, key, value[key])
        if expression and (key == "width" or key == "height") then expression = tonumber(expression) or expression end
        filters[key] = expression
    end
    if filters.size then
        local size = tonumber(filters.size)
        filters.width, filters.height = size, size
        if size then filters.size = nil end
    end
    return filters
end

function Puzzle.filterMatcher(game, filters)
    local selections = {}
    for key, value in pairs(filters) do
        local _, selected = Puzzle.filterSelection(game, key, value)
        selections[key] = selected
    end
    local function matches(key, value)
        local selected = selections[key]
        return not selected or selected[value] == true
    end
    return function(puzzle)
        local metadata = type(puzzle.metadata) == "table" and puzzle.metadata or {}
        local difficulty, solvability = metadata.difficulty, metadata.solvability
        if game == "nonogram" then difficulty, solvability = tonumber(difficulty), tonumber(solvability) end
        return matches("width", puzzle.width) and matches("height", puzzle.height)
            and matches("size", puzzle.width) and matches("size", puzzle.height)
            and matches("difficulty", difficulty) and matches("solvability", solvability)
            and matches("tiling", metadata.tiling)
            and (game ~= "hashi" or not filters.size or puzzle.width == puzzle.height)
            and matches("source", metadata.source or (game == "nonogram" and "webpbn" or game == "hashi" and "bridges"))
    end
end

function Puzzle.filterParameters(filters)
    local parameters, advanced = {}, false
    for key, value in pairs(filters) do
        local extended = type(value) == "string" and (value:find(",", 1, true)
            or (key ~= "tiling" and key ~= "source" and value:find("-", 1, true)))
        if key == "size" or extended then
            parameters[key .. "Filter"] = value
            advanced = true
        else
            parameters[key] = value
        end
    end
    return parameters, advanced
end

function Puzzle.catalogQuery(game, filters)
    local parameters, advanced = Puzzle.filterParameters(filters)
    local query = "game=" .. game
    for _, key in ipairs(filterKeys) do
        if parameters[key] then query = query .. "&" .. key .. "=" .. parameters[key] end
        local extended = key .. "Filter"
        if parameters[extended] then query = query .. "&" .. extended .. "=" .. parameters[extended] end
    end
    return query, advanced
end

return Puzzle
