-- KSHPLAYER · barre de contrôle maison ("pilule flottante")
-- Apparaît quand la souris approche du bas de l'image, disparaît toute seule.
-- Icônes : Material Icons Round (Google, Apache 2.0).

local mp = require 'mp'
local utils = require 'mp.utils'

-- Options passées par l'appli (édition PRO, dossiers LUT / captures)
local opts = { pro = false, lutdir = '', shotdir = '', platform = 'windows', font = '' }
require('mp.options').read_options(opts, 'kshosc')
local PRO = opts.pro
local MAC = opts.platform == 'mac'

-- ---------- Apparence ----------
local ACCENT     = '3A28F0'   -- rouge KSHFLIX #F0283A (couleurs ASS en BGR)
local WHITE      = 'FFFFFF'
local PANEL      = '171212'   -- surface KSHFLIX #121217
local BORDER     = '2C2323'   -- trait KSHFLIX #23232C
local ICON_FONT  = 'MaterialIconsRound-Regular'
local TEXT_FONT  = (opts.font ~= '' and opts.font) or 'Segoe UI Semibold'
local BRAND_FONT = 'Unbounded'
local HIDE_DELAY = 1.6        -- secondes sans bouger avant disparition
local FADE_SPEED = 8          -- vitesse du fondu (par seconde)

-- ---------- État ----------
local overlay = mp.create_osd_overlay('ass-events')
local W, H = 0, 0
local mouse = { x = -1, y = -1, hover = false }
local last_move = 0
local flash_until = 0
local fade = { bottom = 0, top = 0 }
local regions = {}
local menu = nil              -- { kind = 'sub'|'audio', items = {...}, anchor_x = n }
local dragging = false
local bindings_on = false
local cleared = true

-- PRO
local info_on = false
local guide_mode = 0          -- 0 aucun · 1 zones de sécurité · 2 2.39 · 3 1.85 · 4 9:16 · 5 4:5 · 6 tiers
local GUIDES = { 'Zones de sécurité', 'Cadre 2.39:1', 'Cadre 1.85:1', 'Vertical 9:16', 'Portrait 4:5', 'Règle des tiers' }
local guide_flash = 0

-- ---------- Utilitaires ASS ----------
local function esc(s)
    s = tostring(s or '')
    return (s:gsub('\\', '\\\239\187\191'):gsub('{', '\\{'):gsub('^ ', '\\h'))
end

local function alpha(op, f)
    local v = math.floor(255 - 255 * op * f + 0.5)
    if v < 0 then v = 0 elseif v > 255 then v = 255 end
    return string.format('%02X', v)
end

local function n(v) return string.format('%d', math.floor(v + 0.5)) end

local function rrect(x, y, w, h, r, color, op, f)
    r = math.min(r, w / 2, h / 2)
    local x2, y2 = x + w, y + h
    local p = table.concat({
        'm', n(x + r), n(y), 'l', n(x2 - r), n(y),
        'b', n(x2 - r * 0.45), n(y), n(x2), n(y + r * 0.45), n(x2), n(y + r),
        'l', n(x2), n(y2 - r),
        'b', n(x2), n(y2 - r * 0.45), n(x2 - r * 0.45), n(y2), n(x2 - r), n(y2),
        'l', n(x + r), n(y2),
        'b', n(x + r * 0.45), n(y2), n(x), n(y2 - r * 0.45), n(x), n(y2 - r),
        'l', n(x), n(y + r),
        'b', n(x), n(y + r * 0.45), n(x + r * 0.45), n(y), n(x + r), n(y),
    }, ' ')
    return '{\\an7\\pos(0,0)\\bord0\\shad0\\blur0\\1c&H' .. color .. '&\\1a&H' .. alpha(op, f) .. '&\\p1}' .. p .. '{\\p0}'
end

local function text(x, y, an, s, size, color, op, f, font)
    return string.format('{\\an%d\\pos(%s,%s)\\fn%s\\fs%s\\bord0\\shad0\\blur0\\1c&H%s&\\1a&H%s&}%s',
        an, n(x), n(y), font or TEXT_FONT, n(size), color, alpha(op, f), esc(s))
end

local function icon(x, y, name, size, color, op, f)
    return text(x, y, 5, name, size, color, op, f, ICON_FONT)
end

local function fmt_time(t)
    if not t or t < 0 then t = 0 end
    t = math.floor(t)
    local h, m, s = math.floor(t / 3600), math.floor(t / 60) % 60, t % 60
    if h > 0 then return string.format('%d:%02d:%02d', h, m, s) end
    return string.format('%d:%02d', m, s)
end

-- Timecode HH:MM:SS:II (images), avec le timecode de départ du fichier s'il existe
local function fps_now()
    local f = mp.get_property_number('container-fps') or mp.get_property_number('estimated-vf-fps')
    if not f or f <= 0 then f = 25 end
    return f
end

local function tc_offset_frames(base)
    local md = mp.get_property_native('metadata') or {}
    local tc = md.timecode or md.TIMECODE
    if not tc then return 0 end
    local h, m, sec, fr = tc:match('(%d+)[:;](%d+)[:;](%d+)[:;](%d+)')
    if not h then return 0 end
    return ((tonumber(h) * 60 + tonumber(m)) * 60 + tonumber(sec)) * base + tonumber(fr)
end

local function fmt_tc(t, with_offset)
    if not t or t < 0 then t = 0 end
    local f = fps_now()
    local base = math.floor(f + 0.5)
    local total = math.floor(t * f + 0.5) + (with_offset and tc_offset_frames(base) or 0)
    local fr = total % base
    local secs = math.floor(total / base)
    return string.format('%02d:%02d:%02d:%02d', math.floor(secs / 3600), math.floor(secs / 60) % 60, secs % 60, fr)
end

local function fmt_main(t, with_offset)
    if PRO then return fmt_tc(t, with_offset) end
    return fmt_time(t)
end

local function inside(r, x, y) return x >= r.x1 and x <= r.x2 and y >= r.y1 and y <= r.y2 end

