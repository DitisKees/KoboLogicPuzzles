local Board = require("kobononogram/board")
local Hashi = require("kobononogram/hashiboard")

local Puzzle = {}
Puzzle.games = { "nonogram", "hashi" }
Puzzle.gameNames = { nonogram = "Nonogram", hashi = "Hashi" }
Puzzle.difficulties = { "easy", "medium", "hard", "expert" }
Puzzle.difficultyNames = { easy = "Easy", medium = "Medium", hard = "Hard", expert = "Expert" }
Puzzle.hashiSolvabilities = { "basic_logic", "advanced_logic", "bounded_lookahead", "requires_search", "unknown" }
Puzzle.hashiSolvabilityNames = { basic_logic = "Basic logic", advanced_logic = "Advanced logic",
    bounded_lookahead = "Bounded lookahead", requires_search = "Requires search", unknown = "Solvability unknown" }
Puzzle.nonogramSolvabilityNames = { "Trivial", "Line logic", "Moderate lookahead", "Deep lookahead", "Some guessing", "Much guessing" }

function Puzzle.localId(value)
    if type(value) ~= "string" then return end
    local number = value:match("^nonogram%-webpbn%-([1-9]%d*)$")
    if number and #number <= 10 and tonumber(number) <= 2147483647 then return tonumber(number) end
    local hash = value:match("^hashi%-local%-([0-9a-f]+)$")
    if hash and #hash == 64 then return value end
end

function Puzzle.isId(id)
    if type(id) == "number" then return id >= 1 and id <= 2147483647 and id == math.floor(id) end
    return type(id) == "string" and Puzzle.localId(id) == id
end

function Puzzle.wireId(id)
    if type(id) == "number" then return "nonogram-webpbn-" .. id end
    return id
end

function Puzzle.game(id) return type(id) == "number" and "nonogram" or "hashi" end

function Puzzle.rules(id) return type(id) == "number" and Board or Hashi end

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
    if puzzle.game == "nonogram" and puzzle.solution ~= nil and not Board.validSolution(decoded, puzzle.solution) then return end
    return decoded
end

function Puzzle.decodeProgress(puzzle, progress)
    if type(progress) ~= "table" or progress.schemaVersion ~= 1 or progress.puzzleId ~= Puzzle.wireId(puzzle.id)
        or type(progress.completed) ~= "boolean" then return end
    local marks
    if type(puzzle.id) == "number" then
        if not Board.validMarks(puzzle, progress.marks) then return end
        marks = progress.marks
    else
        marks = Hashi.decodeMarks(puzzle, progress.marks, progress.completed)
        if not marks then return end
    end
    return { marks = marks, completed = progress.completed, backedUpAt = progress.backedUpAt }
end

function Puzzle.encodeProgress(progress)
    local id = progress.puzzleId
    return { puzzleId = Puzzle.wireId(id), completed = progress.completed,
        marks = type(id) == "number" and progress.marks or Hashi.encodeMarks(progress.marks) }
end

function Puzzle.title(puzzle)
    if Puzzle.game(puzzle.id) == "nonogram" then return puzzle.title end
    return string.format("Hashi %s", puzzle.id:sub(-6))
end

function Puzzle.filters(game, value)
    value = type(value) == "table" and value or {}
    local filters = {}
    for _, key in ipairs({ "width", "height" }) do
        local size = value[key]
        if type(size) == "number" and size >= 1 and size <= 100 and size == math.floor(size) then
            filters[key] = size
        end
    end
    if game == "hashi" then
        if Puzzle.difficultyNames[value.difficulty] then filters.difficulty = value.difficulty end
        if Puzzle.hashiSolvabilityNames[value.solvability] then filters.solvability = value.solvability end
    else
        local difficulty = tonumber(value.difficulty)
        if difficulty and difficulty >= 1 and difficulty <= 5 and difficulty * 4 % 1 == 0 then
            filters.difficulty = tostring(difficulty)
        end
        local solvability = tonumber(value.solvability)
        if solvability and Puzzle.nonogramSolvabilityNames[solvability] then filters.solvability = tostring(solvability) end
    end
    return filters
end

function Puzzle.matchesFilters(puzzle, filters)
    local metadata = type(puzzle.metadata) == "table" and puzzle.metadata or {}
    return (not filters.width or filters.width == puzzle.width) and (not filters.height or filters.height == puzzle.height)
        and (not filters.difficulty or filters.difficulty == tostring(metadata.difficulty))
        and (not filters.solvability or filters.solvability == tostring(metadata.solvability))
end

function Puzzle.catalogQuery(game, filters)
    local query = "game=" .. game
    for _, key in ipairs({ "width", "height", "difficulty", "solvability" }) do
        if filters[key] then query = query .. "&" .. key .. "=" .. filters[key] end
    end
    return query
end

return Puzzle
