using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using static KSHPLAYER.Apercu.NativeMethods;

namespace KSHPLAYER.Apercu
{
    /// <summary>
    /// Écoute la touche Espace dans tout Windows (crochet clavier bas niveau, comme Orbit Look).
    /// Le crochet vit sur son propre fil, qui ne fait QUE ça : il répond toujours en quelques microsecondes,
    /// sinon Windows finit par le retirer. Les vrais travaux (Explorateur, lecture) partent vers un autre fil.
    /// </summary>
    internal sealed class KeyboardHook : IDisposable
    {
        private readonly Func<bool> _onSpace;   // premier appui sur Espace : true = la touche est pour nous
        private readonly LowLevelKeyboardProc _proc;
        private Thread _thread;
        private uint _threadId;
        private IntPtr _hook;
        private DateTime _hookedAt;
        private bool _spaceDown, _swallowing;
        private const uint WM_QUIT_HOOK = WM_APP + 1;
        private System.Threading.Timer _otherCheck;

        /// <summary>Marque de la touche Espace que l'aperçu renvoie lui-même (SendInput) : jamais interceptée.</summary>
        public static readonly IntPtr OwnInputTag = new IntPtr(0x4B534850);   // « KSHP »

        /// <summary>Orbit Look tourne sur ce PC : on lui laisse la barre Espace (vérifié toutes les 3 s, hors du crochet).</summary>
        public volatile bool OtherPreviewRunning;

        /// <summary>Dernière lettre / chiffre tapé (Environment.TickCount) : l'utilisateur tape peut-être un nom de fichier.</summary>
        public volatile int LastTypedTick;

        public KeyboardHook(Func<bool> onSpace)
        {
            _onSpace = onSpace;
            _proc = HookProc;   // gardé en champ : le ramasse-miettes ne doit pas le libérer
            LastTypedTick = Environment.TickCount - 60000;   // « rien tapé récemment » (calcul sûr même quand le compteur reboucle)
        }

        public void Start()
        {
            using (var ready = new ManualResetEventSlim())
            {
                _thread = new Thread(() => Run(ready)) { IsBackground = true, Name = "crochet-clavier" };
                _thread.Start();
                ready.Wait(3000);
            }
        }

        private void Run(ManualResetEventSlim ready)
        {
            _threadId = GetCurrentThreadId();
            PeekMessage(out _, IntPtr.Zero, 0, 0, 0);   // crée la file de messages de ce fil
            // la recherche d'Orbit Look parcourt tous les processus : jamais sur ce fil (chaque frappe l'attendrait)
            _otherCheck = new System.Threading.Timer(_ => CheckOtherPreview(), null, 0, 3000);
            Install();
            var timer = SetTimer(IntPtr.Zero, UIntPtr.Zero, 2000, IntPtr.Zero);
            ready.Set();
            while (GetMessage(out MSG msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (msg.message == WM_QUIT_HOOK) break;
                if (msg.message == WM_TIMER && msg.hwnd == IntPtr.Zero)
                {
                    // Windows retire sans prévenir un crochet qui a tardé : on le réinstalle de temps en temps
                    if ((DateTime.Now - _hookedAt).TotalSeconds > 60) Install();
                    continue;
                }
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
            KillTimer(IntPtr.Zero, timer);
            _otherCheck?.Dispose();
            if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }

        private void Install()
        {
            // le nouveau crochet d'abord, l'ancien ensuite : aucune frappe ne passe entre les deux
            var h = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
            if (h == IntPtr.Zero)
            {
                Log.Write("Crochet clavier impossible (erreur " + Marshal.GetLastWin32Error() + ")");
                return;
            }
            var old = _hook;
            _hook = h;
            _hookedAt = DateTime.Now;
            if (old != IntPtr.Zero) UnhookWindowsHookEx(old);
        }

        private void CheckOtherPreview()
        {
            try
            {
                var procs = Process.GetProcessesByName("OrbitLook");
                OtherPreviewRunning = procs.Length > 0;
                foreach (var p in procs) p.Dispose();
            }
            catch { OtherPreviewRunning = false; }
        }

        private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                var k = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
                int msg = wParam.ToInt32();
                if (k.vkCode != VK_SPACE)
                {
                    // lettre, chiffre ou ponctuation : on tape peut-être un nom pour sauter à un fichier dans l'Explorateur
                    if (msg == WM_KEYDOWN && IsCharacterKey(k.vkCode) && (k.flags & LLKHF_ALTDOWN) == 0 && !KeyDown(VK_CONTROL))
                        LastTypedTick = Environment.TickCount;
                }
                // la touche Espace que l'aperçu renvoie lui-même n'est jamais interceptée
                else if (k.dwExtraInfo != OwnInputTag)
                {
                    if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
                    {
                        if (!_spaceDown)
                        {
                            // premier appui ; tant que la touche reste enfoncée, on garde la même décision
                            // (Espace maintenue n'ouvre et ne ferme pas l'aperçu en boucle)
                            _spaceDown = true;
                            bool take = false;
                            try { take = _onSpace(); } catch (Exception ex) { Log.Write("Touche Espace", ex); }
                            _swallowing = take;
                        }
                        if (_swallowing) return (IntPtr)1;
                    }
                    else if (msg == WM_KEYUP || msg == WM_SYSKEYUP)
                    {
                        _spaceDown = false;
                        if (_swallowing)
                        {
                            _swallowing = false;
                            return (IntPtr)1;
                        }
                    }
                }
            }
            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        private static bool IsCharacterKey(uint vk) =>
            (vk >= 0x30 && vk <= 0x39) || (vk >= 0x41 && vk <= 0x5A) || (vk >= 0x60 && vk <= 0x6F) ||
            (vk >= 0xBA && vk <= 0xC0) || (vk >= 0xDB && vk <= 0xDF) || vk == 0xE2;

        public void Dispose()
        {
            if (_threadId != 0) PostThreadMessage(_threadId, WM_QUIT_HOOK, IntPtr.Zero, IntPtr.Zero);
            _thread?.Join(1000);
        }
    }
}
