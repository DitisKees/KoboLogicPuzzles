local EdgeHistory = require("kobologicpuzzles/edgehistory")

local Slitherlink = setmetatable({}, { __index = EdgeHistory })
Slitherlink.__index = Slitherlink

Slitherlink.tilings = {
    "square", "triangular", "honeycomb", "snub-square", "cairo", "great-hexagonal", "octagonal",
    "kites", "floret", "dodecagonal", "great-dodecagonal", "penrose-kite-dart", "penrose-rhombs",
    "great-great-dodecagonal", "kagome", "compass-dodecagonal", "hats", "spectres",
    "altair", "snowflake", "laves", "rhombille", "septistar",
}
Slitherlink.tilingNames = {
    square = "Squares", triangular = "Triangles", honeycomb = "Honeycomb", ["snub-square"] = "Snub-Square",
    cairo = "Cairo pentagons", ["great-hexagonal"] = "Great-Hexagonal", octagonal = "Octagons and squares",
    kites = "Kites", floret = "Floret", dodecagonal = "Dodecagonal", ["great-dodecagonal"] = "Great-Dodecagonal",
    ["penrose-kite-dart"] = "Penrose kites and darts", ["penrose-rhombs"] = "Penrose rhombs",
    ["great-great-dodecagonal"] = "Great-Great-Dodecagonal", kagome = "Kagome (triangles and hexagons)",
    ["compass-dodecagonal"] = "Compass-Dodecagonal", hats = "Hats", spectres = "Spectres",
    altair = "Altair", snowflake = "Snowflake", laves = "Laves", rhombille = "Rhombille", septistar = "Septistar",
}
Slitherlink.difficulties = { "easy", "normal", "tricky", "hard" }
Slitherlink.difficultyNames = { easy = "Easy", normal = "Normal", tricky = "Tricky", hard = "Hard" }

function Slitherlink.validSource(source)
    return type(source) == "string" and #source <= 40 and source:match("^[a-z][a-z0-9-]*$") ~= nil
end

local function integer(value, minimum, maximum)
    return type(value) == "number" and value >= minimum and value <= maximum and value == math.floor(value)
end

function Slitherlink.validPuzzle(puzzle)
    local vertices, edges, faces, metadata = puzzle.vertices, puzzle.edges, puzzle.faces, puzzle.metadata
    if not integer(puzzle.width, 1, 100) or not integer(puzzle.height, 1, 100) or type(puzzle.title) ~= "string"
        or type(vertices) ~= "table" or #vertices < 3 or #vertices > 60000
        or type(edges) ~= "table" or #edges < 3 or #edges > 100000
        or type(faces) ~= "table" or #faces == 0 or #faces > 40000
        or type(metadata) ~= "table" or metadata.faceCount ~= #faces or not Slitherlink.tilingNames[metadata.tiling]
        or not Slitherlink.validSource(metadata.source) or type(metadata.sourceName) ~= "string" then return false end
    for i, vertex in ipairs(vertices) do
        if type(vertex) ~= "table" or not integer(vertex.x, 0, 20000000) or not integer(vertex.y, 0, 20000000) then
            return false
        end
        local previous = vertices[i - 1]
        if previous and (vertex.x < previous.x or (vertex.x == previous.x and vertex.y <= previous.y)) then return false end
    end
    for i, edge in ipairs(edges) do
        if type(edge) ~= "table" or not integer(edge.first, 0, #vertices - 1)
            or not integer(edge.second, edge.first + 1, #vertices - 1) then return false end
        local previous = edges[i - 1]
        if previous and (edge.first < previous.first or (edge.first == previous.first and edge.second <= previous.second)) then
            return false
        end
    end
    local incidence = {}
    for _, face in ipairs(faces) do
        if type(face) ~= "table" or type(face.edges) ~= "table" or #face.edges < 3 or #face.edges > 64
            or not integer(face.x, 0, 20000000) or not integer(face.y, 0, 20000000)
            or face.clue ~= nil and face.clue ~= false and not integer(face.clue, 0, #face.edges) then return false end
        local previous = -1
        for _, edge in ipairs(face.edges) do
            if not integer(edge, previous + 1, #edges - 1) then return false end
            incidence[edge] = (incidence[edge] or 0) + 1
            previous = edge
        end
    end
    for edge = 0, #edges - 1 do
        if not incidence[edge] or incidence[edge] > 2 then return false end
    end
    return true
end

local function complete(puzzle, state)
    for _, face in ipairs(puzzle.faces) do
        if face.clue then
            local count = 0
            for _, edge in ipairs(face.edges) do
                if state[edge + 1] == "#" then count = count + 1 end
            end
            if count ~= face.clue then return false end
        end
    end
    local first, second, start, count = {}, {}, nil, 0
    local function add(a, b)
        if not first[a] then first[a] = b
        elseif not second[a] then second[a] = b
        else return false end
        return true
    end
    for i, edge in ipairs(puzzle.edges) do
        if state[i] == "#" then
            local a, b = edge.first + 1, edge.second + 1
            if not add(a, b) or not add(b, a) then return false end
            start, count = a, count + 1
        end
    end
    if not start then return false end

    local previous, current, walked = nil, start, 0
    repeat
        if not second[current] then return false end
        previous, current = current, first[current] == previous and second[current] or first[current]
        walked = walked + 1
    until current == start
    return count == walked
end

function Slitherlink.validMarks(puzzle, marks, completed)
    if type(marks) ~= "string" or #marks ~= #puzzle.edges or marks:find("[^.#x]") then return false end
    if not completed then return true end
    local state = {}
    for mark in marks:gmatch(".") do state[#state + 1] = mark end
    return complete(puzzle, state)
end

function Slitherlink.validSolution(puzzle, solution)
    return type(solution) == "string" and not solution:find("x") and Slitherlink.validMarks(puzzle, solution, true)
end

function Slitherlink:new(puzzle, marks)
    local board = setmetatable({ puzzle = puzzle, solution = puzzle.solution, state = {}, undo = {}, redo = {},
        width = 0, height = 0 }, self)
    for mark in (marks or string.rep(".", #puzzle.edges)):gmatch(".") do board.state[#board.state + 1] = mark end
    for _, vertex in ipairs(puzzle.vertices) do
        board.width, board.height = math.max(board.width, vertex.x), math.max(board.height, vertex.y)
    end
    local lengths = {}
    for i, edge in ipairs(puzzle.edges) do
        local a, b = puzzle.vertices[edge.first + 1], puzzle.vertices[edge.second + 1]
        lengths[i] = (b.x - a.x)^2 + (b.y - a.y)^2
    end
    table.sort(lengths)
    board.edgeSize = math.sqrt(lengths[math.ceil(#lengths / 2)])
    return board
end

function Slitherlink:isComplete()
    if self.completed == nil then self.completed = complete(self.puzzle, self.state) end
    return self.completed
end

function Slitherlink.progress(puzzle, marks)
    local _, decided = (marks or ""):gsub("[#x]", "")
    return decided, #puzzle.edges
end

return Slitherlink
