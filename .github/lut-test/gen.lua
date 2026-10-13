-- Extrait cube_to_shader() de kshosc.lua et génère le shader d'un .cube (test CI)
local src = io.open(arg[1]):read('*a')
local a = src:find('local TMP = ', 1, true)
local b = src:find('local function lut_params', 1, true)
local chunk = src:sub(a, b - 1)
utils = {
    file_info = function(p) local f = io.open(p) if not f then return nil end local n = f:seek('end') f:close() return { size = n, mtime = 0 } end,
    join_path = function(x, y) return x .. '/' .. y end,
    getpid = function() return 1 end,
}
os.getenv = function() return arg[3] end
local fn = assert(loadstring(chunk .. '\nreturn cube_to_shader'))()
local t = os.clock()
local out = fn(arg[2])
print('shader:', out, string.format('%.2fs', os.clock() - t))
assert(out, 'génération échouée')
