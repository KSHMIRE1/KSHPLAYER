-- KSHPLAYER · barre de contrôle maison ("pilule flottante")
-- Apparaît quand la souris approche du bas de l'image, disparaît toute seule.
-- Icônes : Material Icons Round (Google, Apache 2.0).

local mp = require 'mp'
local utils = require 'mp.utils'

-- Options passées par l'appli (édition PRO, dossiers LUT / captures)
--   apercu = fenêtre d'aperçu ouverte par la barre Espace (KSHPLAYER-Apercu.exe, Windows)
--   appexe = chemin de KSHPLAYER.exe, pour « ouvrir en grand » depuis l'aperçu
local opts = { pro = false, lutdir = '', shotdir = '', platform = 'windows', font = '', apercu = false, appexe = '',
               introexe = '', introdb = '' }
require('mp.options').read_options(opts, 'kshosc')
local PRO = opts.pro
local MAC = opts.platform == 'mac'
local APERCU = opts.apercu

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
local late = {}               -- infobulles : dessinées en dernier, par-dessus tout le reste
local toast = nil             -- message passager envoyé par l'appli (ex. mise à jour prête) : { text, t0, t1 }

-- Vidéo précédente / suivante (même dossier) et fiche « lecture impossible »
local nav = { dir = nil, kind = nil, list = nil, idx = nil, path = nil, prev = nil, next = nil, scanned = -1e9 }
local err = nil               -- { path, name, reason, detail, icon } quand un fichier ne se lit pas
local last_path = nil         -- dernier fichier demandé (mpv l'oublie quand le chargement échoue)
local played_something = false

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

-- ---------- Texte : UTF-8, mesure exacte, coupe au milieu, retour à la ligne ----------
local function utf8_chars(s)
    local t = {}
    for ch in tostring(s or ''):gmatch('[%z\1-\127\194-\244][\128-\191]*') do t[#t + 1] = ch end
    return t
end

-- largeur réelle d'un texte (mesurée par libass, mise en cache) ; estimation si mpv ne sait pas mesurer.
-- libass ajoute une petite marge fixe autour de ce qu'il mesure : on mesure « texte » et « textetexte »,
-- la différence donne la largeur exacte.
local meas = mp.create_osd_overlay('ass-events')
meas.hidden, meas.compute_bounds = true, true
local meas_cache, meas_count = {}, 0
local function bounds_w(s, size, font)
    meas.res_x, meas.res_y = W, H
    meas.data = string.format('{\\an7\\pos(0,0)\\fn%s\\fs%s\\bord0\\shad0\\blur0}%s', font, n(size), esc(s))
    local ok, r = pcall(function() return meas:update() end)
    if ok and type(r) == 'table' and r.x1 and r.x0 then return r.x1 - r.x0 end
    return nil
end
local function text_w(s, size, font)
    font = font or TEXT_FONT
    local key = font .. '|' .. n(size) .. '|' .. s
    local c = meas_cache[key]
    if c then return c end
    local w = nil
    if W > 0 and H > 0 and s ~= '' then
        local one, two = bounds_w(s, size, font), bounds_w(s .. s, size, font)
        if one and two and two > one then w = two - one end
    end
    w = w or (#utf8_chars(s) * size * 0.56)
    if meas_count > 600 then meas_cache, meas_count = {}, 0 end
    meas_cache[key], meas_count = w, meas_count + 1
    return w
end

-- « Un nom de fichier bien trop long pour la place.mkv » → « Un nom de fichier bi…la place.mkv »
local ell_cache = {}
local function ellipsize_middle(s, maxw, size, font)
    s = tostring(s or '')
    local key = s .. '|' .. n(maxw) .. '|' .. n(size) .. '|' .. (font or '')
    if ell_cache[key] then return ell_cache[key] end
    local out = s
    if text_w(s, size, font) > maxw then
        local ch = utf8_chars(s)
        out = '…'
        local lo, hi = 1, #ch - 1
        while lo <= hi do
            local k = math.floor((lo + hi) / 2)
            local a = math.ceil(k * 0.55)
            local b = k - a
            local cand = table.concat(ch, '', 1, a) .. '…' .. (b > 0 and table.concat(ch, '', #ch - b + 1, #ch) or '')
            if text_w(cand, size, font) <= maxw then out = cand; lo = k + 1 else hi = k - 1 end
        end
    end
    ell_cache[key] = out
    return out
end

-- coupe un texte en lignes qui tiennent dans maxw (au plus max_lines, la dernière finit par « … »)
local function wrap(s, maxw, size, max_lines)
    local lines, cur = {}, ''
    for word in tostring(s or ''):gmatch('%S+') do
        local cand = cur == '' and word or (cur .. ' ' .. word)
        if cur ~= '' and text_w(cand, size) > maxw then
            lines[#lines + 1] = cur
            cur = word
        else
            cur = cand
        end
    end
    if cur ~= '' then lines[#lines + 1] = cur end
    if max_lines and #lines > max_lines then
        local keep = {}
        for i = 1, max_lines do keep[i] = lines[i] end
        keep[max_lines] = ellipsize_middle(keep[max_lines] .. ' …', maxw, size)
        lines = keep
    end
    return lines
end

-- petite étiquette flottante (au-dessus d'un bouton), toujours entière dans la fenêtre
local function tooltip(x, y, label, s, f)
    local size = 12.5 * s
    label = ellipsize_middle(label, math.min(460 * s, W - 48 * s), size)
    local w = text_w(label, size) + 28 * s
    local h = 28 * s
    local tx = math.max(8 * s, math.min(x - w / 2, W - w - 8 * s))
    late[#late + 1] = rrect(tx - 1 * s, y - h / 2 - 1 * s, w + 2 * s, h + 2 * s, 14 * s, BORDER, 1, f)
    late[#late + 1] = rrect(tx, y - h / 2, w, h, 14 * s, PANEL, 1, f)
    late[#late + 1] = text(tx + w / 2, y, 5, label, size, WHITE, 0.95, f)
end

-- ---------- Vidéos du même dossier (← → d'Orbit Look, ici Pg préc. / Pg suiv.) ----------
local function set_of(list) local t = {} for _, e in ipairs(list) do t[e] = true end return t end
local VIDEO_EXT = set_of({ 'mkv', 'mk3d', 'mp4', 'm4v', 'mov', 'qt', 'avi', 'divx', 'xvid', 'wmv', 'asf', 'webm', 'ts', 'm2ts',
    'mts', 'm2t', 'trp', 'tp', 'mpg', 'mpeg', 'mpe', 'm2v', 'm1v', 'vob', 'evo', 'flv', 'f4v', 'ogv', 'ogm', '3gp', '3g2', 'rm',
    'rmvb', 'mxf', 'gxf', 'y4m', 'dv', 'nut', 'hevc', 'h264', 'h265', '264', '265', 'avc', 'ivf', 'wtv', 'dvr-ms', 'amv', 'mjpeg',
    'mjpg', 'bik', 'braw', 'r3d', 'ari', 'arx', 'crm', 'nev' })
local AUDIO_EXT = set_of({ 'mp3', 'flac', 'wav', 'm4a', 'm4b', 'aac', 'ogg', 'oga', 'opus', 'wma', 'alac', 'ape', 'aiff', 'aif',
    'aifc', 'dts', 'dtshd', 'ac3', 'eac3', 'ec3', 'mka', 'thd', 'truehd', 'mlp', 'wv', 'dsf', 'dff', 'mpc', 'tta', 'caf', 'w64',
    'spx', 'amr', 'au' })

local function ext_of(name) return ((tostring(name or '')):match('%.([^%.\\/]+)$') or ''):lower() end
local function strip_ext(name) return (tostring(name or ''):gsub('%.[^%.\\/]+$', '')) end
local function base_name(p) return select(2, utils.split_path(p or '')) or '' end
local function kind_of(name)
    local e = ext_of(name)
    if VIDEO_EXT[e] then return 'video' elseif AUDIO_EXT[e] then return 'audio' end
    return nil
end

-- fichier local (pas un flux, ni une séquence mf://), en chemin complet
local function local_path(p)
    if not p or p == '' or p:find('^%a[%w+.-]+://') then return nil end
    return utils.join_path(mp.get_property('working-directory', ''), p)
end

-- tri « comme l'Explorateur » : Episode 2 avant Episode 10
local function nat_key(s)
    local key = {}
    for txt, num in s:lower():gmatch('(%D*)(%d*)') do
        if txt ~= '' then key[#key + 1] = txt end
        if num ~= '' then key[#key + 1] = tonumber(num) or num end
    end
    return key
end
local function nat_less(a, b)
    local ka, kb = a.key, b.key
    for i = 1, math.max(#ka, #kb) do
        local x, y = ka[i], kb[i]
        if x == nil then return true end
        if y == nil then return false end
        if x ~= y then
            local tx, ty = type(x), type(y)
            if tx == ty then return x < y end
            return tx == 'number'
        end
    end
    return a.name < b.name
end

local function scan_neighbors(force)
    -- last_path : après un échec de chargement, mpv n'a plus de « path »
    local p = local_path(last_path or mp.get_property('path', ''))
    if not p then nav.list, nav.prev, nav.next, nav.path = nil, nil, nil, nil; return end
    local dir, name = utils.split_path(p)
    local kind = kind_of(name) or 'video'
    if force or nav.dir ~= dir or nav.kind ~= kind or not nav.list or mp.get_time() - nav.scanned > 30 then
        local list = {}
        for _, f in ipairs(utils.readdir(dir, 'files') or {}) do
            -- « ._fichier » : métadonnées laissées par macOS sur les disques externes
            if f:sub(1, 2) ~= '._' and kind_of(f) == kind then list[#list + 1] = { name = f, key = nat_key(f) } end
        end
        table.sort(list, nat_less)
        nav.dir, nav.kind, nav.list, nav.scanned = dir, kind, list, mp.get_time()
    end
    nav.path, nav.idx = p, nil
    local lname = name:lower()
    for i, it in ipairs(nav.list) do
        if it.name:lower() == lname then nav.idx = i; break end
    end
    local function at(i) return nav.list[i] and utils.join_path(nav.dir, nav.list[i].name) or nil end
    if nav.idx then
        nav.prev, nav.next = at(nav.idx - 1), at(nav.idx + 1)
    else
        -- fichier hors liste (extension inconnue) : on le place dans l'ordre du dossier
        local me = { name = name, key = nat_key(name) }
        nav.prev, nav.next = nil, nil
        for i, it in ipairs(nav.list) do
            if nat_less(it, me) then nav.prev = at(i) elseif not nav.next and nat_less(me, it) then nav.next = at(i) end
        end
    end
end

local function has_neighbors() return nav.list ~= nil and (nav.prev ~= nil or nav.next ~= nil) end

local function go_neighbor(dir)
    scan_neighbors(false)
    local target = dir < 0 and nav.prev or nav.next
    if not target then
        if has_neighbors() then mp.osd_message(dir < 0 and 'Première vidéo du dossier' or 'Dernière vidéo du dossier', 1.2) end
        return
    end
    mp.commandv('loadfile', target, 'replace')   -- le titre suit tout seul (voir « start-file »)
end

local function chapter_step(dir)
    if (mp.get_property_number('chapters', 0) or 0) > 0 then mp.commandv('add', 'chapter', tostring(dir))
    else mp.osd_message('Pas de chapitres dans cette vidéo', 1.2) end
end

-- ---------- Séries : « Passer l'intro » et « Épisode suivant » (façon Netflix) ----------
-- Où sont l'intro et le générique de fin ?
--   1. chapitres du fichier nommés « Intro », « Opening », « Générique », « Credits »… (Mac et Windows)
--   2. sinon (Windows) : KSHPLAYER.exe --intros compare le son de deux épisodes voisins (voir Intros.cs),
--      en arrière-plan, et range le résultat dans intros.json ; le suivant est préparé à l'avance.
local ep = { path = nil, intro = nil, credits = nil, src = nil, dismissed = false, asked = {}, skipped = false }

local function episode_like(name)
    local n = strip_ext(base_name(name or '')):lower()
    return n:find('s%d+[%s%._%-]*e%d+') or n:find('%f[%d]%d+x%d%d+%f[%D]') or n:find('%f[%w]ep?%.?%s?%d+%f[%D]')
        or n:find('[eé]pisode%s*%d') or n:find('%s%-%s%d%d+%f[%D]') or false
end

local function series_on()
    -- même règle pour l'intro et l'épisode suivant : une vraie série, rangée dans un dossier
    return not APERCU and ep.path ~= nil and episode_like(ep.path) and nav.next ~= nil and episode_like(nav.next)
end

local MARK_WORDS = { 'intro', 'opening', 'op', 'ed', 'ending', 'outro', 'credits', 'credit', 'crédits', 'générique',
                     'generique', 'ouverture', 'theme song', 'title sequence', 'end credits' }
local function chapter_marks()
    local list = mp.get_property_native('chapter-list') or {}
    local dur = mp.get_property_number('duration', 0) or 0
    local intro, credits = nil, nil
    for i, c in ipairs(list) do
        local t = ' ' .. (c.title or ''):lower():gsub('[%p]', ' ') .. ' '
        local hit = false
        for _, w in ipairs(MARK_WORDS) do
            if t:find(' ' .. w .. ' ', 1, true) then hit = true; break end
        end
        local a, b = c.time or 0, (list[i + 1] and list[i + 1].time) or dur
        if hit and b > a + 5 then
            if a < dur * 0.5 then
                if not intro and b - a < 240 then intro = { a, b } end
            else
                credits = credits or { a, b }
            end
        end
    end
    return intro, credits
end

local function db_key(p) return (p:gsub('/', '\\')):lower() end

local function from_db(p)
    if opts.introdb == '' then return nil end
    local f = io.open(opts.introdb, 'r')
    if not f then return nil end
    local data = utils.parse_json(f:read('*a') or '')
    f:close()
    local e = data and data.files and data.files[db_key(p)]
    local fi = utils.file_info(p)
    if not e or not fi or tonumber(e.size) ~= fi.size then return nil end
    return e
end

local function apply_db(e)
    if not e then return false end
    if type(e.intro) == 'table' and #e.intro == 2 then ep.intro = { e.intro[1], e.intro[2] } end
    if type(e.credits) == 'table' and #e.credits == 2 then ep.credits = { e.credits[1], e.credits[2] } end
    ep.src = 'analyse'
    return true
end

local function publish_ep()   -- pour le diagnostic (et les tests)
    mp.set_property_native('user-data/kshosc/episode', {
        intro = ep.intro, credits = ep.credits, src = ep.src or 'aucune', next = nav.next or '', series = series_on() and true or false })
end

local function analyse(p)
    if opts.introexe == '' or ep.asked[p] then return end
    ep.asked[p] = true
    local args = { opts.introexe, '--intros', p, nav.prev or '', nav.next or '' }
    mp.msg.info('analyse des génériques : ' .. base_name(p))
    mp.command_native_async({ name = 'subprocess', args = args, playback_only = false, capture_stdout = true,
                              capture_stderr = true }, function(_, r)
        if ep.path == p and not ep.intro and not ep.credits then
            apply_db(from_db(p))
            publish_ep()
        end
    end)
end

local function series_check()
    local p = local_path(last_path or mp.get_property('path', ''))
    if APERCU or not p then return end
    ep.path = p
    scan_neighbors(false)
    local ci, cc = chapter_marks()
    if ci or cc then
        ep.intro, ep.credits, ep.src = ci, cc, 'chapitres'
    elseif not apply_db(from_db(p)) and episode_like(p) and (nav.prev or nav.next) then
        analyse(p)
    end
    publish_ep()
end

local function next_episode()
    if not nav.next then return end
    ep.dismissed = true
    go_neighbor(1)
end

local function skip_intro()
    if not ep.intro then return end
    ep.skipped = true
    mp.commandv('seek', tostring(ep.intro[2]), 'absolute+exact')
end

-- Ce qui doit s'afficher maintenant : 'intro', 'next' (avec compte à rebours si le générique est connu) ou nil
local NEXT_COUNTDOWN = 10
local function series_state()
    if APERCU or err or not ep.path then return nil end
    local pos = mp.get_property_number('time-pos')
    local dur = mp.get_property_number('duration', 0) or 0
    if not pos then return nil end
    if ep.intro and not ep.skipped and pos >= ep.intro[1] and pos < ep.intro[2] - 1 then return 'intro' end
    if series_on() and dur >= 300 and not ep.dismissed then
        local from = (ep.credits and ep.credits[1] > dur * 0.6) and ep.credits[1] or (dur - 25)
        if pos >= from then return 'next', from, ep.credits ~= nil end
    end
    return nil
end

-- « Afficher dans le dossier » : l'Explorateur (ou le Finder) s'ouvre avec le fichier sélectionné
local function reveal(p)
    p = p and local_path(p)
    if not p then return end
    -- Windows : « /select, » et le chemin en deux morceaux, sinon un chemin avec des espaces ouvre le mauvais dossier
    local win = p:gsub('/', '\\')
    local args = MAC and { 'open', '-R', p } or { 'explorer.exe', '/select,', win }
    mp.command_native_async({ name = 'subprocess', playback_only = false, detach = true, args = args }, function() end)
end

-- ---------- Fiche « lecture impossible » : la raison en clair au lieu d'un retour muet à l'accueil ----------
local RAW_FORMATS = { braw = 'Blackmagic RAW', r3d = 'RED RAW', ari = 'ARRIRAW', arx = 'ARRIRAW',
                      crm = 'Canon Cinema RAW Light', nev = 'Nikon N-RAW' }
local PARTIAL_EXT = set_of({ 'part', 'crdownload', 'download', 'partial', '!qb', '!ut', 'opdownload', 'tmp' })
local NOT_MEDIA_EXT = set_of({ 'txt', 'nfo', 'srt', 'ass', 'ssa', 'sub', 'idx', 'vtt', 'pdf', 'doc', 'docx', 'xls', 'xlsx', 'zip',
    'rar', '7z', 'exe', 'msi', 'lnk', 'url', 'html', 'htm', 'json', 'xml', 'ini', 'torrent', 'iso', 'cube', 'lut', 'psd', 'aep',
    'prproj', 'drp', 'c4d', 'blend' })

local function explain_error(e)
    local p = last_path or mp.get_property('path', '') or ''
    local name = p:find('^mf://') and (mp.get_property('media-title', '') or p) or base_name(p)
    local ext = ext_of(name)
    local tech = tostring(e.file_error or e.error or '')
    if tech == 'nil' then tech = '' end
    local lp = local_path(p)
    local fi = lp and utils.file_info(lp) or nil
    local reason, icon = nil, 'videocam_off'
    if p == '' then
        reason = 'Ce fichier ne peut pas être lu.'
    elseif not lp then
        if p:find('^mf://') then
            reason = 'Les images de cette séquence ne peuvent pas être lues (format inconnu ou images abîmées).'
        else
            reason = 'Le flux ne répond pas, ou le lien n\'est plus valide.'; icon = 'link_off'
        end
    elseif not fi then
        reason = 'Fichier introuvable : il a été déplacé ou supprimé, ou le disque n\'est plus branché.'; icon = 'search_off'
    elseif fi.size == 0 then
        reason = 'Ce fichier est vide (0 octet) : la copie ou le téléchargement ne sont pas allés au bout.'
    elseif PARTIAL_EXT[ext] then
        reason = 'Téléchargement pas terminé : ce fichier est encore incomplet.'; icon = 'downloading'
    elseif RAW_FORMATS[ext] then
        reason = 'Format ' .. RAW_FORMATS[ext] .. ' (.' .. ext .. ') : pas encore pris en charge par KSHPLAYER.'
    elseif NOT_MEDIA_EXT[ext] then
        reason = 'Ce n\'est pas une vidéo : c\'est un fichier .' .. ext .. '.'; icon = 'insert_drive_file'
    elseif tech:find('unrecognized') or tech:find('recognize') then
        reason = 'Format non reconnu : le fichier est abîmé, ou ce n\'est pas une vidéo.'
    elseif tech:find('no audio or video') then
        reason = 'Aucune image ni aucun son lisible : le fichier est abîmé ou utilise un codec inconnu.'
    elseif tech:find('loading failed') then
        reason = 'Impossible d\'ouvrir le fichier : accès refusé, ou fichier abîmé.'
    else
        reason = 'Ce fichier ne peut pas être lu.'
    end
    return { path = lp, exists = fi ~= nil, name = name, reason = reason, icon = icon,
             detail = tech ~= '' and ('Détail technique : ' .. tech) or nil }
end

-- Fermer après une erreur (ou l'aperçu) : KSHPLAYER se ferme (Mac : retour à son écran), l'aperçu se referme
local function close_player() mp.commandv('quit') end

-- Aperçu : « ouvrir en grand » dans KSHPLAYER, au même endroit de la vidéo
local function open_full()
    local p = local_path(last_path or mp.get_property('path', ''))
    if not p or opts.appexe == '' then return end
    local pos = mp.get_property_number('time-pos', 0) or 0
    mp.command_native_async({ name = 'subprocess', playback_only = false, detach = true,
        args = { opts.appexe, p, string.format('--start=%.3f', pos) } }, function() end)
    mp.add_timeout(0.15, close_player)
end

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

-- Fiche « lecture impossible » (carte KSHFLIX au centre de la fenêtre)
local function render_error(ass, s)
    local e = err
    local cw = math.min(600 * s, W - 24 * s)
    local pad = (cw < 420 * s) and 20 * s or 30 * s
    local inner = cw - pad * 2
    local rsize = 14.5 * s
    local lines = wrap(e.reason, inner, rsize, 3)
    local detail = e.detail and ellipsize_middle(e.detail, inner, 11.5 * s) or nil
    local bh = 40 * s
    local ch = pad + 54 * s + 20 * s + #lines * 21 * s + (detail and 22 * s or 0) + 24 * s + bh + pad
    local x0 = (W - cw) / 2
    local y0 = math.max(8 * s, (H - ch) / 2)

    -- fond « salle obscure » (#050506) : pas de dernière image figée derrière la fiche
    ass[#ass + 1] = rrect(0, 0, W, H, 0, '060505', 1, 1)
    ass[#ass + 1] = rrect(x0 - 1 * s, y0 - 1 * s, cw + 2 * s, ch + 2 * s, 23 * s, BORDER, 0.95, 1)
    ass[#ass + 1] = rrect(x0, y0, cw, ch, 22 * s, PANEL, 0.96, 1)
    regions[#regions + 1] = { x1 = x0, y1 = y0, x2 = x0 + cw, y2 = y0 + ch, id = 'card' }

    local y = y0 + pad
    ass[#ass + 1] = rrect(x0 + pad, y, 54 * s, 54 * s, 27 * s, ACCENT, 0.18, 1)
    ass[#ass + 1] = icon(x0 + pad + 27 * s, y + 27 * s, e.icon or 'videocam_off', 28 * s, ACCENT, 1, 1)
    ass[#ass + 1] = text(x0 + pad + 70 * s, y + 15 * s, 4, 'LECTURE IMPOSSIBLE', 11 * s, ACCENT, 1, 1)
    ass[#ass + 1] = text(x0 + pad + 70 * s, y + 38 * s, 4, ellipsize_middle(e.name, inner - 70 * s, 17 * s), 17 * s, WHITE, 1, 1)
    y = y + 54 * s + 20 * s
    for _, l in ipairs(lines) do
        ass[#ass + 1] = text(x0 + pad, y + 10 * s, 4, l, rsize, WHITE, 0.85, 1)
        y = y + 21 * s
    end
    if detail then
        y = y + 6 * s
        ass[#ass + 1] = text(x0 + pad, y + 8 * s, 4, detail, 11.5 * s, WHITE, 0.45, 1)
        y = y + 16 * s
    end
    y = y + 24 * s

    -- boutons, alignés à droite : [Afficher dans le dossier] [⏮] [⏭] [Fermer]
    local roomy = cw >= 470 * s
    local function pbtn(xr, label, ic, id, fn, primary, tip)
        local size = 13.5 * s
        local tw = label ~= '' and text_w(label, size) or 0
        local w = (ic and 22 * s or 0) + tw + ((ic and label ~= '') and 8 * s or 0) + 32 * s
        local x = xr - w
        local r = { x1 = x, y1 = y, x2 = x + w, y2 = y + bh, id = id, click = fn }
        local hov = inside(r, mouse.x, mouse.y)
        if primary then
            ass[#ass + 1] = rrect(x, y, w, bh, bh / 2, hov and 'FFFFFF' or ACCENT, 1, 1)
        else
            ass[#ass + 1] = rrect(x - 1 * s, y - 1 * s, w + 2 * s, bh + 2 * s, bh / 2 + 1 * s, BORDER, 1, 1)
            ass[#ass + 1] = rrect(x, y, w, bh, bh / 2, '221B1B', 1, 1)
            if hov then ass[#ass + 1] = rrect(x, y, w, bh, bh / 2, WHITE, 0.10, 1) end
        end
        local fg = (primary and hov) and '141414' or WHITE
        local cx = x + 16 * s
        if ic then
            ass[#ass + 1] = icon(cx + 11 * s, y + bh / 2, ic, 21 * s, fg, 1, 1)
            cx = cx + 22 * s + (label ~= '' and 8 * s or 0)
        end
        if label ~= '' then ass[#ass + 1] = text(cx, y + bh / 2, 4, label, size, fg, 1, 1) end
        if hov and tip then tooltip(x + w / 2, y - 22 * s, tip, s, 1) end
        table.insert(regions, 1, r)   -- en tête de liste : prioritaire sur le fond de la carte
        return x - 10 * s
    end
    local xr = x0 + cw - pad
    xr = pbtn(xr, 'Fermer', nil, 'errclose', close_player, true)
    if nav.next then
        xr = pbtn(xr, '', 'skip_next', 'errnext', function() go_neighbor(1) end, false,
            'Suivante  ·  ' .. strip_ext(base_name(nav.next)))
    end
    if nav.prev then
        xr = pbtn(xr, '', 'skip_previous', 'errprev', function() go_neighbor(-1) end, false,
            'Précédente  ·  ' .. strip_ext(base_name(nav.prev)))
    end
    if e.exists and e.path then
        pbtn(xr, roomy and (MAC and 'Afficher dans le Finder' or 'Afficher dans le dossier') or '', 'folder_open', 'errshow',
            function() reveal(e.path) end, false, (not roomy) and 'Afficher dans le dossier' or nil)
    end
end

-- ---------- Mise en page adaptative (paysage, portrait, petites fenêtres) ----------
-- Taille de base = hauteur ; si la fenêtre est trop étroite (vidéo portrait), on réduit l'échelle
-- et on passe en barre « compacte » (sans curseur de volume ni ±10 s).
local function layout()
    -- échelle selon la hauteur : environ 25 % plus petite qu'en 1.2 (1080p ≈ 0,94 · 4K ≈ 1,3)
    local sh = math.max(0.7, math.min(1.3, H / 1150))
    local full = PRO and 1000 or 860
    local compact = W < full * sh
    local base = compact and (PRO and 600 or 460) or full
    local sc = math.min(sh, (W - 16) / base)
    return math.max(0.5, math.min(1.3, sc)), compact
end

-- ---------- Dessin ----------
local function render()
    regions = {}
    late = {}
    local ass = {}
    local s, compact = layout()
    local fb, ft = fade.bottom, fade.top
    if err then fb = 0 end        -- fichier illisible : la fiche remplace la barre du bas

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

    if err then render_error(ass, s) end

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

        -- vidéo précédente / suivante du dossier, avec son nom au survol
        local function nav_button(x, dir)
            local target = dir < 0 and nav.prev or nav.next
            local r = { x1 = x - 19 * s, y1 = row1 - 19 * s, x2 = x + 19 * s, y2 = row1 + 19 * s,
                        id = dir < 0 and 'prev' or 'next', click = function() go_neighbor(dir) end }
            local hov = inside(r, mouse.x, mouse.y)
            if hov and target then ass[#ass + 1] = rrect(r.x1, r.y1, 38 * s, 38 * s, 19 * s, WHITE, 0.12, fb) end
            ass[#ass + 1] = icon(x, row1, dir < 0 and 'skip_previous' or 'skip_next', 26 * s, WHITE, target and 1 or 0.3, fb)
            regions[#regions + 1] = r
            if hov then
                tooltip(x, py - 24 * s, target and ((dir < 0 and 'Précédent  ·  ' or 'Suivant  ·  ') .. strip_ext(base_name(target)))
                    or (dir < 0 and 'Pas de vidéo avant dans ce dossier' or 'Pas de vidéo après dans ce dossier'), s, fb)
            end
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

        -- Centre : ⏮ / -10 s / lecture / +10 s / ⏭  (⏮ ⏭ seulement s'il y a d'autres vidéos dans le dossier)
        local navs = has_neighbors() or not APERCU   -- toujours là : grisés s'il n'y a pas de voisine
        local cx = compact and (px + (navs and 124 or 82) * s) or (px + pw / 2)
        local paused = mp.get_property_native('pause')
        if not compact then
            button(cx - 62 * s, 'replay_10', 25 * s, 'back', function() mp.commandv('seek', '-10') end)
            button(cx + 62 * s, 'forward_10', 25 * s, 'fwd', function() mp.commandv('seek', '10') end)
        end
        if navs then
            local gap = compact and 46 or 118
            nav_button(cx - gap * s, -1)
            nav_button(cx + gap * s, 1)
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
        local bx = ox + 16 * s + tw + 8 * s
        if PRO then
            ass[#ass + 1] = rrect(bx, 24 * s, 46 * s, 28 * s, 9 * s, ACCENT, 1, ft)
            ass[#ass + 1] = text(bx + 23 * s, 38 * s, 5, 'PRO', 12 * s, WHITE, 1, ft, BRAND_FONT)
            bx = bx + 54 * s
        end
        if APERCU then
            -- rappel discret : on est dans l'aperçu (Espace referme, Entrée ouvre en grand)
            local lbl = 'APERÇU  ·  ESPACE POUR FERMER'
            local lw = text_w(lbl, 11 * s) + 24 * s
            if bx + lw < W - 160 * s then
                ass[#ass + 1] = rrect(bx - 1 * s, 23 * s, lw + 2 * s, 30 * s, 10 * s, BORDER, 0.9, ft)
                ass[#ass + 1] = rrect(bx, 24 * s, lw, 28 * s, 9 * s, PANEL, 0.86, ft)
                ass[#ass + 1] = text(bx + lw / 2, 38 * s, 5, lbl, 11 * s, WHITE, 0.8, ft)
            end
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
        local function tbtn(i, name, id, fn, hover_color, tip)
            local cx = tx + 6 * s + bw * (i - 0.5)
            local cy = ty + 22 * s
            local r = { x1 = cx - bw / 2, y1 = ty, x2 = cx + bw / 2, y2 = ty + 44 * s, id = id, click = fn }
            local hov = inside(r, mouse.x, mouse.y)
            if hov then
                ass[#ass + 1] = rrect(cx - 16 * s, cy - 16 * s, 32 * s, 32 * s, 16 * s, hover_color or WHITE, hover_color and 0.9 or 0.14, ft)
                if tip then tooltip(cx, ty + 66 * s, tip, s, ft) end
            end
            ass[#ass + 1] = icon(cx, cy, name, 20 * s, WHITE, 1, ft)
            regions[#regions + 1] = r
        end
        local fs = mp.get_property_native('fullscreen')
        if APERCU then
            tbtn(1, 'open_in_new', 'open', open_full, nil, 'Ouvrir dans ' .. (PRO and 'KSHPLAYER PRO' or 'KSHPLAYER') .. '  ·  Entrée')
        else
            tbtn(1, 'remove', 'min', function() mp.commandv('cycle', 'window-minimized') end)
        end
        tbtn(2, fs and 'fullscreen_exit' or 'fullscreen', 'tfs', function() mp.commandv('cycle', 'fullscreen') end)
        tbtn(3, 'close', 'close', function()
            if APERCU then close_player(); return end      -- l'aperçu se referme, rien d'autre
            -- la croix ferme KSHPLAYER (depuis 1.2 sur Windows, fermer la vidéo ferme l'appli dans tous les cas ;
            -- exit-app est gardé pour l'ancienne appli 1.1.x, qui revenait à son accueil en fin de film)
            mp.set_property('user-data/kshosc/exit-app', 'yes')
            mp.add_timeout(0.08, function() mp.commandv('quit') end)
        end, ACCENT)
        regions[#regions + 1] = { x1 = tx, y1 = ty, x2 = tx + tw, y2 = ty + 44 * s, id = 'topbg' }
    end

    -- zone de la pilule du bas (pour capter les clics dessus)
    if fb > 0.01 then
        regions[#regions + 1] = { x1 = px, y1 = py, x2 = px + pw, y2 = py + ph, id = 'panel' }
    end

    -- === Message passager (ex. « Mise à jour prête ») : centré sous la rangée du haut, jamais sur une barre ===
    if toast then
        local now = mp.get_time()
        if now >= toast.t1 then
            toast = nil
        else
            local tf = math.max(0, math.min(1, (now - toast.t0) * 4, (toast.t1 - now) * 2))   -- fondu entrée / sortie
            local size = 13 * s
            local maxw = math.min(W - 32 * s, 680 * s)
            local label = ellipsize_middle(toast.text, maxw - 58 * s, size)
            local tw = text_w(label, size) + 58 * s
            local tx, ty = (W - tw) / 2, 76 * s
            ass[#ass + 1] = rrect(tx - 1 * s, ty - 1 * s, tw + 2 * s, 42 * s, 21 * s, BORDER, 0.9, tf)
            ass[#ass + 1] = rrect(tx, ty, tw, 40 * s, 20 * s, PANEL, 0.92, tf)
            ass[#ass + 1] = rrect(tx + 20 * s, ty + 16 * s, 8 * s, 8 * s, 4 * s, ACCENT, 1, tf)
            ass[#ass + 1] = text(tx + 38 * s, ty + 20 * s, 4, label, size, WHITE, 0.95, tf)
        end
    end

    -- === Séries : « Passer l'intro » / « Épisode suivant », en bas à droite (au-dessus de la barre si elle est là) ===
    local st, from, counting = series_state()
    if st then
        local bh2 = 44 * s
        local by = (fb > 0.01) and (py - bh2 - 14 * s) or (H - bh2 - 40 * s)
        local xr = W - 32 * s
        local function sbtn(xr0, label, ic, id, fn, primary, fill)
            local size = 14 * s
            local w = 22 * s + 8 * s + text_w(label, size) + 40 * s
            local x = xr0 - w
            local r = { x1 = x, y1 = by, x2 = x + w, y2 = by + bh2, id = id, click = fn }
            local hov = inside(r, mouse.x, mouse.y)
            ass[#ass + 1] = rrect(x - 1 * s, by - 1 * s, w + 2 * s, bh2 + 2 * s, bh2 / 2 + 1 * s, primary and ACCENT or BORDER, 1, 1)
            ass[#ass + 1] = rrect(x, by, w, bh2, bh2 / 2, primary and '1B1BA0' or PANEL, primary and 1 or 0.92, 1)
            if primary then
                -- compte à rebours : le rouge KSHFLIX se remplit de gauche à droite
                local k = fill or 1
                if k > 0 then ass[#ass + 1] = rrect(x, by, math.max(bh2, w * k), bh2, bh2 / 2, hov and 'FFFFFF' or ACCENT, 1, 1) end
            elseif hov then
                ass[#ass + 1] = rrect(x, by, w, bh2, bh2 / 2, WHITE, 0.12, 1)
            end
            local fg = (primary and hov) and '141414' or WHITE
            ass[#ass + 1] = icon(x + 20 * s + 11 * s, by + bh2 / 2, ic, 22 * s, fg, 1, 1)
            ass[#ass + 1] = text(x + 20 * s + 30 * s, by + bh2 / 2, 4, label, size, fg, 1, 1)
            table.insert(regions, 1, r)
            return x - 10 * s
        end
        if st == 'intro' then
            sbtn(xr, 'Passer l\'intro', 'fast_forward', 'skipintro', skip_intro, false)
        else
            local pos = mp.get_property_number('time-pos', 0)
            local fill = counting and math.max(0, math.min(1, (pos - from) / NEXT_COUNTDOWN)) or 1
            local x2 = sbtn(xr, 'Épisode suivant', 'skip_next', 'nextep', next_episode, true, fill)
            if counting then
                sbtn(x2, 'Générique', 'subject', 'credits', function() ep.dismissed = true end, false)
            end
            -- nom de l'épisode suivant, discret, au-dessus
            local nm = ellipsize_middle(strip_ext(base_name(nav.next)), math.min(W * 0.5, 520 * s), 12 * s)
            ass[#ass + 1] = text(xr - 4 * s, by - 14 * s, 6, nm, 12 * s, WHITE, 0.75, 1)
        end
    end

    for _, a in ipairs(late) do ass[#ass + 1] = a end   -- infobulles par-dessus tout

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

-- Message passager envoyé par l'appli : script-message-to kshosc ksh-toast "<texte>" [secondes]
mp.register_script_message('ksh-toast', function(msg, secs)
    if not msg or msg == '' then return end
    local now = mp.get_time()
    toast = { text = msg, t0 = now, t1 = now + (tonumber(secs) or 6) }
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

    local want_bottom = not err and (dragging or menu ~= nil or over or now < flash_until
        or mp.get_property_native('pause') or (recent and near_bottom))
    local want_top = (recent and near_top) or (over and near_top)

    local function step(cur, want)
        local target = want and 1 or 0
        if cur < target then return math.min(target, cur + dt * FADE_SPEED) end
        return math.max(target, cur - dt * FADE_SPEED)
    end
    fade.bottom = step(fade.bottom, want_bottom)
    fade.top = step(fade.top, want_top)
    if fade.bottom == 0 then menu = nil end

    local sstate, sfrom, scount = series_state()
    if sstate == 'next' and scount and not mp.get_property_native('pause')
        and (mp.get_property_number('time-pos', 0) or 0) >= sfrom + NEXT_COUNTDOWN then
        next_episode()   -- générique connu : épisode suivant au bout de 10 s, comme Netflix
    end
    local persistent = err ~= nil or toast ~= nil or sstate ~= nil or
        (PRO and (info_on or guide_mode > 0 or now < guide_flash or (LUT.split and LUT.file ~= nil)))
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

-- ---------- Vidéo précédente / suivante, fiche d'erreur, fin de lecture ----------
-- Le titre affiché suit le fichier (aussi après ⏮ ⏭ ou un glisser-déposer sur la fenêtre)
mp.register_event('start-file', function()
    err = nil
    ep.path, ep.intro, ep.credits, ep.src, ep.dismissed, ep.skipped = nil, nil, nil, nil, false, false
    mp.remove_key_binding('ksh_err_esc')
    mp.remove_key_binding('ksh_err_enter')
    mp.remove_key_binding('ksh_err_kpenter')
    last_path = mp.get_property('path', last_path)
    local p = local_path(last_path)
    if p then
        mp.set_property('force-media-title', strip_ext(base_name(p)))
        -- même dossier : on recale tout de suite ⏮ ⏭ (sans relire le dossier)
        if nav.dir and utils.split_path(p) == nav.dir then scan_neighbors(false) end
    elseif not (last_path or ''):find('^mf://') then
        mp.set_property('force-media-title', '')     -- flux : son propre titre (une séquence garde le sien)
    end
end)

mp.register_event('file-loaded', function()
    played_something = true
    -- voisins cherchés juste après le démarrage, pour ne pas retarder l'image
    mp.add_timeout(0.3, function() scan_neighbors(false) end)
    -- séries : chapitres, résultats déjà connus, sinon analyse en arrière-plan (après le démarrage de l'image)
    mp.add_timeout(1.5, series_check)
end)

-- un retour avant le générique fait revenir « Épisode suivant » ; avant l'intro, « Passer l'intro »
mp.register_event('seek', function()
    local pos = mp.get_property_number('time-pos', 0) or 0
    local dur = mp.get_property_number('duration', 0) or 0
    if ep.intro and pos < ep.intro[1] then ep.skipped = false end
    local from = (ep.credits and ep.credits[1] > dur * 0.6) and ep.credits[1] or (dur - 25)
    if pos < from - 1 then ep.dismissed = false end
end)

-- PRO (keep-open) : la dernière image reste affichée, il n'y a pas de fin de fichier → on enchaîne quand même
mp.observe_property('eof-reached', 'bool', function(_, eof)
    if eof and series_on() and mp.get_property('keep-open', 'no') ~= 'no' then
        mp.add_timeout(0.3, function() go_neighbor(1) end)
    end
end)

local last_end = nil
mp.register_event('end-file', function(e)
    last_end = e.reason
    if e.reason ~= 'error' then return end
    err = explain_error(e)
    menu = nil
    mp.msg.warn('lecture impossible : ' .. tostring(err.name) .. ' (' .. tostring(e.file_error or e.error or '?') .. ')')
    scan_neighbors(false)
    mp.add_forced_key_binding('ESC', 'ksh_err_esc', close_player)
    mp.add_forced_key_binding('ENTER', 'ksh_err_enter', close_player)
    mp.add_forced_key_binding('KP_ENTER', 'ksh_err_kpenter', close_player)
    -- sans fenêtre vidéo (Mac sans force-window, carte graphique indisponible…), la fiche ne peut pas
    -- s'afficher : on rend la main tout de suite, comme avant
    mp.add_timeout(0.4, function()
        if err and not mp.get_property_native('vo-configured') then close_player() end
    end)
end)

-- Avant : idle=once (mpv se fermait tout seul en fin de liste, même après une erreur, sans rien dire).
-- Maintenant : mpv reste ouvert ; on ferme nous-mêmes en fin de lecture, sauf s'il faut montrer une erreur.
mp.set_property('idle', 'yes')
mp.observe_property('idle-active', 'bool', function(_, idle)
    if not idle then return end
    mp.add_timeout(0.15, function()
        if mp.get_property_native('idle-active') and played_something and not err then
            -- fin d'un épisode : on enchaîne sur le suivant au lieu de fermer
            if last_end == 'eof' and series_on() then go_neighbor(1) else close_player() end
        end
    end)
end)

mp.add_forced_key_binding('PGUP', 'ksh_prev', function() go_neighbor(-1) end)
mp.add_forced_key_binding('PGDWN', 'ksh_next', function() go_neighbor(1) end)
mp.add_forced_key_binding('PREV', 'ksh_prev_media', function() go_neighbor(-1) end)
mp.add_forced_key_binding('NEXT', 'ksh_next_media', function() go_neighbor(1) end)
mp.add_forced_key_binding('MBTN_BACK', 'ksh_prev_mouse', function() go_neighbor(-1) end)
mp.add_forced_key_binding('MBTN_FORWARD', 'ksh_next_mouse', function() go_neighbor(1) end)
if not APERCU then
    local function series_enter()
        local st = series_state()
        if st == 'intro' then skip_intro() elseif st == 'next' then next_episode() end
    end
    mp.add_forced_key_binding('ENTER', 'ksh_series_enter', series_enter)
    mp.add_forced_key_binding('KP_ENTER', 'ksh_series_kpenter', series_enter)
end
mp.add_forced_key_binding('Ctrl+PGUP', 'ksh_chap_prev', function() chapter_step(-1) end)
mp.add_forced_key_binding('Ctrl+PGDWN', 'ksh_chap_next', function() chapter_step(1) end)

-- ---------- Raccourcis PRO ----------
if PRO then
    local function k(key, name, fn, flags) mp.add_forced_key_binding(key, name, fn, flags) end
    if not APERCU then   -- dans l'aperçu, ← → passent à la vidéo voisine (comme Orbit Look)
        k('RIGHT', 'ksh_fstep', function() mp.commandv('frame-step') end, { repeatable = true })
        k('LEFT', 'ksh_fback', function() mp.commandv('frame-back-step') end, { repeatable = true })
    end
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

-- ---------- Aperçu (barre Espace dans l'Explorateur / sur le Bureau, Windows) ----------
-- Comme Orbit Look : Espace ou Échap referme, ← → (et ↑ ↓) passent à la vidéo voisine, Entrée ouvre en grand.
if APERCU then
    local function k(key, name, fn) mp.add_forced_key_binding(key, name, fn) end
    k('SPACE', 'ksh_ap_space', close_player)
    k('ESC', 'ksh_ap_esc', close_player)
    k('ENTER', 'ksh_ap_enter', open_full)
    k('KP_ENTER', 'ksh_ap_kpenter', open_full)
    k('LEFT', 'ksh_ap_left', function() go_neighbor(-1) end)
    k('UP', 'ksh_ap_up', function() go_neighbor(-1) end)
    k('RIGHT', 'ksh_ap_right', function() go_neighbor(1) end)
    k('DOWN', 'ksh_ap_down', function() go_neighbor(1) end)
end