-- ---------- Pistes audio / sous-titres ----------
local function track_label(t)
    local parts = {}
    if t.lang then parts[#parts + 1] = t.lang:upper() end
    if t.title then parts[#parts + 1] = t.title end
    if t.codec then parts[#parts + 1] = t.codec:upper() end
    if t['demux-channel-count'] then parts[#parts + 1] = t['demux-channel-count'] .. ' can.' end
    if t.forced then parts[#parts + 1] = 'forcés' end
    if #parts == 0 then parts[1] = 'Piste ' .. t.id end
    return table.concat(parts, '  ·  ')
end

local function open_menu(kind, anchor_x)
    local items = {}
    local prop = kind == 'sub' and 'sid' or 'aid'
    if kind == 'sub' then
        items[#items + 1] = { label = 'Désactivés', id = 'no', selected = mp.get_property(prop) == 'no' }
    end
    for _, t in ipairs(mp.get_property_native('track-list') or {}) do
        if t.type == kind then
            items[#items + 1] = { label = track_label(t), id = t.id, selected = t.selected }
        end
    end
    menu = { kind = kind, prop = prop, items = items, anchor_x = anchor_x,
             title = kind == 'sub' and 'SOUS-TITRES' or 'AUDIO' }
end

-- ================= PRO : moteur LUT =================
-- Tout se passe sur la carte graphique : le .cube est converti en petit shader (texture 3D)
-- dont l'intensité et la ligne avant/après sont des réglages modifiables en direct,
-- sans filtre logiciel ni changement de décodage → aucun ralentissement, même en 4K HDR.
local LUT = { file = nil, k = 1.0, split = false, pos = 0.5, with_capture = false, shader = nil, hwdec = nil, vf = false }
local LUT_DB = (opts.lutdir ~= '') and utils.join_path(opts.lutdir, '..' .. (MAC and '/' or '\\') .. 'luts-par-dossier.json') or nil
local lut_db_cache = nil

local function lut_name() return LUT.file and select(2, utils.split_path(LUT.file)):gsub('%.[Cc][Uu][Bb][Ee]$', '') or '' end

local function lut_db()
    if lut_db_cache then return lut_db_cache end
    lut_db_cache = {}
    if LUT_DB then
        local f = io.open(LUT_DB, 'r')
        if f then lut_db_cache = utils.parse_json(f:read('*a') or '') or {}; f:close() end
    end
    lut_db_cache.dirs = lut_db_cache.dirs or {}
    return lut_db_cache
end

local function lut_db_save()
    if not LUT_DB then return end
    local f = io.open(LUT_DB, 'w')
    if f then f:write(utils.format_json(lut_db()) or '{}'); f:close() end
end

local function video_dir() return (utils.split_path(mp.get_property('path', ''))) or '' end
local function remembered() local e = lut_db().dirs[video_dir()]; return e and LUT.file and e.file == LUT.file end

local function ff_path(p)   -- chemin pour un filtre FFmpeg : barres « / » et deux-points échappés
    return (p:gsub('\\', '/'):gsub(':', '\\:'):gsub("'", ''))
end

local function blend_expr(pos, k)
    return string.format('if(lt(X,W*%.4f),A,A+(B-A)*%.3f)', pos, k)
end

local function vf_remove() mp.commandv('vf', 'remove', '@ksh') end

-- .cube → shader GPU (mis en cache : un fichier par LUT, refait si le .cube change)
local TMP = os.getenv('TEMP') or os.getenv('TMPDIR') or '/tmp'
local lut_cache, lut_seq = {}, 0
local NUM = '([%-%+]?[%d%.]+[eE]?[%-%+]?%d*)'

local function cube_to_shader(file)
    local fi = utils.file_info(file)
    if not fi then return nil end
    local key = file .. '|' .. tostring(fi.size) .. '|' .. tostring(fi.mtime)
    if lut_cache[key] then return lut_cache[key] end
    local f = io.open(file, 'r')
    if not f then return nil end
    local size, dmin, dmax = nil, { 0, 0, 0 }, { 1, 1, 1 }
    local hex, cnt = {}, 0
    local function u16(x)
        local v = math.floor((tonumber(x) or 0) * 65535 + 0.5)
        if v < 0 then v = 0 elseif v > 65535 then v = 65535 end
        return v
    end
    for line in f:lines() do
        local l = line:gsub('^%s+', '')
        local c = l:sub(1, 1)
        if c:match('[%d%-%+%.]') then
            local r, g, b = l:match('^' .. NUM .. '%s+' .. NUM .. '%s+' .. NUM)
            if r then
                r, g, b = u16(r), u16(g), u16(b)
                cnt = cnt + 1
                hex[cnt] = string.format('%02x%02x%02x%02x%02x%02xffff',
                    r % 256, math.floor(r / 256), g % 256, math.floor(g / 256), b % 256, math.floor(b / 256))
            end
        elseif l:match('^LUT_3D_SIZE') then size = tonumber(l:match('^LUT_3D_SIZE%s+(%d+)'))
        elseif l:match('^LUT_1D_SIZE') then f:close(); return nil
        elseif l:match('^DOMAIN_MIN') then
            local a, b2, c2 = l:match('^DOMAIN_MIN%s+' .. NUM .. '%s+' .. NUM .. '%s+' .. NUM)
            if a then dmin = { tonumber(a), tonumber(b2), tonumber(c2) } end
        elseif l:match('^DOMAIN_MAX') then
            local a, b2, c2 = l:match('^DOMAIN_MAX%s+' .. NUM .. '%s+' .. NUM .. '%s+' .. NUM)
            if a then dmax = { tonumber(a), tonumber(b2), tonumber(c2) } end
        end
    end
    f:close()
    if not size or size < 2 or cnt ~= size * size * size then return nil end
    local rng = {}
    for i = 1, 3 do rng[i] = (dmax[i] - dmin[i]) ~= 0 and (dmax[i] - dmin[i]) or 1 end
    local S, O = (size - 1) / size, 0.5 / size
    lut_seq = lut_seq + 1
    local out = utils.join_path(TMP, string.format('kshplayer-lut-%d-%d.glsl', utils.getpid and utils.getpid() or 0, lut_seq))
    local w = io.open(out, 'w')
    if not w then return nil end
    w:write(string.format([[
//!PARAM ksh_k
//!TYPE float
//!MINIMUM 0.0
//!MAXIMUM 1.0
1.0

//!PARAM ksh_split
//!TYPE float
//!MINIMUM -1.0
//!MAXIMUM 1.0
-1.0

//!HOOK MAIN
//!BIND HOOKED
//!BIND KSHLUT
//!DESC KSHPLAYER LUT
vec4 hook() {
    vec4 c = HOOKED_texOff(0);
    vec3 x = clamp((c.rgb - vec3(%.6f, %.6f, %.6f)) / vec3(%.6f, %.6f, %.6f), 0.0, 1.0);
    vec3 l = texture(KSHLUT, x * %.8f + %.8f).rgb;
    float k = (HOOKED_pos.x < ksh_split) ? 0.0 : ksh_k;
    c.rgb = mix(c.rgb, l, k);
    return c;
}

//!TEXTURE KSHLUT
//!SIZE %d %d %d
//!FORMAT rgba16
//!FILTER LINEAR
]], dmin[1], dmin[2], dmin[3], rng[1], rng[2], rng[3], S, O, size, size, size))
    w:write(table.concat(hex))
    w:write('\n')
    w:close()
    lut_cache[key] = out
    return out
end

local function lut_params()
    mp.set_property('glsl-shader-opts', string.format('ksh_k=%.4f,ksh_split=%.4f',
        LUT.k, (LUT.split and LUT.pos) or -1))
end

local function shader_remove()
    if LUT.shader then mp.commandv('change-list', 'glsl-shaders', 'remove', LUT.shader); LUT.shader = nil end
end

local function apply_lut()
    if LUT.vf then vf_remove(); mp.set_property('hwdec', LUT.hwdec or 'auto-safe'); LUT.vf = false end
    if not LUT.file then shader_remove(); mp.set_property('lut', ''); return end
    local sh = cube_to_shader(LUT.file)
    if not sh then
        -- LUT 1D ou fichier inhabituel : moteur LUT intégré de mpv (100 %, sans comparaison)
        shader_remove(); mp.set_property('lut', LUT.file)
        return
    end
    mp.set_property('lut', '')
    lut_params()
    if LUT.shader ~= sh then
        shader_remove()
        mp.commandv('change-list', 'glsl-shaders', 'append', sh)
        LUT.shader = sh
    end
end

-- mise à jour en direct (glisser la séparation / l'intensité) : simple réglage du shader
local last_live = 0
local function lut_live(force)
    if not LUT.shader then apply_lut(); return end
    local now = mp.get_time()
    if not force and now - last_live < 0.016 then return end
    last_live = now
    lut_params()
end

local function set_lut(file, k)
    LUT.file = file
    if k then LUT.k = k end
    if not file then LUT.split = false end
    apply_lut()
    mp.osd_message(file and ('LUT : ' .. lut_name()) or 'LUT désactivée')
end

local function toggle_split()
    if not LUT.file then return end
    LUT.split = not LUT.split
    apply_lut()
    mp.osd_message(LUT.split and 'Avant / après  ·  glisse la ligne' or 'Comparaison désactivée')
end

local function toggle_remember()
    if not LUT.file then return end
    local db, d = lut_db(), video_dir()
    if remembered() then db.dirs[d] = nil; mp.osd_message('LUT oubliée pour ce dossier')
    else db.dirs[d] = { file = LUT.file, k = LUT.k }; mp.osd_message('LUT mémorisée pour ce dossier') end
    lut_db_save()
end

local function toggle_capture_lut()
    LUT.with_capture = not LUT.with_capture
    local db = lut_db(); db.capture_with_lut = LUT.with_capture; lut_db_save()
    mp.osd_message(LUT.with_capture and 'Captures AVEC la LUT' or 'Captures SANS la LUT (image brute)')
end

-- au chargement d'un fichier : LUT mémorisée pour son dossier
mp.register_event('file-loaded', function()
    if not PRO then return end
    LUT.with_capture = lut_db().capture_with_lut == true
    local e = lut_db().dirs[video_dir()]
    if e and e.file and utils.file_info(e.file) then
        LUT.file, LUT.k, LUT.split = e.file, tonumber(e.k) or 1, false
        apply_lut()
        mp.osd_message('LUT du dossier : ' .. lut_name(), 2.5)
    elseif LUT.file then
        apply_lut()
    end
end)

-- PRO : menu des LUT (.cube du dossier LUT + du dossier de la vidéo)
local function open_lut_menu(anchor_x)
    local current = LUT.file or ''
    local items = { { label = 'Aucune LUT', selected = current == '',
                      act = function() set_lut(nil) end } }
    local dirs = {}
    if opts.lutdir ~= '' then dirs[#dirs + 1] = opts.lutdir end
    local path = mp.get_property('path', '')
    local vdir = utils.split_path(path)
    if vdir and vdir ~= '' and vdir ~= opts.lutdir then dirs[#dirs + 1] = vdir end
    local seen = {}
    for _, d in ipairs(dirs) do
        for _, f in ipairs(utils.readdir(d, 'files') or {}) do
            if f:lower():match('%.cube$') and not seen[f] then
                seen[f] = true
                local full = utils.join_path(d, f)
                items[#items + 1] = { label = f:gsub('%.[Cc][Uu][Bb][Ee]$', ''), selected = current == full,
                    act = function() set_lut(full) end }
            end
        end
    end
    items[#items + 1] = { label = 'Ouvrir le dossier des LUT…', dim = true,
        act = function()
            if opts.lutdir ~= '' then
                mp.command_native_async({ name = 'subprocess', playback_only = false, detach = true,
                    args = { MAC and 'open' or 'explorer.exe', opts.lutdir } }, function() end)
            end
        end }
    menu = { kind = 'lut', items = items, anchor_x = anchor_x, title = 'LUT  ·  .cube' }
end

-- ---------- Outils PRO ----------
function capture()
    local where = MAC and 'Images/KSHPLAYER PRO' or 'Images\\KSHPLAYER PRO'
    if not (PRO and LUT.with_capture and LUT.file) then
        mp.commandv('screenshot', 'video')
        mp.osd_message('Image enregistrée (PNG) dans ' .. where, 2)
        return
    end
    -- capture AVEC la LUT : filtre FFmpeg le temps d'une image (l'écran garde son réglage)
    LUT.hwdec = mp.get_property('hwdec')
    mp.set_property('hwdec', 'auto-copy-safe')
    local g = string.format("lut3d=file='%s'", ff_path(LUT.file))
    if LUT.k < 0.999 then
        g = string.format("split[ksha][kshb];[kshb]lut3d=file='%s'[kshc];[ksha][kshc]blend=all_expr='%s'", ff_path(LUT.file), blend_expr(0, LUT.k))
    end
    vf_remove()
    mp.commandv('vf', 'add', '@ksh:lavfi=graph=%' .. #g .. '%' .. g)
    LUT.vf = true
    if LUT.shader then mp.set_property('glsl-shader-opts', 'ksh_k=0,ksh_split=-1') end
    mp.add_timeout(0.6, function()
        mp.commandv('screenshot', 'video')
        apply_lut()
        mp.osd_message('Image enregistrée AVEC la LUT (PNG) dans ' .. where, 2)
    end)
end

function cycle_guides()
    guide_mode = (guide_mode + 1) % (#GUIDES + 1)
    guide_flash = mp.get_time() + 1.5
end

local function set_in()  mp.set_property('ab-loop-a', tostring(mp.get_property_number('time-pos', 0))); mp.osd_message('Entrée (I)  ' .. fmt_tc(mp.get_property_number('time-pos', 0), true)) end
local function set_out() mp.set_property('ab-loop-b', tostring(mp.get_property_number('time-pos', 0))); mp.osd_message('Sortie (O)  ' .. fmt_tc(mp.get_property_number('time-pos', 0), true) .. '  ·  lecture en boucle') end
local function clear_io() mp.set_property('ab-loop-a', 'no'); mp.set_property('ab-loop-b', 'no'); mp.osd_message('Entrée / sortie effacées') end

-- J / K / L : marche arrière, pause, marche avant (x1, x2, x4, x8)
local function speed_chip()
    local sp = mp.get_property_number('speed', 1)
    local back = mp.get_property('play-direction') == 'backward'
    mp.osd_message((back and '◀◀  ' or '▶▶  ') .. string.format('x%g', sp), 1)
end
local function key_l()
    if mp.get_property('play-direction') == 'backward' then
        mp.set_property('play-direction', 'forward'); mp.set_property_number('speed', 1)
    elseif mp.get_property_native('pause') then
        mp.set_property_number('speed', 1)
    else
        mp.set_property_number('speed', math.min(8, mp.get_property_number('speed', 1) * 2))
    end
    mp.set_property_native('pause', false); speed_chip()
end
local function key_j()
    if mp.get_property('play-direction') ~= 'backward' then
        mp.set_property('play-direction', 'backward'); mp.set_property_number('speed', 1)
    elseif not mp.get_property_native('pause') then
        mp.set_property_number('speed', math.min(8, mp.get_property_number('speed', 1) * 2))
    end
    mp.set_property_native('pause', false); speed_chip()
end
local function key_k()
    mp.set_property_native('pause', true)
    mp.set_property('play-direction', 'forward'); mp.set_property_number('speed', 1)
end

-- Panneau d'infos techniques
local function chroma_of(pf)
    if not pf then return '?' end
    if pf:find('444') or pf:find('gbr') or pf:find('rgb') then return '4:4:4'
    elseif pf:find('422') or pf:find('p210') or pf:find('y210') then return '4:2:2'
    elseif pf:find('411') then return '4:1:1' end
    return '4:2:0'
end
local function depth_of(pf)
    if not pf then return '?' end
    local d = pf:match('p(%d%d)') or pf:match('(%d%d)le')
    if pf:find('p010') then d = '10' end
    if pf:find('p016') then d = '16' end
    return (d or '8') .. ' bits'
end

local function info_lines()
    local L = {}
    local function add(k, v) if v and v ~= '' then L[#L + 1] = { k, tostring(v) } end end
    local vp = mp.get_property_native('video-params') or {}
    local ap = mp.get_property_native('audio-params') or {}
    local size = mp.get_property_number('file-size')
    add('Fichier', (mp.get_property('file-format', '') or ''):upper() ..
        (size and string.format('  ·  %.2f Go', size / 1073741824) or ''))
    add('Vidéo', (mp.get_property('video-format', '') or ''):upper() .. '  ·  ' .. (mp.get_property('video-codec', '') or ''))
    if vp.w then add('Image', string.format('%d × %d  ·  %.3f i/s', vp.w, vp.h, fps_now())) end
    local vb = mp.get_property_number('video-bitrate')
    if vb then add('Débit', string.format('%.1f Mb/s', vb / 1e6)) end
    if vp.pixelformat then add('Échantillonnage', chroma_of(vp.pixelformat) .. '  ·  ' .. depth_of(vp.pixelformat) .. '  ·  ' .. vp.pixelformat) end
    if vp.colormatrix then add('Couleurs', (vp.primaries or '?') .. '  ·  ' .. (vp.gamma or '?') .. '  ·  ' .. vp.colormatrix .. '  ·  ' .. (vp.colorlevels or '')) end
    local hdr = nil
    if vp.gamma == 'pq' then hdr = 'HDR10 (PQ)' elseif vp.gamma == 'hlg' then hdr = 'HLG' end
    for _, t in ipairs(mp.get_property_native('track-list') or {}) do
        if t.type == 'video' and t.selected and t['dolby-vision-profile'] then
            hdr = 'Dolby Vision profil ' .. t['dolby-vision-profile'] .. (hdr and ('  ·  ' .. hdr) or '')
        end
    end
    if hdr then add('HDR', hdr .. (vp['max-luma'] and string.format('  ·  %d nits', vp['max-luma']) or '')) end
    local md = mp.get_property_native('metadata') or {}
    add('Timecode départ', md.timecode or md.TIMECODE)
    add('Décodage', (mp.get_property('hwdec-current', 'no') == 'no') and 'processeur' or ('carte graphique (' .. mp.get_property('hwdec-current') .. ')'))
    add('Audio', (mp.get_property('audio-codec-name', '') or ''):upper() ..
        (ap['channel-count'] and ('  ·  ' .. ap['channel-count'] .. ' canaux') or '') ..
        (ap.samplerate and string.format('  ·  %.1f kHz', ap.samplerate / 1000) or '') ..
        (ap.format and ('  ·  ' .. ap.format) or ''))
    if LUT.file then add('LUT', lut_name() .. string.format('  ·  %d %%', math.floor(LUT.k * 100 + 0.5))) end
    local drop = mp.get_property_number('frame-drop-count', 0)
    add('Images perdues', tostring(drop))
    return L
end

local function render_info(ass, s)
    local L = info_lines()
    local lh = 22 * s
    local w = math.min(560 * s, W - 32 * s)
    local h = #L * lh + 54 * s
    local x, y = 16 * s, 72 * s
    ass[#ass + 1] = rrect(x - 1 * s, y - 1 * s, w + 2 * s, h + 2 * s, 19 * s, BORDER, 0.9, 1)
    ass[#ass + 1] = rrect(x, y, w, h, 18 * s, PANEL, 0.9, 1)
    ass[#ass + 1] = text(x + 20 * s, y + 24 * s, 4, 'INFOS TECHNIQUES', 11 * s, ACCENT, 1, 1)
    for i, l in ipairs(L) do
        local ly = y + 44 * s + (i - 1) * lh + lh / 2
        ass[#ass + 1] = text(x + 20 * s, ly, 4, l[1], 12.5 * s, WHITE, 0.5, 1)
        local v = l[2]
        local mc = math.floor((w - 170 * s) / (7 * s))
        if #v > mc then v = v:sub(1, mc - 1) .. '…' end
        ass[#ass + 1] = text(x + 160 * s, ly, 4, v, 13 * s, WHITE, 0.95, 1)
    end
end

-- Repères de cadrage sur l'image
local function render_guides(ass, s)
    local d = mp.get_property_native('osd-dimensions') or {}
    local vx, vy = d.ml or 0, d.mt or 0
    local vw, vh = W - (d.ml or 0) - (d.mr or 0), H - (d.mt or 0) - (d.mb or 0)
    if vw <= 0 or vh <= 0 then return end
    local lw = math.max(1, 1.5 * s)
    local function frame(x, y, w, h, op, color)
        color = color or WHITE
        ass[#ass + 1] = rrect(x, y, w, lw, 0, color, op, 1)
        ass[#ass + 1] = rrect(x, y + h - lw, w, lw, 0, color, op, 1)
        ass[#ass + 1] = rrect(x, y, lw, h, 0, color, op, 1)
        ass[#ass + 1] = rrect(x + w - lw, y, lw, h, 0, color, op, 1)
    end
    local function mask(ratio)
        local cw, ch = vw, vw / ratio
        if ch > vh then ch = vh; cw = vh * ratio end
        local cx, cy = vx + (vw - cw) / 2, vy + (vh - ch) / 2
        if cy > vy then
            ass[#ass + 1] = rrect(vx, vy, vw, cy - vy, 0, '000000', 0.55, 1)
            ass[#ass + 1] = rrect(vx, cy + ch, vw, vy + vh - cy - ch, 0, '000000', 0.55, 1)
        end
        if cx > vx then
            ass[#ass + 1] = rrect(vx, vy, cx - vx, vh, 0, '000000', 0.55, 1)
            ass[#ass + 1] = rrect(cx + cw, vy, vx + vw - cx - cw, vh, 0, '000000', 0.55, 1)
        end
        frame(cx, cy, cw, ch, 0.8, ACCENT)
    end
    if guide_mode == 1 then
        frame(vx + vw * 0.035, vy + vh * 0.035, vw * 0.93, vh * 0.93, 0.7)        -- action safe 93 %
        frame(vx + vw * 0.05, vy + vh * 0.05, vw * 0.90, vh * 0.90, 0.45, ACCENT) -- title safe 90 %
        ass[#ass + 1] = rrect(vx + vw / 2 - 10 * s, vy + vh / 2 - lw / 2, 20 * s, lw, 0, WHITE, 0.7, 1)
        ass[#ass + 1] = rrect(vx + vw / 2 - lw / 2, vy + vh / 2 - 10 * s, lw, 20 * s, 0, WHITE, 0.7, 1)
    elseif guide_mode == 2 then mask(2.39)
    elseif guide_mode == 3 then mask(1.85)
    elseif guide_mode == 4 then mask(9 / 16)
    elseif guide_mode == 5 then mask(4 / 5)
    elseif guide_mode == 6 then
        for i = 1, 2 do
            ass[#ass + 1] = rrect(vx + vw * i / 3 - lw / 2, vy, lw, vh, 0, WHITE, 0.45, 1)
            ass[#ass + 1] = rrect(vx, vy + vh * i / 3 - lw / 2, vw, lw, 0, WHITE, 0.45, 1)
        end
    end
    if mp.get_time() < guide_flash then
        local label = GUIDES[guide_mode] or 'Repères masqués'
        local tw = #label * 7.6 * s + 40 * s
        ass[#ass + 1] = rrect(W / 2 - tw / 2, 18 * s, tw, 36 * s, 18 * s, PANEL, 0.9, 1)
        ass[#ass + 1] = text(W / 2, 36 * s, 5, label, 13 * s, WHITE, 1, 1)
    end
end

-- ---------- Mise en page adaptative (paysage, portrait, petites fenêtres) ----------
-- Taille de base = hauteur ; si la fenêtre est trop étroite (vidéo portrait), on réduit l'échelle
-- et on passe en barre « compacte » (sans curseur de volume ni ±10 s).
local function layout()
    local sh = math.max(0.75, math.min(1.7, H / 900))
    local full = PRO and 1000 or 860
    local compact = W < full * sh
    local base = compact and (PRO and 600 or 460) or full
    local sc = math.min(sh, (W - 16) / base)
    return math.max(0.5, math.min(1.7, sc)), compact
end

-- ---------- Dessin ----------
local function render()
    regions = {}
    local ass = {}
    local s, compact = layout()
    local fb, ft = fade.bottom, fade.top

    if PRO and LUT.split and LUT.file then
        local d = mp.get_property_native('osd-dimensions') or {}
        local vx, vy = d.ml or 0, d.mt or 0
        local vw, vh = W - (d.ml or 0) - (d.mr or 0), H - (d.mt or 0) - (d.mb or 0)
        if vw > 0 and vh > 0 then
            local lx = vx + vw * LUT.pos
            local hov = dragging == 'split' or math.abs(mouse.x - lx) < 14 * s
            ass[#ass + 1] = rrect(lx - 1 * s, vy, 2 * s, vh, 0, WHITE, hov and 1 or 0.85, 1)
            ass[#ass + 1] = rrect(lx - 16 * s, vy + vh / 2 - 16 * s, 32 * s, 32 * s, 16 * s, hov and ACCENT or PANEL, 0.95, 1)
            ass[#ass + 1] = icon(lx, vy + vh / 2, 'unfold_more', 18 * s, WHITE, 1, 1)
            ass[#ass + 1] = rrect(lx - 92 * s, vy + 14 * s, 80 * s, 26 * s, 13 * s, PANEL, 0.8, 1)
            ass[#ass + 1] = text(lx - 52 * s, vy + 27 * s, 5, 'AVANT', 11 * s, WHITE, 0.9, 1)
            ass[#ass + 1] = rrect(lx + 12 * s, vy + 14 * s, 80 * s, 26 * s, 13 * s, ACCENT, 0.9, 1)
            ass[#ass + 1] = text(lx + 52 * s, vy + 27 * s, 5, 'APRÈS', 11 * s, WHITE, 1, 1)
            regions[#regions + 1] = { x1 = lx - 16 * s, y1 = vy, x2 = lx + 16 * s, y2 = vy + vh, id = 'split',
                                      vx = vx, vw = vw }
        end
    end
    if PRO and guide_mode > 0 then render_guides(ass, s) end
    if PRO and info_on then render_info(ass, s) end
    if PRO and guide_mode == 0 and mp.get_time() < guide_flash then render_guides(ass, s) end

    -- === Pilule du bas (style KSHFLIX) ===
    local pw = math.min((compact and (PRO and 580 or 440) or (PRO and 980 or 820)) * s, W - 16 * s)
    local ph = 104 * s
    local px = (W - pw) / 2
    local py = H - ph - 26 * s
    if fb > 0.01 then
        -- carte : bord fin + fond sombre translucide
        ass[#ass + 1] = rrect(px - 1 * s, py - 1 * s, pw + 2 * s, ph + 2 * s, 24 * s, BORDER, 0.9, fb)
        ass[#ass + 1] = rrect(px, py, pw, ph, 23 * s, PANEL, 0.86, fb)

        local row1 = py + 36 * s     -- boutons
        local row2 = py + 78 * s     -- timeline

        local function button(x, name, size, id, on_click, on_wheel, active)
            local r = { x1 = x - 19 * s, y1 = row1 - 19 * s, x2 = x + 19 * s, y2 = row1 + 19 * s,
                        id = id, click = on_click, wheel = on_wheel }
            if inside(r, mouse.x, mouse.y) then
                ass[#ass + 1] = rrect(r.x1, r.y1, 38 * s, 38 * s, 19 * s, WHITE, 0.12, fb)
            end
            ass[#ass + 1] = icon(x, row1, name, size, active and ACCENT or WHITE, 1, fb)
            regions[#regions + 1] = r
        end

        -- Gauche : volume (icône + curseur)
        local muted = mp.get_property_native('mute')
        local vol = mp.get_property_number('volume', 100)
        local vmax = mp.get_property_number('volume-max', 100)
        local function vol_wheel(dir) mp.commandv('add', 'volume', tostring(dir * 5)) end
        button(px + 34 * s, muted and 'volume_off' or (vol < 50 and 'volume_down' or 'volume_up'), 23 * s, 'vol',
            function() mp.commandv('cycle', 'mute') end, vol_wheel)
        if not compact then
        local vx1, vx2 = px + 60 * s, px + 150 * s
        local vr = math.max(0, math.min(1, vol / vmax))
        ass[#ass + 1] = rrect(vx1, row1 - 2 * s, vx2 - vx1, 4 * s, 2 * s, WHITE, 0.22, fb)
        if not muted then
            ass[#ass + 1] = rrect(vx1, row1 - 2 * s, (vx2 - vx1) * vr, 4 * s, 2 * s, WHITE, 0.95, fb)
        end
        local vhov = dragging == 'vol' or (mouse.x >= vx1 - 8 * s and mouse.x <= vx2 + 8 * s and math.abs(mouse.y - row1) <= 14 * s)
        local vk = (vhov and 8 or 6) * s
        ass[#ass + 1] = rrect(vx1 + (vx2 - vx1) * vr - vk, row1 - vk, vk * 2, vk * 2, vk, WHITE, 1, fb)
        if vhov then
            ass[#ass + 1] = rrect(vx1 + (vx2 - vx1) * vr - 22 * s, row1 - 40 * s, 44 * s, 22 * s, 11 * s, PANEL, 0.95, fb)
            ass[#ass + 1] = text(vx1 + (vx2 - vx1) * vr, row1 - 29 * s, 5, string.format('%d%%', math.floor(vol + 0.5)), 11 * s, WHITE, 1, fb)
        end
        regions[#regions + 1] = { x1 = vx1 - 8 * s, y1 = row1 - 14 * s, x2 = vx2 + 8 * s, y2 = row1 + 14 * s, id = 'volbar',
            vx1 = vx1, vx2 = vx2, vmax = vmax, wheel = vol_wheel }
        end

        -- Centre : -10 s / lecture / +10 s
        local cx = compact and (px + 82 * s) or (px + pw / 2)
        local paused = mp.get_property_native('pause')
        if not compact then
            button(cx - 62 * s, 'replay_10', 25 * s, 'back', function() mp.commandv('seek', '-10') end)
            button(cx + 62 * s, 'forward_10', 25 * s, 'fwd', function() mp.commandv('seek', '10') end)
        end
        local pr = { x1 = cx - 24 * s, y1 = row1 - 24 * s, x2 = cx + 24 * s, y2 = row1 + 24 * s, id = 'play',
                     click = function() mp.commandv('cycle', 'pause') end }
        local phov = inside(pr, mouse.x, mouse.y)
        ass[#ass + 1] = rrect(pr.x1, pr.y1, 48 * s, 48 * s, 24 * s, phov and 'FFFFFF' or ACCENT, 1, fb)
        ass[#ass + 1] = icon(cx, row1, paused and 'play_arrow' or 'pause', 30 * s, phov and '141414' or WHITE, 1, fb)
        regions[#regions + 1] = pr

        -- Droite : sous-titres / audio / plein écran
        local rx = px + pw - 34 * s
        local fs = mp.get_property_native('fullscreen')
        button(rx, fs and 'fullscreen_exit' or 'fullscreen', 25 * s, 'fs',
            function() mp.commandv('cycle', 'fullscreen') end)
        button(rx - 44 * s, 'audiotrack', 22 * s, 'audio',
            function() if menu and menu.kind == 'audio' then menu = nil else open_menu('audio', rx - 44 * s) end end,
            nil, menu and menu.kind == 'audio')
        local sid = mp.get_property('sid')
        button(rx - 88 * s, (sid == 'no' or not sid) and 'subtitles_off' or 'subtitles', 23 * s, 'subs',
            function() if menu and menu.kind == 'sub' then menu = nil else open_menu('sub', rx - 88 * s) end end,
            nil, menu and menu.kind == 'sub')

        if PRO then
            -- séparateur discret
            ass[#ass + 1] = rrect(rx - 118 * s, row1 - 12 * s, 1.5 * s, 24 * s, 0.75 * s, WHITE, 0.15, fb)
            button(rx - 148 * s, 'photo_camera', 22 * s, 'shot', function() capture() end)
            button(rx - 192 * s, 'info', 22 * s, 'info', function() info_on = not info_on end, nil, info_on)
            button(rx - 236 * s, 'crop_free', 22 * s, 'guides', function() cycle_guides() end, nil, guide_mode > 0)
            button(rx - 280 * s, 'gradient', 22 * s, 'lut',
                function() if menu and menu.kind == 'lut' then menu = nil else open_lut_menu(rx - 280 * s) end end,
                nil, (menu and menu.kind == 'lut') or LUT.file ~= nil)
        end

        -- Ligne 2 : temps + timeline
        local pos = mp.get_property_number('time-pos', 0)
        local dur = mp.get_property_number('duration', 0)
        local tsize = 14 * s
        local tpad = PRO and 120 or 92
        ass[#ass + 1] = text(px + 24 * s, row2, 4, fmt_main(pos, true), tsize, WHITE, 0.9, fb)
        ass[#ass + 1] = text(px + pw - 24 * s, row2, 6, PRO and fmt_main(dur, true) or ('-' .. fmt_time(dur - pos)), tsize, WHITE, 0.55, fb)

        local bx1, bx2 = px + tpad * s, px + pw - tpad * s
        local bar = { x1 = bx1, y1 = row2 - 12 * s, x2 = bx2, y2 = row2 + 12 * s, id = 'bar' }
        local hover_bar = inside(bar, mouse.x, mouse.y) or dragging == 'bar'
        local bh = (hover_bar and 6 or 4) * s
        local ratio = dur > 0 and math.max(0, math.min(1, pos / dur)) or 0
        ass[#ass + 1] = rrect(bx1, row2 - bh / 2, bx2 - bx1, bh, bh / 2, WHITE, 0.18, fb)
        -- zone déjà en mémoire tampon
        local cache = mp.get_property_native('demuxer-cache-state')
        if cache and cache['seekable-ranges'] and dur > 0 then
            for _, rg in ipairs(cache['seekable-ranges']) do
                local a1 = math.max(0, math.min(1, rg['start'] / dur))
                local a2 = math.max(0, math.min(1, rg['end'] / dur))
                if a2 > a1 then
                    ass[#ass + 1] = rrect(bx1 + (bx2 - bx1) * a1, row2 - bh / 2, (bx2 - bx1) * (a2 - a1), bh, bh / 2, WHITE, 0.16, fb)
                end
            end
        end
        if ratio > 0 then
            ass[#ass + 1] = rrect(bx1, row2 - bh / 2, (bx2 - bx1) * ratio, bh, bh / 2, ACCENT, 1, fb)
        end
        if PRO and dur > 0 then
            local a = tonumber(mp.get_property('ab-loop-a'))
            local b = tonumber(mp.get_property('ab-loop-b'))
            local function mark(t) return bx1 + (bx2 - bx1) * math.max(0, math.min(1, t / dur)) end
            if a and b then
                ass[#ass + 1] = rrect(mark(a), row2 - 9 * s, mark(b) - mark(a), 18 * s, 4 * s, ACCENT, 0.25, fb)
            end
            if a then ass[#ass + 1] = rrect(mark(a) - 1.5 * s, row2 - 11 * s, 3 * s, 22 * s, 1.5 * s, WHITE, 1, fb) end
            if b then ass[#ass + 1] = rrect(mark(b) - 1.5 * s, row2 - 11 * s, 3 * s, 22 * s, 1.5 * s, WHITE, 1, fb) end
        end
        local kx = bx1 + (bx2 - bx1) * ratio
        local kr = (hover_bar and 8 or 6) * s
        ass[#ass + 1] = rrect(kx - kr, row2 - kr, kr * 2, kr * 2, kr, WHITE, 1, fb)
        if hover_bar and dur > 0 and mouse.x >= bx1 and mouse.x <= bx2 then
            local hx = math.max(bx1, math.min(bx2, mouse.x))
            local ht = (hx - bx1) / (bx2 - bx1) * dur
            local bw2 = (PRO and 52 or 36) * s
            ass[#ass + 1] = rrect(hx - bw2, row2 - 44 * s, bw2 * 2, 26 * s, 13 * s, BORDER, 1, fb)
            ass[#ass + 1] = rrect(hx - bw2 + 1 * s, row2 - 43 * s, bw2 * 2 - 2 * s, 24 * s, 12 * s, PANEL, 1, fb)
            ass[#ass + 1] = text(hx, row2 - 31 * s, 5, fmt_main(ht, true), 13 * s, WHITE, 1, fb)
        end
        regions[#regions + 1] = bar

        -- PRO : panneau de la LUT active (intensité, avant/après, mémoire, captures)
        if PRO and LUT.file and not menu then
            local lh = 48 * s
            local lw = math.min(620 * s, pw)
            local lx, ly = px, py - lh - 10 * s
            ass[#ass + 1] = rrect(lx - 1 * s, ly - 1 * s, lw + 2 * s, lh + 2 * s, 24 * s, BORDER, 0.9, fb)
            ass[#ass + 1] = rrect(lx, ly, lw, lh, 23 * s, PANEL, 0.88, fb)
            local cy = ly + lh / 2
            ass[#ass + 1] = icon(lx + 26 * s, cy, 'gradient', 20 * s, ACCENT, 1, fb)
            local name = lut_name()
            if #name > 16 then name = name:sub(1, 15) .. '…' end
            ass[#ass + 1] = text(lx + 46 * s, cy, 4, name, 13 * s, WHITE, 0.95, fb)
            -- intensité
            local kx1, kx2 = lx + 190 * s, lx + 310 * s
            ass[#ass + 1] = rrect(kx1, cy - 2 * s, kx2 - kx1, 4 * s, 2 * s, WHITE, 0.22, fb)
            ass[#ass + 1] = rrect(kx1, cy - 2 * s, (kx2 - kx1) * LUT.k, 4 * s, 2 * s, ACCENT, 1, fb)
            local khov = dragging == 'lutk' or (mouse.x >= kx1 - 8 * s and mouse.x <= kx2 + 8 * s and math.abs(mouse.y - cy) <= 14 * s)
            local kk = (khov and 8 or 6) * s
            ass[#ass + 1] = rrect(kx1 + (kx2 - kx1) * LUT.k - kk, cy - kk, kk * 2, kk * 2, kk, WHITE, 1, fb)
            ass[#ass + 1] = text(kx2 + 12 * s, cy, 4, string.format('%d %%', math.floor(LUT.k * 100 + 0.5)), 12 * s, WHITE, 0.8, fb)
            regions[#regions + 1] = { x1 = kx1 - 8 * s, y1 = cy - 14 * s, x2 = kx2 + 8 * s, y2 = cy + 14 * s, id = 'lutk',
                                      kx1 = kx1, kx2 = kx2, wheel = function(dir)
                                          LUT.k = math.max(0, math.min(1, LUT.k + dir * 0.05)); lut_live(true) end }
            -- boutons
            local function lbtn(x, name, id, fn, on)
                local r = { x1 = x - 17 * s, y1 = cy - 17 * s, x2 = x + 17 * s, y2 = cy + 17 * s, id = id, click = fn }
                if inside(r, mouse.x, mouse.y) then ass[#ass + 1] = rrect(r.x1, r.y1, 34 * s, 34 * s, 17 * s, WHITE, 0.12, fb) end
                ass[#ass + 1] = icon(x, cy, name, 20 * s, on and ACCENT or WHITE, 1, fb)
                regions[#regions + 1] = r
            end
            local bx = lx + lw - 26 * s
            lbtn(bx, 'close', 'lutoff', function() set_lut(nil) end)
            lbtn(bx - 40 * s, 'photo_filter', 'lutcap', toggle_capture_lut, LUT.with_capture)
            lbtn(bx - 80 * s, remembered() and 'bookmark' or 'bookmark_border', 'lutmem', toggle_remember, remembered())
            lbtn(bx - 120 * s, 'compare', 'lutsplit', toggle_split, LUT.split)
            regions[#regions + 1] = { x1 = lx, y1 = ly, x2 = lx + lw, y2 = ly + lh, id = 'lutbg' }
        end

        -- Menu des pistes (carte KSHFLIX)
        if menu then
            local ih = 38 * s
            local mw = math.min(400 * s, W - 32 * s)
            local mh = #menu.items * ih + 52 * s
            local mx = math.max(16 * s, math.min(menu.anchor_x - mw / 2, W - mw - 16 * s))
            local my = py - mh - 12 * s
            ass[#ass + 1] = rrect(mx - 1 * s, my - 1 * s, mw + 2 * s, mh + 2 * s, 19 * s, BORDER, 0.9, fb)
            ass[#ass + 1] = rrect(mx, my, mw, mh, 18 * s, PANEL, 0.95, fb)
            ass[#ass + 1] = text(mx + 20 * s, my + 24 * s, 4, menu.title, 11 * s, ACCENT, 1, fb)
            for i, it in ipairs(menu.items) do
                local iy = my + 44 * s + (i - 1) * ih
                local r = { x1 = mx + 6 * s, y1 = iy, x2 = mx + mw - 6 * s, y2 = iy + ih, id = 'menu',
                            click = function()
                                if it.act then it.act() else mp.set_property(menu.prop, tostring(it.id)) end
                                menu = nil
                            end }
                if it.selected or inside(r, mouse.x, mouse.y) then
                    ass[#ass + 1] = rrect(r.x1, r.y1, r.x2 - r.x1, ih, 12 * s, WHITE, it.selected and 0.1 or 0.06, fb)
                end
                if it.selected then
                    ass[#ass + 1] = rrect(mx + 20 * s, iy + ih / 2 - 4 * s, 8 * s, 8 * s, 4 * s, ACCENT, 1, fb)
                end
                local label = it.label
                local mc = math.floor((mw - 60 * s) / (7.4 * s))
                if #label > mc then label = label:sub(1, mc - 1) .. '…' end
                ass[#ass + 1] = text(mx + 40 * s, iy + ih / 2, 4, label, 14 * s, WHITE, it.selected and 1 or (it.dim and 0.5 or 0.8), fb)
                table.insert(regions, 1, r)
            end
            table.insert(regions, #menu.items + 1, { x1 = mx, y1 = my, x2 = mx + mw, y2 = my + mh, id = 'menubg' })
        end
    end

    -- === En haut à gauche : titre en pastille KSHFLIX (sur Mac : après les boutons rouge/orange/vert) ===
    if ft > 0.01 then
        local ox = MAC and 76 * s or 0
        local title = mp.get_property('media-title', '')
        local maxc = math.floor((W * 0.6) / (8 * s))
        if #title > maxc then title = title:sub(1, maxc - 1) .. '…' end
        local tw = math.min(W * 0.62, #title * 7.6 * s + 56 * s)
        ass[#ass + 1] = rrect(ox + 15 * s, 15 * s, tw + 2 * s, 46 * s, 23 * s, BORDER, 0.9, ft)
        ass[#ass + 1] = rrect(ox + 16 * s, 16 * s, tw, 44 * s, 22 * s, PANEL, 0.86, ft)
        ass[#ass + 1] = rrect(ox + 34 * s, 34 * s, 8 * s, 8 * s, 4 * s, ACCENT, 1, ft)
        ass[#ass + 1] = text(ox + 52 * s, 38 * s, 4, title, 14 * s, WHITE, 0.95, ft)
        if PRO then
            ass[#ass + 1] = rrect(ox + 16 * s + tw + 8 * s, 24 * s, 46 * s, 28 * s, 9 * s, ACCENT, 1, ft)
            ass[#ass + 1] = text(ox + 16 * s + tw + 31 * s, 38 * s, 5, 'PRO', 12 * s, WHITE, 1, ft, BRAND_FONT)
        end
    end

    -- === Petite pilule en haut à droite : réduire / plein écran / fermer (Windows ; le Mac a ses boutons natifs) ===
    if ft > 0.01 and not MAC then
        local bw = 40 * s
        local tw = bw * 3 + 12 * s
        local tx = W - tw - 16 * s
        local ty = 16 * s
        ass[#ass + 1] = rrect(tx - 1 * s, ty - 1 * s, tw + 2 * s, 46 * s, 23 * s, BORDER, 0.9, ft)
        ass[#ass + 1] = rrect(tx, ty, tw, 44 * s, 22 * s, PANEL, 0.86, ft)
        local function tbtn(i, name, id, fn, hover_color)
            local cx = tx + 6 * s + bw * (i - 0.5)
            local cy = ty + 22 * s
            local r = { x1 = cx - bw / 2, y1 = ty, x2 = cx + bw / 2, y2 = ty + 44 * s, id = id, click = fn }
            local hov = inside(r, mouse.x, mouse.y)
            if hov then
                ass[#ass + 1] = rrect(cx - 16 * s, cy - 16 * s, 32 * s, 32 * s, 16 * s, hover_color or WHITE, hover_color and 0.9 or 0.14, ft)
            end
            ass[#ass + 1] = icon(cx, cy, name, 20 * s, WHITE, 1, ft)
            regions[#regions + 1] = r
        end
        local fs = mp.get_property_native('fullscreen')
        tbtn(1, 'remove', 'min', function() mp.commandv('cycle', 'window-minimized') end)
        tbtn(2, fs and 'fullscreen_exit' or 'fullscreen', 'tfs', function() mp.commandv('cycle', 'fullscreen') end)
        tbtn(3, 'close', 'close', function()
            -- la croix ferme toute l'appli (la fin du film, elle, ramène à l'accueil)
            mp.set_property('user-data/kshosc/exit-app', 'yes')
            mp.add_timeout(0.08, function() mp.commandv('quit') end)
        end, ACCENT)
        regions[#regions + 1] = { x1 = tx, y1 = ty, x2 = tx + tw, y2 = ty + 44 * s, id = 'topbg' }
    end

    -- zone de la pilule du bas (pour capter les clics dessus)
    if fb > 0.01 then
        regions[#regions + 1] = { x1 = px, y1 = py, x2 = px + pw, y2 = py + ph, id = 'panel' }
    end

    overlay.res_x, overlay.res_y = W, H
    overlay.data = table.concat(ass, '\n')
    overlay:update()
    cleared = false
end

-- ---------- Souris ----------
local function region_at(x, y)
    for _, r in ipairs(regions) do
        if inside(r, x, y) then return r end
    end
    return nil
end

local function seek_to_mouse(final)
    for _, r in ipairs(regions) do
        if r.id == 'bar' then
            local ratio = (mouse.x - r.x1) / (r.x2 - r.x1)
            ratio = math.max(0, math.min(1, ratio))
            mp.commandv('seek', tostring(ratio * 100), final and 'absolute-percent+exact' or 'absolute-percent+keyframes')
            return
        end
    end
end

local function volume_to_mouse()
    for _, r in ipairs(regions) do
        if r.id == 'volbar' then
            local ratio = math.max(0, math.min(1, (mouse.x - r.vx1) / (r.vx2 - r.vx1)))
            mp.set_property_number('volume', math.floor(ratio * r.vmax + 0.5))
            mp.set_property_native('mute', false)
            return
        end
    end
end

local function split_to_mouse()
    for _, r in ipairs(regions) do
        if r.id == 'split' then
            LUT.pos = math.max(0.02, math.min(0.98, (mouse.x - r.vx) / r.vw))
            lut_live(false)
            return
        end
    end
end

local function lutk_to_mouse(final)
    for _, r in ipairs(regions) do
        if r.id == 'lutk' then
            LUT.k = math.max(0, math.min(1, (mouse.x - r.kx1) / (r.kx2 - r.kx1)))
            if final then apply_lut() else lut_live(false) end
            return
        end
    end
end

local function on_click(e)
    if e.event == 'down' then
        local r = region_at(mouse.x, mouse.y)
        if menu and (not r or (r.id ~= 'menu' and r.id ~= 'menubg' and r.id ~= 'subs' and r.id ~= 'audio' and r.id ~= 'lut')) then
            menu = nil
            return
        end
        if not r then return end
        if r.id == 'bar' then
            dragging = 'bar'
            seek_to_mouse(false)
        elseif r.id == 'volbar' then
            dragging = 'vol'
            volume_to_mouse()
        elseif r.id == 'split' then
            dragging = 'split'
            split_to_mouse()
        elseif r.id == 'lutk' then
            dragging = 'lutk'
            lutk_to_mouse(false)
        elseif r.click then
            r.click()
        end
    elseif e.event == 'up' then
        if dragging == 'bar' then seek_to_mouse(true) end
        if dragging == 'split' then lut_live(true) end
        if dragging == 'lutk' then lutk_to_mouse(true) end
        dragging = false
    end
end

local function on_wheel(dir)
    local r = region_at(mouse.x, mouse.y)
    if r and r.wheel then r.wheel(dir)
    elseif PRO then mp.commandv(dir > 0 and 'frame-step' or 'frame-back-step')   -- jog
    elseif r and (r.id == 'bar' or r.id == 'panel') then mp.commandv('seek', tostring(dir * 5)) end
end

-- Sections de raccourcis "force" : quand elles sont actives (sans le drapeau allow-vo-dragging),
-- mpv ne déplace plus la fenêtre et nos boutons reçoivent bien les clics.
local SECTIONS = { 'ksh_mbtn', 'ksh_dbl', 'ksh_wheel' }

local function refresh_mouse()
    local m = mp.get_property_native('mouse-pos')
    if m then mouse.x, mouse.y, mouse.hover = m.x, m.y, m.hover end
end

mp.set_key_bindings({
    { 'mbtn_left',
      function() on_click({ event = 'up' }) end,
      function() refresh_mouse(); on_click({ event = 'down' }) end },
}, 'ksh_mbtn', 'force')
mp.set_key_bindings({ { 'mbtn_left_dbl', 'ignore' } }, 'ksh_dbl', 'force')
mp.set_key_bindings({
    { 'wheel_up', function() on_wheel(1) end },
    { 'wheel_down', function() on_wheel(-1) end },
}, 'ksh_wheel', 'force')

local function set_bindings(on)
    if on == bindings_on then return end
    bindings_on = on
    for _, sec in ipairs(SECTIONS) do
        if on then mp.enable_key_bindings(sec, '')
        elseif not (PRO and sec == 'ksh_wheel') then mp.disable_key_bindings(sec) end
    end
end

mp.observe_property('mouse-pos', 'native', function(_, m)
    if not m then return end
    local moved = (m.x ~= mouse.x or m.y ~= mouse.y)
    mouse.x, mouse.y, mouse.hover = m.x, m.y, m.hover
    if moved then last_move = mp.get_time() end
    if dragging == 'bar' then seek_to_mouse(false)
    elseif dragging == 'vol' then volume_to_mouse()
    elseif dragging == 'split' then split_to_mouse()
    elseif dragging == 'lutk' then lutk_to_mouse(false) end
end)

mp.observe_property('osd-dimensions', 'native', function(_, d)
    if d then W, H = d.w, d.h end
end)

-- Affiche brièvement la barre lors d'un saut ou d'une pause au clavier
mp.register_event('seek', function() flash_until = mp.get_time() + 1.2 end)
mp.observe_property('pause', 'bool', function() flash_until = mp.get_time() + 1.2 end)

-- ---------- Boucle d'animation ----------
local last_tick = mp.get_time()
mp.add_periodic_timer(1 / 30, function()
    local now = mp.get_time()
    local dt = now - last_tick
    last_tick = now
    if W <= 0 or H <= 0 then return end

    local s = layout()
    if MAC and mp.get_property('user-data/kshosc/leave') == 'yes' then mouse.hover = false end
    local recent = (now - last_move) < HIDE_DELAY and mouse.hover
    local over = region_at(mouse.x, mouse.y) ~= nil and mouse.hover
    local near_bottom = mouse.y >= H - (104 + 26 + 120) * s
    local near_top = mouse.y <= 90 * s

    local want_bottom = dragging or menu ~= nil or over or now < flash_until
        or mp.get_property_native('pause') or (recent and near_bottom)
    local want_top = (recent and near_top) or (over and near_top)

    local function step(cur, want)
        local target = want and 1 or 0
        if cur < target then return math.min(target, cur + dt * FADE_SPEED) end
        return math.max(target, cur - dt * FADE_SPEED)
    end
    fade.bottom = step(fade.bottom, want_bottom)
    fade.top = step(fade.top, want_top)
    if fade.bottom == 0 then menu = nil end

    local persistent = PRO and (info_on or guide_mode > 0 or now < guide_flash or (LUT.split and LUT.file ~= nil))
    if fade.bottom > 0 or fade.top > 0 or persistent then
        render()
        local captured = region_at(mouse.x, mouse.y) ~= nil or dragging or menu ~= nil
        set_bindings(captured)
        if MAC then mp.set_property('user-data/kshosc/over', captured and 'yes' or 'no') end
    elseif not cleared then
        regions = {}
        overlay.data = ''
        overlay:update()
        cleared = true
        set_bindings(false)
        if MAC then mp.set_property('user-data/kshosc/over', 'no') end
    end
end)

-- ---------- Raccourcis PRO ----------
if PRO then
    local function k(key, name, fn, flags) mp.add_forced_key_binding(key, name, fn, flags) end
    k('RIGHT', 'ksh_fstep', function() mp.commandv('frame-step') end, { repeatable = true })
    k('LEFT', 'ksh_fback', function() mp.commandv('frame-back-step') end, { repeatable = true })
    k('Shift+RIGHT', 'ksh_s1f', function() mp.commandv('seek', '1', 'exact') end, { repeatable = true })
    k('Shift+LEFT', 'ksh_s1b', function() mp.commandv('seek', '-1', 'exact') end, { repeatable = true })
    k('HOME', 'ksh_home', function() mp.commandv('seek', '0', 'absolute+exact') end)
    k('END', 'ksh_end', function() mp.commandv('seek', '100', 'absolute-percent+exact') end)
    k('j', 'ksh_j', key_j)
    k('k', 'ksh_k', key_k)
    k('l', 'ksh_l', key_l)
    k('i', 'ksh_in', set_in)
    k('o', 'ksh_out', set_out)
    k('Alt+x', 'ksh_clear', clear_io)
    k('Ctrl+i', 'ksh_info', function() info_on = not info_on end)
    k('g', 'ksh_guides', cycle_guides)
    k('s', 'ksh_shot', capture)
    k('t', 'ksh_lut', function() open_lut_menu(W / 2); flash_until = mp.get_time() + 3 end)
    k('Shift+t', 'ksh_lut_split', toggle_split)
    k('Alt+t', 'ksh_lut_off', function() set_lut(nil) end)
    if opts.shotdir ~= '' then mp.set_property('screenshot-directory', opts.shotdir) end
    mp.set_property('screenshot-format', 'png')
    mp.set_property('screenshot-template', '%F - %wH-%wM-%wS-%wT')
    -- les clics/molette sur l'image servent au jog : on garde la molette active en permanence
    mp.enable_key_bindings('ksh_wheel', '')
end
