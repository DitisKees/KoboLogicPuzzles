local EdgeHistory = {}

function EdgeHistory:marks() return table.concat(self.state) end

function EdgeHistory:setMark(edge, mark)
    local previous = self.state[edge]
    if previous == mark then return end
    return self:commit({ { edge = edge, before = previous, after = mark } })
end

function EdgeHistory:commit(changes)
    if #changes == 0 then return end
    self.undo[#self.undo + 1] = changes
    self.redo = {}
    return self:apply(changes, "after")
end

function EdgeHistory:apply(changes, key)
    for _, change in ipairs(changes) do self.state[change.edge] = change[key] end
    self.completed = nil
    return changes
end

function EdgeHistory:undoStroke()
    local changes = table.remove(self.undo)
    if not changes then return end
    self.redo[#self.redo + 1] = changes
    return self:apply(changes, "before")
end

function EdgeHistory:redoStroke()
    local changes = table.remove(self.redo)
    if not changes then return end
    self.undo[#self.undo + 1] = changes
    return self:apply(changes, "after")
end

function EdgeHistory:restoreCheckpoint(marks)
    if not self.validMarks(self.puzzle, marks) then return end
    local changes = {}
    for index, before in ipairs(self.state) do
        local after = marks:sub(index, index)
        if before ~= after then changes[#changes + 1] = { edge = index, before = before, after = after } end
    end
    return self:commit(changes)
end

function EdgeHistory:checkSolution()
    local errors, remaining = 0, 0
    for i, actual in ipairs(self.state) do
        local expected = self.solution:sub(i, i)
        if actual == "#" and expected ~= "#" or actual == "x" and expected == "#" then
            errors = errors + 1
        elseif actual == "." and expected == "#" then
            remaining = remaining + 1
        end
    end
    return errors, remaining
end

return EdgeHistory
