-- Script mpv : captures avec intensité 100 %, 0 %, puis 50 % + avant/après au milieu
local dir = mp.get_opt('shotdir')
local function shot(name) mp.commandv('screenshot-to-file', dir .. '/' .. name .. '.png', 'window') end
mp.register_event('file-loaded', function()
    mp.add_timeout(2, function()
        shot('k100')
        mp.set_property('glsl-shader-opts', 'ksh_k=0.0000,ksh_split=-1.0000')
        mp.add_timeout(1.5, function()
            shot('k0')
            mp.set_property('glsl-shader-opts', 'ksh_k=0.5000,ksh_split=0.5000')
            mp.add_timeout(1.5, function() shot('split'); mp.command('quit') end)
        end)
    end)
end)
