-- Test du lecteur : intro détectée → Entrée la passe ; générique → épisode suivant tout seul au bout de 10 s
local out = mp.get_opt('out')
local log = {}
local function note(s) log[#log + 1] = s; mp.msg.warn('TEST ' .. s) end
local function finish()
    local f = io.open(out, 'w'); f:write(table.concat(log, '\n') .. '\n'); f:close(); mp.command('quit')
end
local function wait(cond, secs, fn, label)
    local t0 = mp.get_time()
    local tm
    tm = mp.add_periodic_timer(0.25, function()
        if cond() then tm:kill(); fn()
        elseif mp.get_time() - t0 > secs then tm:kill(); note('DELAI DEPASSE : ' .. label); finish() end
    end)
end
local function epi() return mp.get_property_native('user-data/kshosc/episode') or {} end
local started = false
mp.register_event('file-loaded', function()
    if started then return end
    started = true
    wait(function() return epi().intro ~= nil end, 120, function()
        local e = epi()
        note(string.format('intro %.1f-%.1f fin %s source %s serie %s', e.intro[1], e.intro[2],
            e.credits and string.format('%.1f-%.1f', e.credits[1], e.credits[2]) or 'aucune', tostring(e.src), tostring(e.series)))
        mp.commandv('seek', tostring(e.intro[1] + 3), 'absolute+exact')
        mp.add_timeout(1.5, function()
            mp.commandv('screenshot-to-file', mp.get_opt('shots') .. '/intro.png', 'window')
            mp.commandv('keypress', 'ENTER')
            mp.add_timeout(1.0, function()
                note(string.format('apres Entree : %.1f s (attendu %.1f)', mp.get_property_number('time-pos', -1), e.intro[2]))
                local c = e.credits and e.credits[1] or (mp.get_property_number('duration') - 25)
                mp.commandv('seek', tostring(c + 1), 'absolute+exact')
                mp.add_timeout(2, function() mp.commandv('screenshot-to-file', mp.get_opt('shots') .. '/suivant.png', 'window') end)
                local p0 = mp.get_property('path')
                wait(function() return mp.get_property('path') ~= p0 end, 30, function()
                    note('episode suivant : ' .. mp.get_property('path'))
                    finish()
                end, 'episode suivant')
            end)
        end)
    end, 'detection intro')
end)
