// Melody Overlay — управление музыкой поверх игр без Alt+Tab.
//
// Работает с любым плеером, который виден в системной панели мультимедиа Windows:
// Spotify, Яндекс Музыка, YouTube / VK / Яндекс в браузере, Apple Music, AIMP, VLC и т.д.
// Оверлей не забирает фокус у игры и пропускает клики сквозь себя.
//
// Сборка: build.bat (нужен только встроенный в Windows .NET Framework 4.x, ничего ставить не надо).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using Windows.Media.Control;
using Windows.Storage.Streams;

using WinTimer = System.Windows.Forms.Timer;
using MediaSession = Windows.Media.Control.GlobalSystemMediaTransportControlsSession;
using MediaProps = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionMediaProperties;
using PlaybackStatus = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus;

namespace MelodyOverlay
{
    static class Program
    {
        public static bool DemoSnapshot, DemoHover, DemoExpanded, OpenSettingsOnStart;
        public static string LangOverride;

        [STAThread]
        static int Main(string[] args)
        {
            if (args.Length == 2 && args[0] == "--write-icon")
            {
                IconArt.WriteIco(args[1], new int[] { 16, 20, 24, 32, 40, 48, 64, 256 });
                return 0;
            }

            if (args.Length == 1 && args[0] == "--selftest")
            {
                int pct; bool mute;
                bool ok = Volume.TryChange(0f, out pct, out mute);
                Log.Write("selftest: volume ok=" + ok + " percent=" + pct + " muted=" + mute);
                try
                {
                    var m = WinRtAsync.AsTask(GlobalSystemMediaTransportControlsSessionManager.RequestAsync()).Result;
                    foreach (MediaSession s in m.GetSessions())
                    {
                        var pi = s.GetPlaybackInfo();
                        var c = pi.Controls;
                        Log.Write("selftest: " + s.SourceAppUserModelId + " status=" + pi.PlaybackStatus +
                                  " next=" + c.IsNextEnabled + " prev=" + c.IsPreviousEnabled + " seek=" + c.IsPlaybackPositionEnabled +
                                  " ff=" + c.IsFastForwardEnabled + " rew=" + c.IsRewindEnabled + " playpause=" + c.IsPlayPauseToggleEnabled);
                    }
                }
                catch (Exception ex) { Log.Write("selftest: медиасессии: " + ex.Message); }
                return ok ? 0 : 1;
            }

            string snapshot = null;
            bool quiet = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--autostart") quiet = true;
                if (args[i] == "--snapshot" && i + 1 < args.Length) snapshot = args[i + 1];
                if (args[i] == "--settings") OpenSettingsOnStart = true;
                if (args[i] == "--lang" && i + 1 < args.Length) LangOverride = args[i + 1];   // для скриншотов
                if (args[i] == "--demo") DemoSnapshot = true;     // для скриншотов: выдуманный трек
                if (args[i] == "--hover") DemoHover = true;       // для скриншотов: кнопки как при наведении
                if (args[i] == "--expanded") DemoExpanded = true; // для скриншотов: кружок раскрыт в плашку
            }

            Native.EnableDpiAwareness();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => Log.Write("Ошибка: " + e.Exception);
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());

            Config.MigrateOld();
            try { L.Apply(LangOverride ?? Config.Load().Language); } catch { L.Apply(LangOverride); }

            if (snapshot != null)
            {
                Application.Run(new App(snapshot, true));
                return 0;
            }

            bool createdNew;
            using (var mutex = new Mutex(true, "MelodyOverlay.SingleInstance.v1", out createdNew))
            {
                if (!createdNew)
                {
                    MessageBox.Show(L.T("Melody Overlay уже запущен — его значок в трее, возле часов.",
                                        "Melody Overlay is already running — its icon is in the tray, next to the clock."),
                        "Melody Overlay", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return 0;
                }
                Application.Run(new App(null, quiet));
            }
            return 0;
        }
    }

    // ------------------------------------------------------------------ приложение

    class App : ApplicationContext
    {
        const int HK_PLAYPAUSE = 1, HK_NEXT = 2, HK_PREV = 3, HK_VOLUP = 4, HK_VOLDOWN = 5, HK_TOGGLE = 6;
        const int BaseW = 380, BaseH = 100;

        static readonly Color Accent = Color.FromArgb(255, 61, 220, 151);
        static readonly Color Muted = Color.FromArgb(255, 165, 168, 182);

        readonly string snapshotPath;
        readonly bool quiet;
        readonly SynchronizationContext ui;
        Config cfg;
        bool firstRun;

        OverlayWindow win;
        NotifyIcon tray;
        Icon trayIcon;
        IntPtr trayIconHandle;
        MenuItem miAlways, miAutostart;
        SettingsForm settings;
        FileSystemWatcher watcher;
        DateTime lastSelfSave = DateTime.MinValue;

        readonly WinTimer pollTimer = new WinTimer();
        readonly WinTimer animTimer = new WinTimer();
        readonly WinTimer reloadTimer = new WinTimer();

        GlobalSystemMediaTransportControlsSessionManager mgr;
        string rememberedApp;
        bool polling;

        // что показываем
        bool hasSession;
        string title = "", artist = "", appId = "";
        string trackKey;                       // null — ещё ни разу не опрашивали
        PlaybackStatus status = PlaybackStatus.Closed;
        bool hasTimeline;
        TimeSpan tlPos, tlDur;
        DateTime tlStampUtc;
        Bitmap thumb;
        int thumbLoadsLeft;
        string hintText, hintIcon;
        float hintLevel = -1f;
        DateTime hintUntil;
        bool hintWasActive;

        // состояние окна
        Bitmap frame;
        bool dirty = true;
        bool shown;
        DateTime lastTopmost;

        // мышь
        const int T_NONE = 0, T_PREV = 1, T_PLAY = 2, T_NEXT = 3, T_GEAR = 4, T_BAR = 5, T_CIRCLE = 6;
        bool hovering, dragging;
        int hoverTarget, pressTarget;
        float dragFrac;
        bool canSeek, canNext = true, canPrev = true;
        RectangleF rGear, rPrev, rPlay, rNext, rBarHit;
        float barX0, barX1;

        public App(string snapshotPath, bool quiet)
        {
            this.snapshotPath = snapshotPath;
            this.quiet = quiet;
            ui = SynchronizationContext.Current;
            if (snapshotPath == null) { Config.MigrateOld(); Autostart.MigrateOld(); }
            firstRun = !File.Exists(Config.PathIni);
            cfg = Config.Load();
            if (Program.LangOverride != null) cfg.Language = Program.LangOverride;
            L.Apply(cfg.Language);

            if (snapshotPath == null)
            {
                win = new OverlayWindow();
                win.HotkeyPressed += OnHotkey;
                win.MouseMoved += OnOverlayMouseMove;
                win.MouseLeft += OnOverlayMouseLeave;
                win.LeftDown += OnOverlayMouseDown;
                win.LeftUp += OnOverlayMouseUp;
                win.CaptureLost += OnOverlayCaptureLost;
                win.WantsHandCursor = WantsHand;
                win.RightUp += ShowCircleMenu;
                ApplyInteractivity();

                trayIcon = MakeTrayIcon(out trayIconHandle);
                tray = new NotifyIcon();
                tray.Icon = trayIcon;
                tray.Text = "Melody Overlay";
                tray.ContextMenu = BuildMenu();
                tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) Flash(); };
                tray.MouseDoubleClick += (s, e) => { if (e.Button == MouseButtons.Left) OpenSettingsWindow(); };
                tray.Visible = true;

                RegisterHotkeys(!quiet || firstRun);

                pollTimer.Interval = 700;
                pollTimer.Tick += (s, e) => { var t = PollAsync(); };
                animTimer.Interval = 15;
                animTimer.Tick += (s, e) => AnimTick();
                reloadTimer.Interval = 400;
                reloadTimer.Tick += (s, e) => { reloadTimer.Stop(); ReloadConfig(); };
                WatchConfig();
                StartCircle();
            }

            InitMedia();
        }

        // Выдуманный играющий трек — для скриншотов и проверки (--demo). Настоящий плеер при этом не трогаем.
        void SetDemoTrack()
        {
            hasSession = true; appId = "Spotify.exe"; status = PlaybackStatus.Playing;
            title = L.T("Название трека", "Track title"); artist = L.T("Исполнитель", "Artist");
            hasTimeline = true; tlDur = TimeSpan.FromSeconds(225); tlPos = TimeSpan.FromSeconds(83);
            tlStampUtc = DateTime.UtcNow; canSeek = true; canNext = true; canPrev = true;
            SetThumb(null);
        }

        async void InitMedia()
        {
            if (Program.DemoSnapshot && snapshotPath == null)
            {
                SetDemoTrack();
                Flash(4);
                return;
            }

            try { mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync(); }
            catch (Exception ex) { Log.Write("Панель мультимедиа Windows недоступна: " + ex.Message); }

            await PollAsync();

            if (snapshotPath != null)
            {
                if (Program.DemoSnapshot) SetDemoTrack();
                hovering = Program.DemoHover;
                if (hovering) cfg.MouseControls = true;
                ct = ctFrom = ctTo = Program.DemoExpanded ? 1f : 0f;
                ComposeCircle();
                frame.Save(snapshotPath, ImageFormat.Png);
                ExitThread();
                return;
            }

            pollTimer.Start();
            Flash(4);
            if (Program.OpenSettingsOnStart) OpenSettingsWindow();
        }

        // ---------------------------------------------------------- горячие клавиши

        void UnregisterAll()
        {
            if (win == null) return;
            for (int i = 1; i <= 6; i++) Native.UnregisterHotKey(win.Handle, i);
        }

        List<string> RegisterAll()
        {
            var failed = new List<string>();
            if (win == null) return failed;
            UnregisterAll();
            Reg(HK_PLAYPAUSE, cfg.PlayPause, false, failed);
            Reg(HK_NEXT, cfg.Next, false, failed);
            Reg(HK_PREV, cfg.Previous, false, failed);
            Reg(HK_VOLUP, cfg.VolumeUp, true, failed);
            Reg(HK_VOLDOWN, cfg.VolumeDown, true, failed);
            Reg(HK_TOGGLE, cfg.ToggleOverlay, false, failed);
            if (failed.Count > 0) Log.Write("Не удалось назначить: " + string.Join(", ", failed.ToArray()));
            return failed;
        }

        void RegisterHotkeys(bool announce)
        {
            List<string> failed = RegisterAll();
            if (failed.Count > 0)
            {
                var pretty = new List<string>();
                foreach (string f in failed) pretty.Add(Hotkey.Pretty(f));
                tray.ShowBalloonTip(6000, L.T("Melody Overlay — горячие клавиши", "Melody Overlay — hotkeys"),
                    L.T("Не получилось назначить: ", "Couldn't register: ") + string.Join(", ", pretty.ToArray()) +
                    L.T(". Сочетание занято другой программой — поменяй его в настройках (двойной клик по значку).",
                        ". The shortcut is taken by another program — change it in settings (double-click the tray icon)."),
                    ToolTipIcon.Warning);
            }
            else if (announce)
            {
                tray.ShowBalloonTip(5000, L.T("Melody Overlay запущен", "Melody Overlay is running"),
                    Hotkey.Pretty(cfg.PlayPause) + L.T(" — пауза / играть\n", " — play / pause\n") +
                    Hotkey.Pretty(cfg.Next) + " / " + Hotkey.Pretty(cfg.Previous) + L.T(" — треки\n", " — next / previous\n") +
                    L.T("Двойной клик по значку — настройки", "Double-click the icon for settings"),
                    ToolTipIcon.None);
            }
        }

        // --- для окна настроек

        public void SuspendHotkeys() { UnregisterAll(); }
        public void ResumeHotkeys() { RegisterAll(); }

        public bool IsComboFree(uint mods, uint vk)
        {
            if (win == null) return true;
            if (!Native.RegisterHotKey(win.Handle, 99, mods | Hotkey.MOD_NOREPEAT, vk)) return false;
            Native.UnregisterHotKey(win.Handle, 99);
            return true;
        }

        // Предпросмотр, пока окно настроек открыто (горячие клавиши не трогаем).
        // Прозрачность относится к свёрнутому кружку — её и показываем на свёрнутом (раскрытая плашка всегда
        // непрозрачная, на ней изменение не видно). Размер, время и язык — раскрывая плашку.
        public void PreviewLook(Config look)
        {
            bool opacityOnly = look.Opacity != cfg.Opacity && look.Scale == cfg.Scale && look.ShowSeconds == cfg.ShowSeconds;
            bool showPanel = look.Scale != cfg.Scale || look.ShowSeconds != cfg.ShowSeconds || look.Language != cfg.Language;
            cfg.CopyLookFrom(look);
            ApplyInteractivity();
            ApplyLanguage();
            dirty = true;
            if (opacityOnly)
            {
                if (!cfg.AlwaysVisible && ctTo > 0f) MorphTo(0f, 300f);
                RedrawNow();
            }
            else if (showPanel) Flash();
            else RedrawNow();
        }

        public void RestoreLook(Config look)
        {
            cfg.CopyLookFrom(look);
            cfg.CircleX = savedCircleX;
            cfg.CircleY = savedCircleY;
            ApplyInteractivity();
            ApplyLanguage();
            RedrawNow();
        }

        public void ApplyConfig(Config c, bool autostart)
        {
            // место кружка — живое (его могли перетащить, пока были открыты настройки), а не из черновика
            int keepX = cfg.CircleX, keepY = cfg.CircleY;
            cfg = c.Clone();
            cfg.CircleX = keepX;
            cfg.CircleY = keepY;
            SaveConfig();
            RegisterAll();
            ApplyInteractivity();
            ApplyLanguage();
            try { if (Autostart.IsOn() != autostart) Autostart.Set(autostart); }
            catch (Exception ex) { Log.Write("автозапуск: " + ex.Message); }
            SyncMenu();
            ShowHint(L.T("Настройки сохранены", "Settings saved"), "check");
        }

        public void OpenSettingsWindow()
        {
            if (settings != null && !settings.IsDisposed)
            {
                if (settings.WindowState == FormWindowState.Minimized) settings.WindowState = FormWindowState.Normal;
                settings.Activate();
                return;
            }
            savedCircleX = cfg.CircleX;
            savedCircleY = cfg.CircleY;
            settings = new SettingsForm(this, cfg, Autostart.IsOn());
            settings.FormClosed += (s, e) => { settings = null; };
            settings.Show();
            settings.Activate();
        }

        void Reg(int id, string combo, bool repeat, List<string> failed)
        {
            uint mods, vk;
            if (!Hotkey.TryParse(combo, out mods, out vk))
            {
                if (!Hotkey.IsDisabled(combo)) failed.Add(combo);
                return;
            }
            if (!repeat) mods |= Hotkey.MOD_NOREPEAT;
            if (!Native.RegisterHotKey(win.Handle, id, mods, vk)) failed.Add(combo);
        }

        void OnHotkey(int id)
        {
            switch (id)
            {
                case HK_PLAYPAUSE: PlayPause(); break;
                case HK_NEXT: Skip(true); break;
                case HK_PREV: Skip(false); break;
                case HK_VOLUP: ChangeVolume(+0.02f); break;
                case HK_VOLDOWN: ChangeVolume(-0.02f); break;
                case HK_TOGGLE: SetAlways(!cfg.AlwaysVisible); break;
            }
        }

        async void PlayPause()
        {
            bool wasPlaying = hasSession && status == PlaybackStatus.Playing;
            ShowHint(wasPlaying ? L.T("Пауза", "Paused") : L.T("Воспроизведение", "Playing"), wasPlaying ? "pause" : "play");
            bool ok = false;
            MediaSession s = PickSession();
            if (s != null)
            {
                try { ok = await s.TryTogglePlayPauseAsync(); }
                catch (Exception ex) { Log.Write("play/pause: " + ex.Message); }
            }
            if (!ok) Native.TapKey(Native.VK_MEDIA_PLAY_PAUSE);
            RefreshSoon();
        }

        async void Skip(bool next)
        {
            ShowHint(next ? L.T("Следующий трек", "Next track") : L.T("Предыдущий трек", "Previous track"), next ? "next" : "prev");
            bool ok = false;
            MediaSession s = PickSession();
            if (s != null)
            {
                try { ok = next ? await s.TrySkipNextAsync() : await s.TrySkipPreviousAsync(); }
                catch (Exception ex) { Log.Write("skip: " + ex.Message); }
            }
            if (!ok) Native.TapKey(next ? Native.VK_MEDIA_NEXT_TRACK : Native.VK_MEDIA_PREV_TRACK);
            RefreshSoon();
        }

        void ChangeVolume(float delta)
        {
            int percent; bool muted;
            string icon = delta > 0 ? "volup" : "voldown";
            if (Volume.TryChange(delta, out percent, out muted))
            {
                hintLevel = muted ? 0f : percent / 100f;
                ShowHint(muted ? L.T("Звук выключен", "Muted") : L.T("Громкость ", "Volume ") + percent + " %", icon, hintLevel);
            }
            else
            {
                Native.TapKey(delta > 0 ? Native.VK_VOLUME_UP : Native.VK_VOLUME_DOWN);
                ShowHint(delta > 0 ? L.T("Громкость +", "Volume +") : L.T("Громкость −", "Volume −"), icon);
            }
        }

        async void RefreshSoon()
        {
            await Task.Delay(150); await PollAsync();
            await Task.Delay(500); await PollAsync();
            await Task.Delay(1200); await PollAsync();
        }

        // ---------------------------------------------------------- медиасессии

        // Какой плеер сейчас «главный»: тот, что играет (предпочитая прошлый выбор),
        // иначе прошлый выбор, иначе то, что считает текущим сама Windows.
        MediaSession PickSession()
        {
            if (mgr == null) return null;
            IReadOnlyList<MediaSession> list;
            try { list = mgr.GetSessions(); } catch { return null; }

            MediaSession remembered = null, firstPlaying = null;
            bool rememberedPlaying = false;
            foreach (MediaSession s in list)
            {
                PlaybackStatus st;
                try { st = s.GetPlaybackInfo().PlaybackStatus; } catch { continue; }
                string id = SafeAppId(s);
                bool playing = st == PlaybackStatus.Playing;
                if (remembered == null && id == rememberedApp) { remembered = s; rememberedPlaying = playing; }
                if (playing && firstPlaying == null) firstPlaying = s;
            }

            MediaSession pick;
            if (remembered != null && rememberedPlaying) pick = remembered;
            else if (firstPlaying != null) pick = firstPlaying;
            else if (remembered != null) pick = remembered;
            else
            {
                try { pick = mgr.GetCurrentSession(); } catch { pick = null; }
            }
            if (pick != null) rememberedApp = SafeAppId(pick);
            return pick;
        }

        async Task PollAsync()
        {
            if (polling) return;
            polling = true;
            try
            {
                MediaSession s = PickSession();
                if (s == null)
                {
                    if (hasSession || trackKey == null)
                    {
                        bool changed = hasSession;
                        hasSession = false; title = ""; artist = ""; appId = "";
                        canSeek = false; canNext = true; canPrev = true;
                        hasTimeline = false; status = PlaybackStatus.Closed;
                        SetThumb(null);
                        trackKey = "";
                        dirty = true;
                        UpdateTrayText();
                        if (changed && cfg.ShowOnTrackChange) Flash();
                    }
                    return;
                }

                PlaybackStatus st = PlaybackStatus.Closed;
                try
                {
                    var pi = s.GetPlaybackInfo();
                    st = pi.PlaybackStatus;
                    var pc = pi.Controls;
                    canSeek = pc.IsPlaybackPositionEnabled;
                    canNext = pc.IsNextEnabled;
                    canPrev = pc.IsPreviousEnabled;
                }
                catch { }

                MediaProps props = null;
                try { props = await s.TryGetMediaPropertiesAsync(); } catch { }

                string app = SafeAppId(s);
                string t = props != null ? (props.Title ?? "") : "";
                string a = props != null ? (props.Artist ?? "") : "";
                if (a.Length == 0 && props != null) a = props.AlbumArtist ?? "";
                string key = app + "\n" + t + "\n" + a;

                bool firstPoll = trackKey == null;
                bool trackChanged = !firstPoll && key != trackKey;
                bool statusChanged = !firstPoll && hasSession && st != status;

                if (key != trackKey) { SetThumb(null); thumbLoadsLeft = 4; }
                trackKey = key;
                hasSession = true;
                appId = app; title = t; artist = a; status = st;

                hasTimeline = false;
                try
                {
                    var tl = s.GetTimelineProperties();
                    TimeSpan dur = tl.EndTime - tl.StartTime;
                    if (dur.TotalSeconds >= 1 && dur.TotalHours < 24)
                    {
                        hasTimeline = true;
                        tlDur = dur;
                        tlPos = tl.Position - tl.StartTime;
                        tlStampUtc = tl.LastUpdatedTime.UtcDateTime;
                    }
                }
                catch { }

                // Обложку перечитываем несколько раз: плееры часто присылают её позже названия.
                if (thumbLoadsLeft > 0 && props != null && props.Thumbnail != null)
                {
                    thumbLoadsLeft--;
                    Bitmap img = await LoadThumbAsync(props.Thumbnail);
                    if (img != null)
                    {
                        if (key == trackKey) SetThumb(img); else img.Dispose();
                    }
                }

                dirty = true;
                UpdateTrayText();
                if ((trackChanged || statusChanged) && cfg.ShowOnTrackChange) Flash();
            }
            catch (Exception ex) { Log.Write("poll: " + ex.Message); }
            finally { polling = false; }
        }

        static async Task<Bitmap> LoadThumbAsync(IRandomAccessStreamReference r)
        {
            try
            {
                using (IRandomAccessStreamWithContentType ws = await r.OpenReadAsync())
                {
                    ulong size = ws.Size;
                    if (size == 0 || size > 20 * 1024 * 1024) return null;
                    using (var reader = new DataReader(ws))
                    {
                        uint got = await WinRtAsync.AsTask<uint>(reader.LoadAsync((uint)size));
                        if (got == 0) return null;
                        var bytes = new byte[got];
                        reader.ReadBytes(bytes);
                        using (var ms = new MemoryStream(bytes))
                        using (var img = Image.FromStream(ms))
                            return Gfx.CoverSquare(img, 256);
                    }
                }
            }
            catch (Exception ex) { Log.Write("обложка: " + ex.Message); return null; }
        }

        void SetThumb(Bitmap b)
        {
            if (thumb != null) thumb.Dispose();
            thumb = b;
            thumbVersion++;
            dirty = true;
        }

        static string SafeAppId(MediaSession s)
        {
            try { return s.SourceAppUserModelId ?? ""; } catch { return ""; }
        }

        TimeSpan CurrentPos()
        {
            TimeSpan p = tlPos;
            if (status == PlaybackStatus.Playing)
            {
                TimeSpan since = DateTime.UtcNow - tlStampUtc;
                if (since > TimeSpan.Zero && since < TimeSpan.FromHours(6)) p += since;
            }
            if (p < TimeSpan.Zero) p = TimeSpan.Zero;
            if (p > tlDur) p = tlDur;
            return p;
        }

        // ---------------------------------------------------------- показ / анимация

        void ShowHint(string text, string icon) { ShowHint(text, icon, -1f); }

        void ShowHint(string text, string icon, float level)
        {
            hintText = text; hintIcon = icon; hintLevel = level;
            hintUntil = DateTime.UtcNow.AddSeconds(1.6);
            dirty = true;
            Flash();
        }

        void Flash() { Flash(cfg.ShowSeconds); }

        // Показать: кружок раскрывается в плашку на заданное время.
        void Flash(double seconds)
        {
            if (win == null) return;
            Expand(seconds);
        }

        void AnimTick() { CircleTick(); }

        void Render() { RenderCircle(); }

        // Текст, полоса прогресса и кнопки карточки. Координаты — внутри карточки: её левый верхний угол — (0, 0).
        // Зоны кнопок запоминаются тоже в координатах карточки (см. cardOffX / cardOffY).
        void DrawCardContent(Graphics g, float s, float H, float tx, float tr, bool controls)
        {
            DateTime now = DateTime.UtcNow;
            bool hint = hintText != null && now < hintUntil;
            bool playing = hasSession && status == PlaybackStatus.Playing;
            float pad = 12 * s;
            float tw = tr - tx;
            {
                // зоны кнопок (по ним же определяем, куда попала мышь)
                float gs = 18 * s;
                rGear = new RectangleF(tr - gs + 3 * s, 12 * s, gs, gs);
                float cy = 52 * s, pd = 30 * s, sd = 26 * s;
                rNext = new RectangleF(tr - sd + 4 * s, cy - sd / 2, sd, sd);
                rPlay = new RectangleF(rNext.X - 2 * s - pd, cy - pd / 2, pd, pd);
                rPrev = new RectangleF(rPlay.X - 2 * s - sd, cy - sd / 2, sd, sd);
                float barCenter = H - pad - 2.5f * s;
                barX0 = tx; barX1 = tr;
                rBarHit = new RectangleF(tx - 4 * s, barCenter - 9 * s, tw + 8 * s, 18 * s);
                float textW = controls ? rPrev.X - tx - 6 * s : tw;
                float line1Right = controls ? rGear.X - 6 * s : tr;

                using (var fSmall = new Font("Segoe UI", 11.5f * s, FontStyle.Regular, GraphicsUnit.Pixel))
                using (var fTitle = new Font("Segoe UI Semibold", 15.5f * s, FontStyle.Regular, GraphicsUnit.Pixel))
                using (var fSub = new Font("Segoe UI", 13f * s, FontStyle.Regular, GraphicsUnit.Pixel))
                using (var sf = new StringFormat(StringFormatFlags.NoWrap))
                using (var sfRight = new StringFormat(StringFormatFlags.NoWrap))
                using (var bSmall = new SolidBrush(Color.FromArgb(150, 255, 255, 255)))
                using (var bHint = new SolidBrush(Color.FromArgb(240, 255, 255, 255)))
                using (var bTitle = new SolidBrush(Color.White))
                using (var bSub = new SolidBrush(Color.FromArgb(185, 255, 255, 255)))
                {
                    sf.Trimming = StringTrimming.EllipsisCharacter;
                    sf.LineAlignment = StringAlignment.Center;
                    sfRight.Alignment = StringAlignment.Far;
                    sfRight.LineAlignment = StringAlignment.Center;

                    // строка 1: статус / подсказка + время
                    float y1 = 12 * s, h1 = 18 * s;
                    string icon, line1;
                    Color iconColor;
                    if (hint) { icon = hintIcon; line1 = hintText; iconColor = Accent; }
                    else if (!hasSession) { icon = "stop"; line1 = "Melody Overlay"; iconColor = Muted; }
                    else
                    {
                        icon = playing ? "play" : (status == PlaybackStatus.Paused ? "pause" : "stop");
                        line1 = PrettyApp(appId) + "  ·  " + StatusText(status);
                        iconColor = playing ? Accent : Muted;
                    }
                    float ig = 10 * s;
                    Gfx.DrawGlyph(g, icon, new RectangleF(tx, y1 + (h1 - ig) / 2, ig, ig), iconColor);
                    float lx = tx + ig + 7 * s;

                    float timeW = 0;
                    if (hasSession && hasTimeline && (!hint || dragging))
                    {
                        string ts = Fmt(dragging ? DragPos() : CurrentPos()) + " / " + Fmt(tlDur);
                        timeW = g.MeasureString(ts, fSmall).Width;
                        g.DrawString(ts, fSmall, dragging ? bHint : bSmall, new RectangleF(line1Right - timeW, y1, timeW + 2, h1), sfRight);
                    }
                    g.DrawString(line1, fSmall, hint ? bHint : bSmall,
                        new RectangleF(lx, y1, Math.Max(10, line1Right - lx - timeW - 6 * s), h1), sf);

                    // название и исполнитель
                    string t = hasSession ? (title.Length > 0 ? title : L.T("Без названия", "Untitled")) : L.T("Ничего не играет", "Nothing playing");
                    string a = hasSession ? artist : L.T("Включи музыку в плеере или браузере", "Start music in a player or browser");
                    g.DrawString(t, fTitle, bTitle, new RectangleF(tx - s, 31 * s, textW + s, 23 * s), sf);
                    if (a.Length > 0)
                        g.DrawString(a, fSub, bSub, new RectangleF(tx - s, 53 * s, textW + s, 19 * s), sf);

                    // полоса: громкость (во время подсказки) или прогресс трека
                    float frac = -1f;
                    Color fillColor = playing ? Accent : Muted;
                    bool seekBar = false;
                    if (hint && hintLevel >= 0 && !dragging) { frac = hintLevel; fillColor = Color.White; }
                    else if (hasSession && hasTimeline && tlDur.TotalMilliseconds > 0)
                    {
                        frac = dragging ? dragFrac
                             : (float)Math.Max(0, Math.Min(1, CurrentPos().TotalMilliseconds / tlDur.TotalMilliseconds));
                        seekBar = controls && canSeek;
                    }

                    if (frac >= 0)
                    {
                        float ph = (seekBar ? 5 : 3) * s, py = barCenter - ph / 2;
                        var track = new RectangleF(tx, py, tw, ph);
                        using (GraphicsPath tp = Gfx.RoundRect(track, ph / 2))
                        using (var tb = new SolidBrush(Color.FromArgb(45, 255, 255, 255)))
                            g.FillPath(tb, tp);
                        if (frac > 0.003f)
                        {
                            var fill = new RectangleF(tx, py, Math.Max(ph, tw * frac), ph);
                            using (GraphicsPath fp = Gfx.RoundRect(fill, ph / 2))
                            using (var fb = new SolidBrush(dragging ? Color.White : fillColor))
                                g.FillPath(fb, fp);
                        }
                        if (seekBar)
                        {
                            float kr = (hoverTarget == T_BAR || dragging ? 7f : 5.5f) * s;
                            float kx = tx + tw * frac;
                            using (var kb = new SolidBrush(Color.White))
                                g.FillEllipse(kb, kx - kr, barCenter - kr, kr * 2, kr * 2);
                        }
                    }

                    // кнопки при наведении мыши
                    if (controls)
                    {
                        DrawRoundButton(g, rPrev, hoverTarget == T_PREV, canPrev, "prev", s);
                        DrawRoundButton(g, rNext, hoverTarget == T_NEXT, canNext, "next", s);
                        using (var pb = new SolidBrush(hoverTarget == T_PLAY ? Accent : Color.White))
                            g.FillEllipse(pb, rPlay);
                        float gl = 11 * s;
                        var pg = new RectangleF(rPlay.X + (pd - gl) / 2 + (playing ? 0 : 1.2f * s), rPlay.Y + (pd - gl) / 2, gl, gl);
                        Gfx.DrawGlyph(g, playing ? "pause" : "play", pg, Color.FromArgb(255, 18, 18, 24));

                        if (hoverTarget == T_GEAR)
                            using (var hb = new SolidBrush(Color.FromArgb(45, 255, 255, 255)))
                                g.FillEllipse(hb, RectangleF.Inflate(rGear, 3 * s, 3 * s));
                        Gfx.DrawGlyph(g, "gear", RectangleF.Inflate(rGear, -2 * s, -2 * s),
                            hoverTarget == T_GEAR ? Color.White : Color.FromArgb(180, 255, 255, 255));
                    }
                }
            }
        }

        // ---------------------------------------------------------- кружок

        // У края экрана висит круглая обложка трека. По щелчку, по горячей клавише или при смене трека
        // из неё вырастает плашка с названием, прогрессом и кнопками, а через несколько секунд втягивается обратно.

        const float CircleBase = 56f, CardRadius = 22f;
        const float ExpandMs = 520f, CollapseMs = 440f;

        float ct, ctFrom, ctTo;                 // 0 — кружок, 1 — раскрытая плашка
        float morphMs;
        DateTime morphStart, expandUntil;
        int ccx, ccy, lastD;                    // кружок на экране: левый верхний угол и диаметр
        float circleOffX, circleOffY;           // где кружок внутри текущего кадра
        float cardOffX, cardOffY;               // где плашка внутри текущего кадра
        bool circleDown, circleDrag;
        Point downCursor;
        int downCx, downCy;
        Bitmap cardBmp, faceBmp;
        string cardKey, faceKey;
        int thumbVersion;
        Point frameAt;
        int savedCircleX = -1, savedCircleY = -1;
        volatile bool pacing;
        int framePending;
        Thread pacer;
        ContextMenuStrip circleMenu;
        float vis, visFrom, visTo;              // видимость кружка: плавно появляется и исчезает (режимы «Показывать»)
        DateTime visStart;
        float spinAngle;                        // поворот обложки, градусы
        DateTime spinStamp, lastSpinFrame;
        volatile bool spinOnly;                 // идёт только вращение — хватит ~30 кадров в секунду
        Bitmap panelBase;
        string panelBaseKey;
        int cardVersion;

        const float SpinDegPerSec = 120f;       // один оборот за 3 секунды
        const float FadeMs = 180f;

        // Как пружина без отскока: трогается мягко, разгоняется и плавно садится на место.
        static float Spring(float p)
        {
            const float a = 7f;
            double f = 1 - (1 + a * p) * Math.Exp(-a * p);
            double end = 1 - (1 + a) * Math.Exp(-a);
            return (float)Math.Min(1.0, f / end);
        }

        static float Lerp(float a, float b, float t) { return a + (b - a) * t; }

        // Кружок виден всё время; таймер раз в 100 мс следит, когда свернуть плашку и обновить прогресс.
        void StartCircle()
        {
            if (win == null) return;
            animTimer.Interval = 100;
            animTimer.Start();
            dirty = true;
            RenderCircle();
        }

        void ResetCirclePlace()
        {
            cfg.CircleX = cfg.CircleY = -1;
            dirty = true;
        }

        void Expand(double seconds)
        {
            DateTime until = DateTime.UtcNow.AddSeconds(Math.Max(1, seconds));
            if (until > expandUntil) expandUntil = until;
            dirty = true;
            if (circleDrag) return;
            MorphTo(1f, ExpandMs);
            if (!pacing) RenderCircle();
        }

        void MorphTo(float target, float ms)
        {
            if (ctTo == target) return;
            ctFrom = ct;
            ctTo = target;
            morphStart = DateTime.UtcNow;
            // если плашка уже наполовину раскрыта — и путь, и время короче
            morphMs = Math.Max(120f, ms * Math.Abs(ctTo - ctFrom));
            StartPacer();
        }

        // Пока идёт анимация, кадры идут в такт развёртке монитора: 60, 144, 165 Гц — сколько он умеет.
        // Ждём именно сигнал монитора (а не «следующий кадр рабочего стола»): тот приходит, только когда на экране
        // что-то меняется, и на неподвижном экране анимация застревала.
        // Рисуется всё равно в основном потоке, фоновый поток только будит его.
        void StartPacer()
        {
            if (win == null) return;
            pacing = true;
            if (pacer != null && pacer.IsAlive) return;
            string device = Screen.FromPoint(new Point(ccx + lastD / 2, ccy + lastD / 2)).DeviceName;
            pacer = new Thread(() =>
            {
                using (var vblank = new VBlank(device))
                {
                    while (pacing)
                    {
                        if (spinOnly) Thread.Sleep(33);
                        else if (!vblank.Wait()) Thread.Sleep(7);
                        if (Interlocked.Exchange(ref framePending, 1) != 0) continue;
                        ui.Post(_ => { framePending = 0; if (pacing) CircleFrame(); }, null);
                    }
                }
            });
            pacer.IsBackground = true;
            pacer.Name = "Melody Overlay animation";
            pacer.Start();
        }

        void CircleFrame()
        {
            DateTime now = DateTime.UtcNow;
            bool more = false;
            if (ct != ctTo || ctFrom != ctTo)
            {
                float p = (float)Math.Min(1.0, (now - morphStart).TotalMilliseconds / morphMs);
                ct = ctFrom + (ctTo - ctFrom) * Spring(p);
                if (p >= 1f) { ct = ctTo; ctFrom = ctTo; } else more = true;
            }
            if (vis != visTo)
            {
                float p = (float)Math.Min(1.0, (now - visStart).TotalMilliseconds / FadeMs);
                vis = visFrom + (visTo - visFrom) * (p * p * (3f - 2f * p));
                if (p >= 1f) vis = visTo; else more = true;
            }
            bool spin = Spinning();
            spinOnly = !more && spin;
            if (spinOnly && (now - lastSpinFrame).TotalMilliseconds < 25) return;
            lastSpinFrame = now;
            RenderCircle();
            if (!more && !spin) pacing = false;
        }

        // Обложка крутится, только пока музыка играет и кружок на экране.
        bool Spinning()
        {
            return cfg.Spin && shown && vis > 0.001f && hasSession && status == PlaybackStatus.Playing && !circleDrag;
        }

        // Режимы «Показывать»: всегда / пока играет / только при событиях.
        // Раскрытая плашка, мышь над кружком и открытые настройки — кружок виден в любом режиме.
        bool WantVisible()
        {
            if (ctTo > 0f || ct > 0f || circleDown || circleDrag || hovering) return true;
            if (settings != null && !settings.IsDisposed) return true;
            switch (cfg.ShowMode)
            {
                case "playing": return hasSession && status == PlaybackStatus.Playing;
                case "events": return false;
                default: return true;
            }
        }

        void UpdateVisTarget()
        {
            float want = WantVisible() ? 1f : 0f;
            if (want == visTo) return;
            visFrom = vis;
            visTo = want;
            visStart = DateTime.UtcNow;
            StartPacer();
        }

        // Раз в 100 мс: когда свернуть, не пора ли перерисовать прогресс, не перекрыла ли нас игра.
        void CircleTick()
        {
            DateTime now = DateTime.UtcNow;
            bool hintActive = hintText != null && now < hintUntil;
            if (hintActive != hintWasActive) { hintWasActive = hintActive; dirty = true; }

            if (ctTo >= 1f)
            {
                if (hovering || dragging || circleDown)
                {
                    DateTime keep = now.AddSeconds(1.5);
                    if (keep > expandUntil) expandUntil = keep;
                }
                else if (!cfg.AlwaysVisible && now >= expandUntil) MorphTo(0f, CollapseMs);
            }
            else if (cfg.AlwaysVisible && !circleDrag) MorphTo(1f, ExpandMs);

            bool pacerDead = pacer == null || !pacer.IsAlive;
            if ((ct != ctTo || vis != visTo) && pacerDead) StartPacer();
            if (!pacing && Spinning()) StartPacer();
            if (!pacing && (dirty || !shown || visTo != (WantVisible() ? 1f : 0f))) RenderCircle();
            if (shown && (now - lastTopmost).TotalSeconds > 2) { win.KeepTopmost(); lastTopmost = now; }
        }

        Rectangle CircleScreen()
        {
            if (lastD <= 0) return Screen.PrimaryScreen.Bounds;
            return Screen.FromPoint(new Point(ccx + lastD / 2, ccy + lastD / 2)).Bounds;
        }

        void PlaceCircle(int d, float dpi)
        {
            lastD = d;
            if (circleDrag) return;
            if (cfg.CircleX == -1 || cfg.CircleY == -1)
            {
                // своё место не задано — ставим по «Положению» из настроек
                Rectangle wa = Screen.PrimaryScreen.WorkingArea;
                int m = (int)Math.Round(cfg.Margin * dpi);
                switch ((cfg.Position ?? "").ToLowerInvariant())
                {
                    case "topleft": ccx = wa.Left + m; ccy = wa.Top + m; break;
                    case "topcenter": ccx = wa.Left + (wa.Width - d) / 2; ccy = wa.Top + m; break;
                    case "bottomleft": ccx = wa.Left + m; ccy = wa.Bottom - d - m; break;
                    case "bottomcenter": ccx = wa.Left + (wa.Width - d) / 2; ccy = wa.Bottom - d - m; break;
                    case "bottomright": ccx = wa.Right - d - m; ccy = wa.Bottom - d - m; break;
                    default: ccx = wa.Right - d - m; ccy = wa.Top + m; break;
                }
            }
            else { ccx = cfg.CircleX; ccy = cfg.CircleY; }
            // не даём кружку уехать за экран (например, если монитор отключили)
            Rectangle b = Screen.FromPoint(new Point(ccx + d / 2, ccy + d / 2)).Bounds;
            ccx = Math.Max(b.Left, Math.Min(b.Right - d, ccx));
            ccy = Math.Max(b.Top, Math.Min(b.Bottom - d, ccy));
        }

        void RenderCircle()
        {
            if (win == null) return;
            UpdateVisTarget();
            if (vis <= 0.001f && visTo <= 0f)
            {
                // режим «пока играет» / «по событию» — кружок спрятан
                if (shown) { win.Hide(); shown = false; hovering = false; hoverTarget = T_NONE; }
                return;
            }
            ComposeCircle();
            float open = Math.Min(1f, ct * 2.5f);
            int idle = (hovering || circleDown) ? 100 : Math.Max(30, Math.Min(100, cfg.Opacity));
            double a = (idle + (100 - idle) * open) * vis;
            win.Present(frame, frameAt.X, frameAt.Y, (byte)Math.Round(255 * a / 100.0));
            if (!shown) { win.ShowNoActivate(); shown = true; lastTopmost = DateTime.UtcNow; }
        }

        // Собирает кадр (кружок или раскрывающуюся плашку) в frame, положение кадра на экране — в frameAt.
        void ComposeCircle()
        {
            DateTime now = DateTime.UtcNow;
            double dt = (now - spinStamp).TotalSeconds;
            spinStamp = now;
            if (cfg.Spin && hasSession && status == PlaybackStatus.Playing && dt > 0 && dt < 0.5)
                spinAngle = (spinAngle + (float)(dt * SpinDegPerSec)) % 360f;

            Rectangle scrB = CircleScreen();
            float dpi = Native.DpiScaleAt(scrB);
            float s = dpi * (float)cfg.Scale;
            int d = Math.Max(24, (int)Math.Round(CircleBase * s));
            PlaceCircle(d, dpi);

            var circle = new Rectangle(ccx, ccy, d, d);
            Rectangle wa = Screen.FromPoint(new Point(ccx + d / 2, ccy + d / 2)).WorkingArea;
            bool onRight = ccx + d / 2 > wa.Left + wa.Width / 2;   // кружок справа — плашка растёт влево
            int W = (int)Math.Round(BaseW * s), H = (int)Math.Round(BaseH * s);
            int hm = (H - d) / 2;
            int cardX = onRight ? circle.Right + hm - W : circle.Left - hm;
            int cardY = circle.Top + d / 2 - H / 2;
            cardX = Math.Max(wa.Left, Math.Min(wa.Right - W, cardX));
            cardY = Math.Max(wa.Top, Math.Min(wa.Bottom - H, cardY));
            cardX = Math.Max(circle.Right + 2 - W, Math.Min(circle.Left - 2, cardX));   // кружок всегда внутри плашки
            cardY = Math.Max(circle.Bottom + 2 - H, Math.Min(circle.Top - 2, cardY));
            var card = new Rectangle(cardX, cardY, W, H);

            int pad = (int)Math.Ceiling(d * 0.24f);
            bool open = ct > 0.001f || ctTo > 0f;
            Rectangle bounds = Rectangle.Inflate(circle, pad, pad);
            // на время раскрытия кадр сразу размером с плашку — окно не пересоздаётся каждый кадр
            if (open) bounds = Rectangle.Union(bounds, Rectangle.Inflate(card, (int)Math.Ceiling(14 * s), (int)Math.Ceiling(14 * s)));
            frameAt = bounds.Location;
            circleOffX = circle.X - bounds.X; circleOffY = circle.Y - bounds.Y;
            cardOffX = card.X - bounds.X; cardOffY = card.Y - bounds.Y;

            if (frame == null || frame.Width != bounds.Width || frame.Height != bounds.Height)
            {
                if (frame != null) frame.Dispose();
                frame = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
            }

            bool controls = hovering && cfg.MouseControls && ct >= 0.98f;
            if (open)
            {
                string key = W + "|" + H + "|" + (circle.X - card.X) + "|" + onRight + "|" + controls + "|" + hoverTarget;
                if (cardBmp == null || dirty || key != cardKey)
                {
                    RenderCardBitmap(W, H, s, circle.X - card.X, d, onRight, controls);
                    cardKey = key;
                    cardVersion++;
                }
            }
            dirty = false;

            using (Graphics g = Graphics.FromImage(frame))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.Clear(Color.Transparent);
                g.CompositingMode = CompositingMode.SourceOver;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;

                var c = new RectangleF(circleOffX, circleOffY, d, d);
                if (!open || ct <= 0.001f)
                {
                    // свёрнуто: только кружок с тенью
                    for (int i = 5; i >= 1; i--)
                        using (var sb = new SolidBrush(Color.FromArgb(12, 0, 0, 0)))
                            g.FillEllipse(sb, RectangleF.Inflate(new RectangleF(c.X, c.Y + d * 0.05f, d, d), i * d * 0.018f, i * d * 0.018f));
                    DrawCircleFace(g, c, s, hoverTarget == T_CIRCLE);
                    return;
                }

                // Плашка раскрыта и стоит на месте: неподвижная часть (тень, плашка) берётся из кэша,
                // заново рисуется только кружок — так вращение обложки почти ничего не стоит.
                bool still = ct >= 0.999f && ctTo >= 1f;
                string baseKey = cardVersion + "|" + frame.Width + "x" + frame.Height + "|" + cardOffX + "," + cardOffY;
                if (still && panelBase != null && panelBaseKey == baseKey)
                {
                    g.CompositingMode = CompositingMode.SourceCopy;
                    g.DrawImageUnscaled(panelBase, 0, 0);
                    g.CompositingMode = CompositingMode.SourceOver;
                    using (var under = new SolidBrush(Color.FromArgb(245, 26, 26, 33)))
                        g.FillEllipse(under, RectangleF.Inflate(c, -0.5f, -0.5f));
                    DrawCircleFace(g, c, s, hoverTarget == T_CIRCLE);
                    return;
                }

                // раскрытие: прямоугольник растёт от кружка до плашки, текст выезжает вместе с растущим краем
                var f = new RectangleF(cardOffX, cardOffY, W, H);
                float t = ct;
                var r = new RectangleF(Lerp(c.X, f.X, t), Lerp(c.Y, f.Y, t), Lerp(c.Width, f.Width, t), Lerp(c.Height, f.Height, t));
                float rad = Lerp(d / 2f, CardRadius * s, t);
                for (int i = 6; i >= 1; i--)
                {
                    float k = i * 1.7f * s;
                    using (GraphicsPath sp = Gfx.RoundRect(RectangleF.Inflate(new RectangleF(r.X, r.Y + 2.5f * s, r.Width, r.Height), k, k), rad + k))
                    using (var sb = new SolidBrush(Color.FromArgb(11, 0, 0, 0)))
                        g.FillPath(sb, sp);
                }
                using (GraphicsPath p = Gfx.RoundRect(r, rad))
                {
                    using (var tb = new TextureBrush(cardBmp, WrapMode.Clamp))
                    {
                        tb.TranslateTransform(onRight ? r.X : r.Right - f.Width, r.Y + (r.Height - f.Height) / 2f);
                        g.FillPath(tb, p);
                    }
                    using (var pen = new Pen(Color.FromArgb((int)(38 * Math.Min(1f, t * 1.5f)), 255, 255, 255), Math.Max(1f, s)))
                        g.DrawPath(pen, p);
                }
                if (still)
                {
                    g.Flush();
                    if (panelBase != null) panelBase.Dispose();
                    panelBase = (Bitmap)frame.Clone();
                    panelBaseKey = baseKey;
                }
                // кружок с обложкой остаётся на месте поверх плашки
                using (var under = new SolidBrush(Color.FromArgb(245, 26, 26, 33)))
                    g.FillEllipse(under, RectangleF.Inflate(c, -0.5f, -0.5f));
                DrawCircleFace(g, c, s, hoverTarget == T_CIRCLE);
            }
        }

        // Содержимое плашки для вида «кружок»: та же плашка, только вместо квадратной обложки — место под кружок.
        void RenderCardBitmap(int W, int H, float s, int circleX, int d, bool onRight, bool controls)
        {
            if (cardBmp == null || cardBmp.Width != W || cardBmp.Height != H)
            {
                if (cardBmp != null) cardBmp.Dispose();
                cardBmp = new Bitmap(W, H, PixelFormat.Format32bppArgb);
            }
            using (Graphics g = Graphics.FromImage(cardBmp))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                using (var bg = new LinearGradientBrush(new RectangleF(0, 0, W, H),
                    Color.FromArgb(245, 34, 34, 43), Color.FromArgb(245, 17, 17, 23), 90f))
                    g.FillRectangle(bg, 0, 0, W, H);
                g.CompositingMode = CompositingMode.SourceOver;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                float tx = onRight ? 16 * s : circleX + d + 14 * s;
                float tr = onRight ? circleX - 14 * s : W - 16 * s;
                DrawCardContent(g, s, H, tx, tr, controls);
            }
        }

        // Круглая обложка (пока играет музыка — медленно крутится), затемнение на паузе и кольцо прогресса по краю.
        void DrawCircleFace(Graphics g, RectangleF c, float s, bool hot)
        {
            int d = Math.Max(1, (int)Math.Round(c.Width));
            string key = thumbVersion + "|" + d + "|" + (thumb != null);
            if (faceBmp == null || faceKey != key)
            {
                if (faceBmp != null) faceBmp.Dispose();
                faceBmp = new Bitmap(d, d, PixelFormat.Format32bppArgb);
                using (Graphics fg = Graphics.FromImage(faceBmp))
                {
                    fg.Clear(Color.Transparent);
                    fg.SmoothingMode = SmoothingMode.AntiAlias;
                    fg.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    fg.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    var r = new RectangleF(0, 0, d, d);
                    if (thumb != null) Gfx.FillImageEllipse(fg, thumb, r);
                    else
                    {
                        using (var b = new LinearGradientBrush(r, Color.FromArgb(255, 124, 92, 255), Color.FromArgb(255, 32, 201, 151), 45f))
                            fg.FillEllipse(b, r);
                        Gfx.DrawNote(fg, RectangleF.Inflate(r, -d * 0.27f, -d * 0.27f), Color.FromArgb(235, 255, 255, 255));
                    }
                }
                faceKey = key;
            }
            if (cfg.Spin && spinAngle != 0f)
            {
                GraphicsState st = g.Save();
                g.TranslateTransform(c.X + c.Width / 2f, c.Y + c.Height / 2f);
                g.RotateTransform(spinAngle);
                g.DrawImage(faceBmp, -c.Width / 2f, -c.Height / 2f, c.Width, c.Height);
                g.Restore(st);
            }
            else g.DrawImage(faceBmp, c);

            bool playing = hasSession && status == PlaybackStatus.Playing;
            if (hasSession && !playing)
            {
                using (var dim = new SolidBrush(Color.FromArgb(110, 0, 0, 0))) g.FillEllipse(dim, c);
                float gl = c.Width * 0.28f;
                Gfx.DrawGlyph(g, "pause", new RectangleF(c.X + (c.Width - gl) / 2, c.Y + (c.Height - gl) / 2, gl, gl),
                    Color.FromArgb(235, 255, 255, 255));
            }

            float pw = Math.Max(2f, 2.6f * s);
            var ring = RectangleF.Inflate(c, -pw / 2, -pw / 2);
            if (hasSession && hasTimeline && tlDur.TotalMilliseconds > 0)
            {
                float frac = (float)Math.Max(0, Math.Min(1, (dragging ? DragPos() : CurrentPos()).TotalMilliseconds / tlDur.TotalMilliseconds));
                using (var tp = new Pen(Color.FromArgb(hot ? 120 : 70, 255, 255, 255), pw)) g.DrawEllipse(tp, ring);
                if (frac > 0.003f)
                    using (var ap = new Pen(playing ? Accent : Muted, pw))
                    {
                        ap.StartCap = LineCap.Round;
                        ap.EndCap = LineCap.Round;
                        g.DrawArc(ap, ring, -90f, 360f * frac);
                    }
            }
            else
                using (var bp = new Pen(Color.FromArgb(hot ? 170 : 80, 255, 255, 255), Math.Max(1f, c.Width * 0.03f)))
                    g.DrawEllipse(bp, RectangleF.Inflate(c, -c.Width * 0.015f, -c.Width * 0.015f));
        }

        // Правый щелчок по кружку.
        void ShowCircleMenu()
        {
            if (circleMenu != null) circleMenu.Dispose();
            var m = new ContextMenuStrip();
            m.Renderer = new ToolStripProfessionalRenderer(new DarkMenuColors());
            m.BackColor = Theme.Bar;
            m.ForeColor = Theme.Text;
            m.ShowImageMargin = false;
            m.ShowCheckMargin = true;
            Action<string, bool, Action> add = (text, check, act) =>
            {
                var it = new ToolStripMenuItem(text);
                it.ForeColor = Theme.Text;
                it.Checked = check;
                it.Click += (s, e) => act();
                m.Items.Add(it);
            };
            add(L.T("Развернуть", "Expand"), false, () => Expand(cfg.ShowSeconds));
            add(L.T("Не сворачивать плашку", "Keep the panel open"), cfg.AlwaysVisible, () => SetAlways(!cfg.AlwaysVisible));
            add(L.T("Вернуть кружок на место", "Put the bubble back"), false, PutBubbleBack);
            m.Items.Add(new ToolStripSeparator());
            add(L.T("Настройки…", "Settings…"), false, OpenSettingsWindow);
            add(L.T("Выход", "Exit"), false, ExitApp);
            circleMenu = m;
            m.Show(Cursor.Position);
        }

        static void DrawRoundButton(Graphics g, RectangleF r, bool hot, bool enabled, string glyph, float s)
        {
            if (hot && enabled)
                using (var hb = new SolidBrush(Color.FromArgb(45, 255, 255, 255)))
                    g.FillEllipse(hb, r);
            float gl = 10 * s;
            Gfx.DrawGlyph(g, glyph, new RectangleF(r.X + (r.Width - gl) / 2, r.Y + (r.Height - gl) / 2, gl, gl),
                enabled ? Color.White : Color.FromArgb(80, 255, 255, 255));
        }

        TimeSpan DragPos() { return TimeSpan.FromMilliseconds(tlDur.TotalMilliseconds * dragFrac); }

        // ---------------------------------------------------------- мышь на оверлее

        int HitTest(int x, int y)
        {
            if (!cfg.MouseControls) return T_NONE;
            float r = lastD / 2f;
            float dx = x - (circleOffX + r), dy = y - (circleOffY + r);
            if (dx * dx + dy * dy <= r * r) return T_CIRCLE;
            if (ct < 0.98f) return T_NONE;      // кнопки есть только у раскрытой плашки
            var p = new PointF(x - cardOffX, y - cardOffY);
            float m = 3f * lastD / CircleBase;
            if (RectangleF.Inflate(rGear, m, m).Contains(p)) return T_GEAR;
            if (RectangleF.Inflate(rPrev, m, m).Contains(p)) return T_PREV;
            if (RectangleF.Inflate(rPlay, m, m).Contains(p)) return T_PLAY;
            if (RectangleF.Inflate(rNext, m, m).Contains(p)) return T_NEXT;
            if (hasSession && hasTimeline && rBarHit.Contains(p)) return T_BAR;
            return T_NONE;
        }

        float FracAt(int x) { return Math.Max(0f, Math.Min(1f, (x - cardOffX - barX0) / Math.Max(1f, barX1 - barX0))); }

        bool WantsHand()
        {
            if (hoverTarget == T_NONE) return false;
            if (hoverTarget == T_CIRCLE) return !circleDrag;
            if (hoverTarget == T_BAR) return canSeek;
            if (hoverTarget == T_PREV) return canPrev;
            if (hoverTarget == T_NEXT) return canNext;
            return true;
        }

        void OnOverlayMouseMove(int x, int y)
        {
            if (!cfg.MouseControls) return;
            if (circleDown)
            {
                // кружок тащат — он едет за мышью; карточку на это время сворачиваем
                Point p = Cursor.Position;
                int ddx = p.X - downCursor.X, ddy = p.Y - downCursor.Y;
                if (!circleDrag && ddx * ddx + ddy * ddy > 25)
                {
                    circleDrag = true;
                    ct = ctFrom = ctTo = 0f;
                    pacing = false;
                }
                if (circleDrag) { ccx = downCx + ddx; ccy = downCy + ddy; RenderCircle(); }
                return;
            }
            bool changed = false;
            if (!hovering)
            {
                hovering = true;
                changed = true;
            }
            if (dragging) { dragFrac = FracAt(x); changed = true; }
            int t = HitTest(x, y);
            if (t != hoverTarget) { hoverTarget = t; changed = true; }
            if (changed) RedrawNow();
        }

        void OnOverlayMouseLeave()
        {
            if (!hovering) return;
            hovering = false;
            hoverTarget = T_NONE;
            DateTime keep = DateTime.UtcNow.AddSeconds(1.5);
            if (ctTo >= 1f && keep > expandUntil) expandUntil = keep;
            RedrawNow();
        }

        void OnOverlayMouseDown(int x, int y)
        {
            pressTarget = HitTest(x, y);
            if (pressTarget == T_CIRCLE)
            {
                circleDown = true;
                circleDrag = false;
                downCursor = Cursor.Position;
                downCx = ccx;
                downCy = ccy;
                win.CaptureMouse();
                return;
            }
            if (pressTarget == T_BAR && canSeek)
            {
                dragging = true;
                dragFrac = FracAt(x);
                win.CaptureMouse();
                RedrawNow();
            }
        }

        void OnOverlayMouseUp(int x, int y)
        {
            if (circleDown)
            {
                // флаги сбрасываем до ReleaseCapture: Windows сразу пришлёт WM_CAPTURECHANGED
                bool wasDrag = circleDrag;
                circleDown = false;
                circleDrag = false;
                pressTarget = T_NONE;
                win.ReleaseMouse();
                if (wasDrag)
                {
                    cfg.CircleX = ccx;           // сначала запоминаем, куда утащили,
                    cfg.CircleY = ccy;
                    RenderCircle();              // потом подтягиваем внутрь экрана
                    cfg.CircleX = ccx;
                    cfg.CircleY = ccy;
                    SaveConfig();
                }
                else if (ctTo >= 1f)
                {
                    if (cfg.AlwaysVisible) { cfg.AlwaysVisible = false; SaveConfig(); SyncMenu(); }
                    MorphTo(0f, CollapseMs);
                }
                else Expand(cfg.ShowSeconds);
                return;
            }
            if (dragging)
            {
                dragging = false;
                float f = FracAt(x);
                win.ReleaseMouse();
                SeekToFrac(f);
                RedrawNow();
                return;
            }
            int t = HitTest(x, y);
            if (t != T_NONE && t == pressTarget)
            {
                switch (t)
                {
                    case T_PREV: if (canPrev) Skip(false); break;
                    case T_PLAY: PlayPause(); break;
                    case T_NEXT: if (canNext) Skip(true); break;
                    case T_GEAR: OpenSettingsWindow(); break;
                    case T_BAR: ShowHint(L.T("Этот плеер не даёт перематывать", "This player doesn't allow seeking"), "stop"); break;
                }
            }
            pressTarget = T_NONE;
        }

        void OnOverlayCaptureLost()
        {
            if (circleDown) { circleDown = false; circleDrag = false; }
            if (!dragging) return;
            dragging = false;
            RedrawNow();
        }

        async void SeekToFrac(float f)
        {
            if (!hasTimeline) return;
            TimeSpan target = TimeSpan.FromMilliseconds(tlDur.TotalMilliseconds * f);
            TimeSpan cur = CurrentPos();
            MediaSession s = PickSession();
            if (s == null) return;
            bool ok = false;
            try
            {
                var tl = s.GetTimelineProperties();
                ok = await s.TryChangePlaybackPositionAsync((tl.StartTime + target).Ticks);
            }
            catch (Exception ex) { Log.Write("перемотка: " + ex.Message); }
            if (ok) { tlPos = target; tlStampUtc = DateTime.UtcNow; }
            ShowHint(ok ? L.T("Перемотка на ", "Seek to ") + Fmt(target) : L.T("Плеер не дал перемотать", "The player refused to seek"),
                target >= cur ? "ff" : "rew");
            RefreshSoon();
        }

        // Сразу перерисовать (отклик на мышь не должен ждать таймера).
        void RedrawNow()
        {
            dirty = true;
            if (!pacing) RenderCircle();
        }

        void ApplyLanguage()
        {
            bool was = L.En;
            L.Apply(cfg.Language);
            if (was == L.En) return;
            if (tray != null)
            {
                ContextMenu old = tray.ContextMenu;
                tray.ContextMenu = BuildMenu();
                if (old != null) old.Dispose();
            }
            UpdateTrayText();
            dirty = true;
        }

        void ApplyInteractivity()
        {
            if (win == null) return;
            win.SetClickThrough(!cfg.MouseControls);
            if (!cfg.MouseControls && (hovering || dragging))
            {
                hovering = false; dragging = false; hoverTarget = T_NONE;
                win.ReleaseMouse();
                dirty = true;
            }
        }

        static string Fmt(TimeSpan t)
        {
            if (t.TotalHours >= 1) return string.Format("{0}:{1:00}:{2:00}", (int)t.TotalHours, t.Minutes, t.Seconds);
            return string.Format("{0}:{1:00}", (int)t.TotalMinutes, t.Seconds);
        }

        static string StatusText(PlaybackStatus st)
        {
            switch (st)
            {
                case PlaybackStatus.Playing: return L.T("играет", "playing");
                case PlaybackStatus.Paused: return L.T("пауза", "paused");
                case PlaybackStatus.Stopped: return L.T("остановлено", "stopped");
                case PlaybackStatus.Changing: return L.T("переключается…", "switching…");
                default: return L.T("ожидание", "idle");
            }
        }

        static string PrettyApp(string id)
        {
            if (string.IsNullOrEmpty(id)) return L.T("Плеер", "Player");
            string l = id.ToLowerInvariant();
            if (l.Contains("spotify")) return "Spotify";
            if (l.Contains("yandex") && l.Contains("music")) return L.T("Яндекс Музыка", "Yandex Music");
            if (l.Contains("yandex")) return L.T("Яндекс Браузер", "Yandex Browser");
            if (l.Contains("chrome")) return "Chrome";
            if (l.Contains("msedge") || l.Contains("microsoftedge")) return "Edge";
            if (l.Contains("firefox") || l.Contains("308046b0af4a39cb")) return "Firefox";
            if (l.Contains("opera")) return "Opera";
            if (l.Contains("brave")) return "Brave";
            if (l.Contains("vivaldi")) return "Vivaldi";
            if (l.Contains("zunemusic") || l.Contains("mediaplayer")) return L.T("Медиаплеер", "Media Player");
            if (l.Contains("applemusic")) return "Apple Music";
            if (l.Contains("aimp")) return "AIMP";
            if (l.Contains("foobar")) return "foobar2000";
            if (l.Contains("vlc")) return "VLC";
            if (l.Contains("telegram")) return "Telegram";
            if (l.Contains("discord")) return "Discord";
            if (l.Contains("deezer")) return "Deezer";
            if (l.Contains("tidal")) return "TIDAL";
            if (l.Contains("soundcloud")) return "SoundCloud";
            string s = id;
            int bang = s.LastIndexOf('!');
            if (bang >= 0 && bang < s.Length - 1) s = s.Substring(bang + 1);
            int slash = s.LastIndexOfAny(new[] { '\\', '/' });
            if (slash >= 0) s = s.Substring(slash + 1);
            if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) s = s.Substring(0, s.Length - 4);
            if (s.Length > 24) s = s.Substring(0, 24) + "…";
            return s;
        }

        // ---------------------------------------------------------- трей и настройки

        ContextMenu BuildMenu()
        {
            var m = new ContextMenu();
            var miSettings = new MenuItem(L.T("Настройки…", "Settings…"), (s, e) => OpenSettingsWindow());
            miSettings.DefaultItem = true;
            m.MenuItems.Add(miSettings);
            m.MenuItems.Add(new MenuItem(L.T("Развернуть", "Expand"), (s, e) => Flash()));
            miAlways = new MenuItem(L.T("Не сворачивать плашку", "Keep the panel open"), (s, e) => SetAlways(!cfg.AlwaysVisible));
            m.MenuItems.Add(miAlways);
            m.MenuItems.Add(new MenuItem(L.T("Вернуть кружок на место", "Put the bubble back"), (s, e) => PutBubbleBack()));
            m.MenuItems.Add("-");
            miAutostart = new MenuItem(L.T("Запускать вместе с Windows", "Start with Windows"), (s, e) => ToggleAutostart());
            m.MenuItems.Add(miAutostart);
            m.MenuItems.Add("-");
            m.MenuItems.Add(new MenuItem(L.T("Выход", "Exit"), (s, e) => ExitApp()));
            m.Popup += (s, e) => SyncMenu();
            SyncMenu();
            return m;
        }

        void SyncMenu()
        {
            miAlways.Checked = cfg.AlwaysVisible;
            miAutostart.Checked = Autostart.IsOn();
        }

        void SetAlways(bool on)
        {
            cfg.AlwaysVisible = on;
            SaveConfig();
            SyncMenu();
            ShowHint(on ? L.T("Плашка не будет сворачиваться", "The panel stays open")
                        : L.T("Плашка снова сворачивается сама", "The panel folds on its own again"), "pin");
        }

        // Кружок снова в правом верхнем углу (если его утащили или потеряли за краем).
        void PutBubbleBack()
        {
            ResetCirclePlace();
            SaveConfig();
            Flash();
        }

        void ToggleAutostart()
        {
            try { Autostart.Set(!Autostart.IsOn()); }
            catch (Exception ex) { MessageBox.Show(L.T("Не удалось изменить автозапуск: ", "Couldn't change autostart: ") + ex.Message, "Melody Overlay"); }
            SyncMenu();
        }

        public void OpenSettingsFile()
        {
            try
            {
                if (!File.Exists(Config.PathIni)) SaveConfig();
                Process.Start("notepad.exe", "\"" + Config.PathIni + "\"");
            }
            catch (Exception ex) { MessageBox.Show(L.T("Не удалось открыть настройки: ", "Couldn't open settings: ") + ex.Message, "Melody Overlay"); }
        }

        void SaveConfig()
        {
            lastSelfSave = DateTime.UtcNow;
            try { cfg.Save(); } catch (Exception ex) { Log.Write("сохранение настроек: " + ex.Message); }
        }

        void WatchConfig()
        {
            try
            {
                watcher = new FileSystemWatcher(Path.GetDirectoryName(Config.PathIni), Path.GetFileName(Config.PathIni));
                watcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName;
                FileSystemEventHandler h = (s, e) => ui.Post(_ =>
                {
                    if ((DateTime.UtcNow - lastSelfSave).TotalSeconds < 1.5) return;
                    reloadTimer.Stop();
                    reloadTimer.Start();
                }, null);
                watcher.Changed += h;
                watcher.Created += h;
                watcher.Renamed += (s, e) => h(s, e);
                watcher.EnableRaisingEvents = true;
            }
            catch (Exception ex) { Log.Write("слежение за настройками: " + ex.Message); }
        }

        void ReloadConfig()
        {
            try { cfg = Config.Load(); }
            catch (Exception ex) { Log.Write("чтение настроек: " + ex.Message); return; }
            RegisterHotkeys(false);
            ApplyInteractivity();
            ApplyLanguage();
            SyncMenu();
            ShowHint(L.T("Настройки применены", "Settings applied"), "check");
        }

        void UpdateTrayText()
        {
            if (tray == null) return;
            string txt = hasSession && title.Length > 0
                ? (artist.Length > 0 ? artist + " — " + title : title)
                : L.T("Melody Overlay — ничего не играет", "Melody Overlay — nothing playing");
            if (txt.Length > 63) txt = txt.Substring(0, 62) + "…";
            if (tray.Text != txt) tray.Text = txt;
        }

        static Icon MakeTrayIcon(out IntPtr handle)
        {
            int sz = Math.Max(16, SystemInformation.SmallIconSize.Width);
            using (Bitmap b = IconArt.Draw(sz))
            {
                handle = b.GetHicon();
                return Icon.FromHandle(handle);
            }
        }

        void ExitApp()
        {
            if (settings != null && !settings.IsDisposed) settings.Close();
            pacing = false;
            if (circleMenu != null) circleMenu.Dispose();
            pollTimer.Stop();
            animTimer.Stop();
            if (win != null)
            {
                for (int i = 1; i <= 6; i++) Native.UnregisterHotKey(win.Handle, i);
                win.Dispose();
            }
            if (watcher != null) watcher.Dispose();
            if (tray != null) { tray.Visible = false; tray.Dispose(); }
            if (trayIconHandle != IntPtr.Zero) Native.DestroyIcon(trayIconHandle);
            ExitThread();
        }
    }

    // ------------------------------------------------------------------ await для WinRT

    // Свой мост IAsyncOperation -> Task, чтобы не зависеть от Windows SDK.
    static class WinRtAsync
    {
        public static Task<T> AsTask<T>(Windows.Foundation.IAsyncOperation<T> op)
        {
            var tcs = new TaskCompletionSource<T>();
            op.Completed = (info, st) =>
            {
                try
                {
                    if (st == Windows.Foundation.AsyncStatus.Completed) tcs.TrySetResult(info.GetResults());
                    else if (st == Windows.Foundation.AsyncStatus.Canceled) tcs.TrySetCanceled();
                    else tcs.TrySetException(info.ErrorCode ?? new Exception("WinRT: операция не выполнена"));
                }
                catch (Exception ex) { tcs.TrySetException(ex); }
            };
            return tcs.Task;
        }

        public static TaskAwaiter<T> GetAwaiter<T>(this Windows.Foundation.IAsyncOperation<T> op)
        {
            return AsTask(op).GetAwaiter();
        }
    }

    // ------------------------------------------------------------------ окно-оверлей

    // Прозрачное окно поверх всех: не попадает в Alt+Tab и панель задач,
    // никогда не получает фокус и пропускает мышь сквозь себя.
    class OverlayWindow : NativeWindow, IDisposable
    {
        public event Action<int> HotkeyPressed;

        public OverlayWindow()
        {
            var cp = new CreateParams();
            cp.Caption = "Melody Overlay";
            cp.Style = Native.WS_POPUP;
            cp.ExStyle = Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW
                       | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST;
            cp.X = -32000; cp.Y = -32000; cp.Width = 1; cp.Height = 1;
            CreateHandle(cp);
        }

        public event Action<int, int> MouseMoved, LeftDown, LeftUp;
        public event Action RightUp;
        public event Action MouseLeft, CaptureLost;
        public Func<bool> WantsHandCursor;
        bool tracking;

        protected override void WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                case Native.WM_HOTKEY:
                {
                    var h = HotkeyPressed;
                    if (h != null) h(m.WParam.ToInt32());
                    return;
                }
                case Native.WM_MOUSEACTIVATE:
                    m.Result = (IntPtr)Native.MA_NOACTIVATE;   // клик не отнимает фокус у игры
                    return;
                case Native.WM_MOUSEMOVE:
                {
                    if (!tracking)
                    {
                        var tme = new Native.TRACKMOUSEEVENT();
                        tme.cbSize = Marshal.SizeOf(typeof(Native.TRACKMOUSEEVENT));
                        tme.dwFlags = Native.TME_LEAVE;
                        tme.hwndTrack = Handle;
                        tracking = Native.TrackMouseEvent(ref tme);
                    }
                    var h = MouseMoved;
                    if (h != null) h(LoX(m.LParam), HiY(m.LParam));
                    return;
                }
                case Native.WM_MOUSELEAVE:
                {
                    tracking = false;
                    var h = MouseLeft;
                    if (h != null) h();
                    return;
                }
                case Native.WM_LBUTTONDOWN:
                {
                    var h = LeftDown;
                    if (h != null) h(LoX(m.LParam), HiY(m.LParam));
                    return;
                }
                case Native.WM_LBUTTONUP:
                {
                    var h = LeftUp;
                    if (h != null) h(LoX(m.LParam), HiY(m.LParam));
                    return;
                }
                case Native.WM_RBUTTONUP:
                {
                    var h = RightUp;
                    if (h != null) h();
                    return;
                }
                case Native.WM_CAPTURECHANGED:
                {
                    var h = CaptureLost;
                    if (h != null) h();
                    break;
                }
                case Native.WM_SETCURSOR:
                    if ((m.LParam.ToInt64() & 0xFFFF) == Native.HTCLIENT)
                    {
                        var f = WantsHandCursor;
                        bool hand = f != null && f();
                        Native.SetCursor(Native.LoadCursor(IntPtr.Zero, hand ? Native.IDC_HAND : Native.IDC_ARROW));
                        m.Result = (IntPtr)1;
                        return;
                    }
                    break;
            }
            base.WndProc(ref m);
        }

        static int LoX(IntPtr lp) { return (short)(lp.ToInt64() & 0xFFFF); }
        static int HiY(IntPtr lp) { return (short)((lp.ToInt64() >> 16) & 0xFFFF); }

        // Без кнопок оверлей пропускает мышь сквозь себя, с кнопками — ловит её.
        public void SetClickThrough(bool on)
        {
            int ex = Native.GetWindowLong(Handle, Native.GWL_EXSTYLE);
            int nex = on ? (ex | Native.WS_EX_TRANSPARENT) : (ex & ~Native.WS_EX_TRANSPARENT);
            if (nex != ex) Native.SetWindowLong(Handle, Native.GWL_EXSTYLE, nex);
        }

        public void CaptureMouse() { Native.SetCapture(Handle); }
        public void ReleaseMouse() { if (Native.GetCapture() == Handle) Native.ReleaseCapture(); }

        public void Present(Bitmap bmp, int x, int y, byte alpha)
        {
            IntPtr screenDc = Native.GetDC(IntPtr.Zero);
            IntPtr memDc = Native.CreateCompatibleDC(screenDc);
            IntPtr hBmp = IntPtr.Zero, old = IntPtr.Zero;
            try
            {
                hBmp = bmp.GetHbitmap(Color.FromArgb(0));
                old = Native.SelectObject(memDc, hBmp);
                var size = new Native.SIZE(bmp.Width, bmp.Height);
                var src = new Native.POINT(0, 0);
                var dst = new Native.POINT(x, y);
                var blend = new Native.BLENDFUNCTION();
                blend.BlendOp = Native.AC_SRC_OVER;
                blend.SourceConstantAlpha = alpha;
                blend.AlphaFormat = Native.AC_SRC_ALPHA;
                Native.UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, Native.ULW_ALPHA);
            }
            finally
            {
                if (old != IntPtr.Zero) Native.SelectObject(memDc, old);
                if (hBmp != IntPtr.Zero) Native.DeleteObject(hBmp);
                Native.DeleteDC(memDc);
                Native.ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        public void ShowNoActivate()
        {
            Native.ShowWindow(Handle, Native.SW_SHOWNOACTIVATE);
            KeepTopmost();
        }

        public void KeepTopmost()
        {
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }

        public void Hide() { Native.ShowWindow(Handle, Native.SW_HIDE); }

        public void Dispose() { DestroyHandle(); }
    }

    // ------------------------------------------------------------------ настройки

    class Config
    {
        public string PlayPause = "Ctrl+Alt+P";
        public string Next = "Ctrl+Alt+Right";
        public string Previous = "Ctrl+Alt+Left";
        public string VolumeUp = "Ctrl+Alt+Up";
        public string VolumeDown = "Ctrl+Alt+Down";
        public string ToggleOverlay = "Ctrl+Alt+O";
        public string Position = "TopRight";
        public int Margin = 24;
        public double Scale = 1.0;
        public int ShowSeconds = 3;
        public int Opacity = 100;
        public bool ShowOnTrackChange = true;
        public bool AlwaysVisible = false;
        public bool MouseControls = true;
        public string Language = "auto";
        public int CircleX = -1, CircleY = -1; // куда перетащили кружок (-1 — по «Положению»)
        public string ShowMode = "always";     // always — всегда, playing — пока играет музыка, events — только при событиях
        public bool Spin = true;               // обложка в кружке крутится, пока играет музыка

        public Config Clone() { return (Config)MemberwiseClone(); }

        // Всё, кроме горячих клавиш.
        public void CopyLookFrom(Config o)
        {
            Position = o.Position;
            Margin = o.Margin;
            Scale = o.Scale;
            ShowSeconds = o.ShowSeconds;
            Opacity = o.Opacity;
            ShowOnTrackChange = o.ShowOnTrackChange;
            AlwaysVisible = o.AlwaysVisible;
            MouseControls = o.MouseControls;
            Language = o.Language;
            ShowMode = o.ShowMode;
            Spin = o.Spin;
        }

        // Программа раньше называлась Music Overlay — забираем её настройки.
        public static void MigrateOld()
        {
            string old = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "MusicOverlay.ini");
            try { if (!File.Exists(PathIni) && File.Exists(old)) File.Move(old, PathIni); } catch { }
        }

        public static string PathIni
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "MelodyOverlay.ini"); }
        }

        public static Config Load()
        {
            var c = new Config();
            if (!File.Exists(PathIni)) { try { c.Save(); } catch { } return c; }

            foreach (string raw in File.ReadAllLines(PathIni, Encoding.UTF8))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == ';' || line[0] == '#' || line[0] == '[') continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string k = line.Substring(0, eq).Trim().ToLowerInvariant();
                string v = line.Substring(eq + 1).Trim();
                int cm = v.IndexOf(" ;");
                if (cm >= 0) v = v.Substring(0, cm).Trim();
                int n; double d;
                switch (k)
                {
                    case "playpause": c.PlayPause = v; break;
                    case "next": c.Next = v; break;
                    case "previous": c.Previous = v; break;
                    case "volumeup": c.VolumeUp = v; break;
                    case "volumedown": c.VolumeDown = v; break;
                    case "toggleoverlay": c.ToggleOverlay = v; break;
                    case "position": c.Position = v; break;
                    case "margin": if (int.TryParse(v, out n)) c.Margin = Math.Max(0, Math.Min(500, n)); break;
                    case "scale":
                        if (double.TryParse(v.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                            c.Scale = Math.Max(0.5, Math.Min(3.0, d));
                        break;
                    case "showseconds": if (int.TryParse(v, out n)) c.ShowSeconds = Math.Max(1, Math.Min(60, n)); break;
                    case "opacity": if (int.TryParse(v.TrimEnd('%', ' '), out n)) c.Opacity = Math.Max(30, Math.Min(100, n)); break;
                    case "showontrackchange": c.ShowOnTrackChange = ParseBool(v, c.ShowOnTrackChange); break;
                    case "alwaysvisible": c.AlwaysVisible = ParseBool(v, c.AlwaysVisible); break;
                    case "mousecontrols": c.MouseControls = ParseBool(v, c.MouseControls); break;
                    case "showmode":
                    {
                        string sm = v.Trim().ToLowerInvariant();
                        c.ShowMode = sm == "playing" || sm == "events" ? sm : "always";
                        break;
                    }
                    case "spin": c.Spin = ParseBool(v, c.Spin); break;
                    case "circlex": if (int.TryParse(v, out n)) c.CircleX = n; break;
                    case "circley": if (int.TryParse(v, out n)) c.CircleY = n; break;
                    case "language":
                    {
                        string lv = v.Trim().ToLowerInvariant();
                        c.Language = lv == "ru" || lv == "en" ? lv : "auto";
                        break;
                    }
                }
            }
            return c;
        }

        static bool ParseBool(string v, bool def)
        {
            switch (v.Trim().ToLowerInvariant())
            {
                case "1": case "true": case "yes": case "on": case "да": return true;
                case "0": case "false": case "no": case "off": case "нет": return false;
                default: return def;
            }
        }

        public void Save()
        {
            var sb = new StringBuilder();
            sb.AppendLine(L.T("; Melody Overlay — настройки. После сохранения файла применяются сами.",
                              "; Melody Overlay settings. Changes apply automatically after you save this file."));
            sb.AppendLine(";");
            sb.AppendLine(L.T("; Горячие клавиши: модификаторы Ctrl, Alt, Shift, Win + одна клавиша, например Ctrl+Shift+F9.",
                              "; Hotkeys: modifiers Ctrl, Alt, Shift, Win + one key, e.g. Ctrl+Shift+F9."));
            sb.AppendLine(L.T("; Клавиши: ", "; Keys: ") + "A-Z, 0-9, F1-F24, Space, Left, Right, Up, Down, Home, End, PageUp, PageDown,");
            sb.AppendLine(";          Insert, Delete, Pause, Num0-Num9, NumPlus, NumMinus, NumMul, NumDiv.");
            sb.AppendLine(L.T("; None — отключить клавишу.", "; None — disable the shortcut."));
            sb.AppendLine();
            sb.AppendLine("[General]");
            sb.AppendLine(L.T("; язык: auto (как в Windows), ru, en", "; language: auto (same as Windows), ru, en"));
            sb.AppendLine("Language = " + Language);
            sb.AppendLine();
            sb.AppendLine("[Hotkeys]");
            sb.AppendLine("PlayPause = " + PlayPause);
            sb.AppendLine("Next = " + Next);
            sb.AppendLine("Previous = " + Previous);
            sb.AppendLine("VolumeUp = " + VolumeUp);
            sb.AppendLine("VolumeDown = " + VolumeDown);
            sb.AppendLine(L.T("; не сворачивать плашку / снова сворачивать", "; keep the panel open / let it fold again"));
            sb.AppendLine("ToggleOverlay = " + ToggleOverlay);
            sb.AppendLine();
            sb.AppendLine("[Bubble]");
            sb.AppendLine(L.T("; когда показывать кружок: always — всегда, playing — пока играет музыка, events — только при смене трека и горячих клавишах",
                              "; when to show the bubble: always, playing — while music plays, events — only on track change and hotkeys"));
            sb.AppendLine("ShowMode = " + ShowMode);
            sb.AppendLine(L.T("; обложка в кружке крутится, пока играет музыка (false — стоит на месте)",
                              "; the cover in the bubble spins while music plays (false — stays still)"));
            sb.AppendLine("Spin = " + (Spin ? "true" : "false"));
            sb.AppendLine(L.T("; где кружок: TopRight, TopCenter, TopLeft, BottomRight, BottomCenter, BottomLeft",
                              "; bubble corner: TopRight, TopCenter, TopLeft, BottomRight, BottomCenter, BottomLeft"));
            sb.AppendLine("Position = " + Position);
            sb.AppendLine(L.T("; куда кружок перетащили мышкой, px (-1 — стоит по Position)",
                              "; where the bubble was dragged to, px (-1 — placed by Position)"));
            sb.AppendLine("CircleX = " + CircleX);
            sb.AppendLine("CircleY = " + CircleY);
            sb.AppendLine(L.T("; отступ от края экрана, px", "; distance from the screen edge, px"));
            sb.AppendLine("Margin = " + Margin);
            sb.AppendLine(L.T("; размер: 1 — обычный, 1.25 — крупнее, 0.8 — мельче", "; size: 1 — normal, 1.25 — bigger, 0.8 — smaller"));
            sb.AppendLine("Scale = " + Scale.ToString("0.##", CultureInfo.InvariantCulture));
            sb.AppendLine(L.T("; сколько секунд плашка остаётся раскрытой", "; how many seconds the panel stays open"));
            sb.AppendLine("ShowSeconds = " + ShowSeconds);
            sb.AppendLine(L.T("; непрозрачность свёрнутого кружка, 30-100", "; opacity of the folded bubble, 30-100"));
            sb.AppendLine("Opacity = " + Opacity);
            sb.AppendLine(L.T("; раскрывать плашку, когда трек сменился или музыку поставили на паузу",
                              "; open the panel when the track changes or music is paused"));
            sb.AppendLine("ShowOnTrackChange = " + (ShowOnTrackChange ? "true" : "false"));
            sb.AppendLine(L.T("; плашка не сворачивается", "; the panel never folds"));
            sb.AppendLine("AlwaysVisible = " + (AlwaysVisible ? "true" : "false"));
            sb.AppendLine(L.T("; управление мышкой: щелчок, перетаскивание, кнопки (false — мышь проходит насквозь)",
                              "; mouse control: click, drag, buttons (false — the mouse passes through)"));
            sb.AppendLine("MouseControls = " + (MouseControls ? "true" : "false"));
            File.WriteAllText(PathIni, sb.ToString(), new UTF8Encoding(true));
        }
    }

    // Ожидание вертикальной развёртки монитора (D3DKMT из gdi32 — тот же механизм, что у браузеров).
    // Если не получилось (старый драйвер, удалённый рабочий стол), Wait() вернёт false и анимация пойдёт по таймеру.
    sealed class VBlank : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        struct OpenAdapterFromHdc { public IntPtr hDc; public uint hAdapter; public uint LuidLow; public int LuidHigh; public uint VidPnSourceId; }
        [StructLayout(LayoutKind.Sequential)]
        struct WaitForVblank { public uint hAdapter; public uint hDevice; public uint VidPnSourceId; }
        [StructLayout(LayoutKind.Sequential)]
        struct CloseAdapter { public uint hAdapter; }

        [DllImport("gdi32.dll")] static extern int D3DKMTOpenAdapterFromHdc(ref OpenAdapterFromHdc p);
        [DllImport("gdi32.dll")] static extern int D3DKMTWaitForVerticalBlankEvent(ref WaitForVblank p);
        [DllImport("gdi32.dll")] static extern int D3DKMTCloseAdapter(ref CloseAdapter p);
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateDC(string driver, string device, string output, IntPtr init);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);

        uint adapter, source;
        bool ok;

        public VBlank(string deviceName)
        {
            try
            {
                IntPtr dc = CreateDC(deviceName, null, null, IntPtr.Zero);
                if (dc == IntPtr.Zero) return;
                var o = new OpenAdapterFromHdc();
                o.hDc = dc;
                int st = D3DKMTOpenAdapterFromHdc(ref o);
                DeleteDC(dc);
                if (st != 0) return;
                adapter = o.hAdapter;
                source = o.VidPnSourceId;
                ok = true;
            }
            catch { ok = false; }
        }

        public bool Wait()
        {
            if (!ok) return false;
            var w = new WaitForVblank();
            w.hAdapter = adapter;
            w.VidPnSourceId = source;
            try { return D3DKMTWaitForVerticalBlankEvent(ref w) == 0; }
            catch { ok = false; return false; }
        }

        public void Dispose()
        {
            if (!ok) return;
            ok = false;
            var c = new CloseAdapter();
            c.hAdapter = adapter;
            try { D3DKMTCloseAdapter(ref c); } catch { }
        }
    }

    // Тёмное меню по правому щелчку на кружке.
    class DarkMenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground { get { return Theme.Bar; } }
        public override Color MenuBorder { get { return Theme.Border; } }
        public override Color MenuItemBorder { get { return Theme.ButtonHover; } }
        public override Color MenuItemSelected { get { return Theme.ButtonHover; } }
        public override Color MenuItemSelectedGradientBegin { get { return Theme.ButtonHover; } }
        public override Color MenuItemSelectedGradientEnd { get { return Theme.ButtonHover; } }
        public override Color MenuItemPressedGradientBegin { get { return Theme.Button; } }
        public override Color MenuItemPressedGradientEnd { get { return Theme.Button; } }
        public override Color ImageMarginGradientBegin { get { return Theme.Bar; } }
        public override Color ImageMarginGradientMiddle { get { return Theme.Bar; } }
        public override Color ImageMarginGradientEnd { get { return Theme.Bar; } }
        public override Color CheckBackground { get { return Theme.Button; } }
        public override Color CheckSelectedBackground { get { return Theme.ButtonHover; } }
        public override Color CheckPressedBackground { get { return Theme.ButtonHover; } }
        public override Color SeparatorDark { get { return Theme.Border; } }
        public override Color SeparatorLight { get { return Theme.Bar; } }
    }

    // Язык интерфейса. Каждая надпись в коде записана сразу на двух языках: L.T("по-русски", "in English").
    static class L
    {
        public static bool En;

        public static string T(string ru, string en) { return En ? en : ru; }

        // "ru" / "en" / "auto" (по языку Windows: русский — для русской Windows, иначе английский)
        public static string Resolve(string lang)
        {
            string l = (lang ?? "auto").Trim().ToLowerInvariant();
            if (l == "ru" || l == "en") return l;
            return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru" ? "ru" : "en";
        }

        public static void Apply(string lang) { En = Resolve(lang) == "en"; }
    }

    static class Autostart
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string Name = "MelodyOverlay";

        // Старое имя записи автозапуска (Music Overlay) -> новое.
        public static void MigrateOld()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (k == null || k.GetValue("MusicOverlay") == null) return;
                    k.DeleteValue("MusicOverlay", false);
                    k.SetValue(Name, "\"" + Application.ExecutablePath + "\" --autostart");
                }
            }
            catch { }
        }

        public static bool IsOn()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey))
                    return k != null && k.GetValue(Name) != null;
            }
            catch { return false; }
        }

        public static void Set(bool on)
        {
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (on) k.SetValue(Name, "\"" + Application.ExecutablePath + "\" --autostart");
                else k.DeleteValue(Name, false);
            }
        }
    }

    static class Log
    {
        static readonly string PathLog = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "MelodyOverlay.log");

        public static void Write(string msg)
        {
            try
            {
                var fi = new FileInfo(PathLog);
                if (fi.Exists && fi.Length > 200 * 1024) fi.Delete();
                File.AppendAllText(PathLog, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }
    }

    // ------------------------------------------------------------------ разбор горячих клавиш

    static class Hotkey
    {
        public const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;

        public static bool IsDisabled(string combo)
        {
            if (combo == null) return true;
            string t = combo.Trim().ToLowerInvariant();
            return t.Length == 0 || t == "none" || t == "off" || t == "-";
        }

        public static string Pretty(string combo)
        {
            if (IsDisabled(combo)) return "—";
            List<string> parts = Tokens(combo);
            for (int i = 0; i < parts.Count; i++) parts[i] = DisplayToken(parts[i]);
            return string.Join("+", parts.ToArray());
        }

        public static List<string> Tokens(string combo)
        {
            var list = new List<string>();
            if (IsDisabled(combo)) return list;
            foreach (string p in combo.Split('+'))
            {
                string t = p.Trim();
                if (t.Length > 0) list.Add(t);
            }
            return list;
        }

        public static List<string> ModTokens(uint mods)
        {
            var list = new List<string>();
            if ((mods & MOD_CONTROL) != 0) list.Add("Ctrl");
            if ((mods & MOD_ALT) != 0) list.Add("Alt");
            if ((mods & MOD_SHIFT) != 0) list.Add("Shift");
            if ((mods & MOD_WIN) != 0) list.Add("Win");
            return list;
        }

        // Как показывать клавишу человеку.
        public static string DisplayToken(string t)
        {
            switch (t.ToLowerInvariant())
            {
                case "left": return "←";
                case "right": return "→";
                case "up": return "↑";
                case "down": return "↓";
                case "control": case "ctl": case "ctrl": return "Ctrl";
                case "windows": case "win": return "Win";
                case "pageup": case "pgup": case "prior": return "PgUp";
                case "pagedown": case "pgdn": case "pgdown": case "next": return "PgDn";
                case "space": return L.T("Пробел", "Space");
                case "numplus": case "numadd": return "Num +";
                case "numminus": case "numsub": return "Num −";
                case "nummul": case "nummultiply": return "Num *";
                case "numdiv": case "numdivide": return "Num /";
                case "numdel": case "numdecimal": return "Num .";
                case "delete": case "del": return "Del";
                case "insert": case "ins": return "Ins";
                case "escape": case "esc": return "Esc";
                case "scroll": case "scrolllock": return "ScrLk";
                case "back": case "backspace": return "Backspace";
                case "return": case "enter": return "Enter";
            }
            if (t.Length == 4 && t.StartsWith("num", StringComparison.OrdinalIgnoreCase) && char.IsDigit(t[3])) return "Num " + t[3];
            if (t.Length == 1) return t.ToUpperInvariant();
            return char.ToUpperInvariant(t[0]) + t.Substring(1);
        }

        // Нажатые клавиши -> строка для настроек. null + error, если так назначать нельзя.
        public static string FromKeys(uint mods, Keys key, out string error)
        {
            error = null;
            int code = (int)key;
            if (code >= 0xA6 && code <= 0xB7)
            {
                error = L.T("Медиаклавиши Windows обрабатывает сама — выбери обычную клавишу.",
                            "Windows handles media keys itself — pick a regular key.");
                return null;
            }
            string name = KeyName(key);
            if (name == null) { error = L.T("Эту клавишу назначить нельзя — выбери другую.", "This key can't be assigned — pick another one."); return null; }

            int core = 0;
            if ((mods & MOD_CONTROL) != 0) core++;
            if ((mods & MOD_ALT) != 0) core++;
            if ((mods & MOD_SHIFT) != 0) core++;
            if (!IsStandalone(key) && core < 2 && (mods & MOD_WIN) == 0)
            {
                error = L.T("Для букв, цифр и стрелок нужны два модификатора, например Ctrl+Alt — " +
                            "иначе перестанут работать обычные сочетания вроде Ctrl+C.",
                            "Letters, digits and arrows need two modifiers, e.g. Ctrl+Alt — " +
                            "otherwise everyday shortcuts like Ctrl+C would stop working.");
                return null;
            }
            if (mods == MOD_ALT && key == Keys.F4)
            {
                error = L.T("Alt+F4 закрывает окна — выбери другое сочетание.", "Alt+F4 closes windows — pick another shortcut.");
                return null;
            }

            var parts = ModTokens(mods);
            parts.Add(name);
            return string.Join("+", parts.ToArray());
        }

        // Клавиши, которые можно назначать и без модификаторов: F1-F24, цифровой блок, Pause, Scroll Lock.
        static bool IsStandalone(Keys k)
        {
            return (k >= Keys.F1 && k <= Keys.F24) || (k >= Keys.NumPad0 && k <= Keys.Divide)
                || k == Keys.Pause || k == Keys.Scroll;
        }

        static string KeyName(Keys k)
        {
            if (k >= Keys.A && k <= Keys.Z) return ((char)k).ToString();
            if (k >= Keys.D0 && k <= Keys.D9) return ((char)('0' + (k - Keys.D0))).ToString();
            if (k >= Keys.F1 && k <= Keys.F24) return "F" + (1 + (k - Keys.F1));
            if (k >= Keys.NumPad0 && k <= Keys.NumPad9) return "Num" + (k - Keys.NumPad0);
            switch (k)
            {
                case Keys.Add: return "NumPlus";
                case Keys.Subtract: return "NumMinus";
                case Keys.Multiply: return "NumMul";
                case Keys.Divide: return "NumDiv";
                case Keys.Decimal: return "NumDel";
                case Keys.Space: return "Space";
                case Keys.Left: return "Left";
                case Keys.Right: return "Right";
                case Keys.Up: return "Up";
                case Keys.Down: return "Down";
                case Keys.Home: return "Home";
                case Keys.End: return "End";
                case Keys.PageUp: return "PageUp";
                case Keys.PageDown: return "PageDown";
                case Keys.Insert: return "Insert";
                case Keys.Delete: return "Delete";
                case Keys.Pause: return "Pause";
                case Keys.Scroll: return "Scroll";
                case Keys.Return: return "Enter";
                case Keys.Back: return "Backspace";
                case Keys.Tab: return "Tab";
                case Keys.Escape: return "Esc";
                case Keys.OemPeriod: return ".";
                case Keys.Oemcomma: return ",";
                case Keys.OemMinus: return "-";
                case Keys.Oemplus: return "=";
                case Keys.Oemtilde: return "`";
                case Keys.OemOpenBrackets: return "[";
                case Keys.OemCloseBrackets: return "]";
                case Keys.OemQuestion: return "/";
                case Keys.OemPipe: return "\\";
                case Keys.OemQuotes: return "'";
                case Keys.OemSemicolon: return ";";
            }
            return null;
        }

        public static bool TryParse(string combo, out uint mods, out uint vk)
        {
            mods = 0; vk = 0;
            if (IsDisabled(combo)) return false;
            string[] parts = combo.Split('+');
            for (int i = 0; i < parts.Length; i++)
            {
                string p = parts[i].Trim();
                if (i < parts.Length - 1)
                {
                    switch (p.ToLowerInvariant())
                    {
                        case "ctrl": case "control": case "ctl": mods |= MOD_CONTROL; break;
                        case "alt": mods |= MOD_ALT; break;
                        case "shift": mods |= MOD_SHIFT; break;
                        case "win": case "windows": mods |= MOD_WIN; break;
                        default: return false;
                    }
                }
                else vk = ParseKey(p);
            }
            return vk != 0;
        }

        static uint ParseKey(string k)
        {
            if (k.Length == 0) return 0;
            string l = k.ToLowerInvariant();
            if (l.Length == 1)
            {
                char c = l[0];
                if (c >= '0' && c <= '9') return (uint)c;
                if (c >= 'a' && c <= 'z') return (uint)char.ToUpperInvariant(c);
                switch (c)
                {
                    case '.': return (uint)Keys.OemPeriod;
                    case ',': return (uint)Keys.Oemcomma;
                    case '-': return (uint)Keys.OemMinus;
                    case '=': return (uint)Keys.Oemplus;
                    case '`': return (uint)Keys.Oemtilde;
                    case '[': return (uint)Keys.OemOpenBrackets;
                    case ']': return (uint)Keys.OemCloseBrackets;
                    case '/': return (uint)Keys.OemQuestion;
                    case '\\': return (uint)Keys.OemPipe;
                    case '\'': return (uint)Keys.OemQuotes;
                    case ';': return (uint)Keys.OemSemicolon;
                }
            }
            if (l.Length == 4 && l.StartsWith("num") && char.IsDigit(l[3])) return (uint)Keys.NumPad0 + (uint)(l[3] - '0');
            switch (l)
            {
                case "numplus": case "numadd": return (uint)Keys.Add;
                case "numminus": case "numsub": return (uint)Keys.Subtract;
                case "nummul": case "nummultiply": return (uint)Keys.Multiply;
                case "numdiv": case "numdivide": return (uint)Keys.Divide;
                case "numdel": case "numdecimal": return (uint)Keys.Decimal;
                case "pgup": return (uint)Keys.PageUp;
                case "pgdn": case "pgdown": return (uint)Keys.PageDown;
                case "del": return (uint)Keys.Delete;
                case "ins": return (uint)Keys.Insert;
                case "esc": return (uint)Keys.Escape;
                case "plus": return (uint)Keys.Oemplus;
                case "minus": return (uint)Keys.OemMinus;
                case "scrolllock": return (uint)Keys.Scroll;
                case "backspace": return (uint)Keys.Back;
                case "enter": return (uint)Keys.Return;
                case "tilde": return (uint)Keys.Oemtilde;
            }
            Keys key;
            if (Enum.TryParse<Keys>(k, true, out key))
            {
                uint v = (uint)key;
                if (v > 0 && v <= 0xFE) return v;
            }
            return 0;
        }
    }

    // ------------------------------------------------------------------ громкость системы

    static class Volume
    {
        public static bool TryChange(float delta, out int percent, out bool muted)
        {
            percent = -1; muted = false;
            object enumObj = null, volObj = null;
            IMMDevice dev = null;
            try
            {
                enumObj = new MMDeviceEnumeratorCom();
                var en = (IMMDeviceEnumerator)enumObj;
                if (en.GetDefaultAudioEndpoint(0 /*eRender*/, 1 /*eMultimedia*/, out dev) != 0 || dev == null) return false;
                Guid iid = typeof(IAudioEndpointVolume).GUID;
                if (dev.Activate(ref iid, 23 /*CLSCTX_ALL*/, IntPtr.Zero, out volObj) != 0 || volObj == null) return false;
                var vol = (IAudioEndpointVolume)volObj;
                float cur;
                if (vol.GetMasterVolumeLevelScalar(out cur) != 0) return false;
                float nv = cur;
                if (delta != 0)
                {
                    nv = (float)(Math.Round((cur + delta) * 50) / 50.0);
                    nv = Math.Max(0f, Math.Min(1f, nv));
                    Guid ctx = Guid.Empty;
                    vol.SetMasterVolumeLevelScalar(nv, ref ctx);
                    bool m;
                    if (delta > 0 && vol.GetMute(out m) == 0 && m) vol.SetMute(false, ref ctx);
                }
                vol.GetMute(out muted);
                percent = (int)Math.Round(nv * 100);
                return true;
            }
            catch (Exception ex) { Log.Write("громкость: " + ex.Message); return false; }
            finally
            {
                if (volObj != null) Marshal.ReleaseComObject(volObj);
                if (dev != null) Marshal.ReleaseComObject(dev);
                if (enumObj != null) Marshal.ReleaseComObject(enumObj);
            }
        }
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumeratorCom { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float levelDB, ref Guid ctx);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid ctx);
        [PreserveSig] int GetMasterVolumeLevel(out float levelDB);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint ch, float levelDB, ref Guid ctx);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint ch, float level, ref Guid ctx);
        [PreserveSig] int GetChannelVolumeLevel(uint ch, out float levelDB);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint ch, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        [PreserveSig] int GetVolumeStepInfo(out uint step, out uint count);
        [PreserveSig] int VolumeStepUp(ref Guid ctx);
        [PreserveSig] int VolumeStepDown(ref Guid ctx);
        [PreserveSig] int QueryHardwareSupport(out uint mask);
        [PreserveSig] int GetVolumeRange(out float min, out float max, out float inc);
    }

    // ------------------------------------------------------------------ рисование

    static class Gfx
    {
        public static GraphicsPath RoundRect(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            float d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
            if (d <= 0.5f) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        // Квадратная обложка: обрезаем по центру и уменьшаем.
        public static Bitmap CoverSquare(Image img, int size)
        {
            int side = Math.Min(img.Width, img.Height);
            int sx = (img.Width - side) / 2, sy = (img.Height - side) / 2;
            int outSize = Math.Min(size, side);
            var bmp = new Bitmap(outSize, outSize, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            using (var ia = new ImageAttributes())
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                ia.SetWrapMode(WrapMode.TileFlipXY);
                g.DrawImage(img, new Rectangle(0, 0, outSize, outSize), sx, sy, side, side, GraphicsUnit.Pixel, ia);
            }
            return bmp;
        }

        public static void FillImageRounded(Graphics g, Image img, RectangleF r, float radius)
        {
            int sz = Math.Max(1, (int)Math.Ceiling(r.Width));
            using (var scaled = new Bitmap(sz, sz, PixelFormat.Format32bppArgb))
            {
                using (Graphics sg = Graphics.FromImage(scaled))
                using (var ia = new ImageAttributes())
                {
                    sg.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    sg.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    ia.SetWrapMode(WrapMode.TileFlipXY);
                    sg.DrawImage(img, new Rectangle(0, 0, sz, sz), 0, 0, img.Width, img.Height, GraphicsUnit.Pixel, ia);
                }
                using (var tb = new TextureBrush(scaled, WrapMode.Clamp))
                using (GraphicsPath path = RoundRect(r, radius))
                {
                    tb.TranslateTransform(r.X, r.Y);
                    g.FillPath(tb, path);
                }
            }
        }

        public static void FillImageEllipse(Graphics g, Image img, RectangleF r)
        {
            int sz = Math.Max(1, (int)Math.Ceiling(r.Width));
            using (var scaled = new Bitmap(sz, sz, PixelFormat.Format32bppArgb))
            {
                using (Graphics sg = Graphics.FromImage(scaled))
                using (var ia = new ImageAttributes())
                {
                    sg.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    sg.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    ia.SetWrapMode(WrapMode.TileFlipXY);
                    sg.DrawImage(img, new Rectangle(0, 0, sz, sz), 0, 0, img.Width, img.Height, GraphicsUnit.Pixel, ia);
                }
                using (var tb = new TextureBrush(scaled, WrapMode.Clamp))
                {
                    tb.TranslateTransform(r.X, r.Y);
                    g.FillEllipse(tb, r);
                }
            }
        }

        static void FillBar(Graphics g, Brush b, float x, float y, float w, float h)
        {
            using (GraphicsPath p = RoundRect(new RectangleF(x, y, w, h), w * 0.3f)) g.FillPath(b, p);
        }

        public static void DrawGlyph(Graphics g, string kind, RectangleF r, Color c)
        {
            float x = r.X, y = r.Y, w = r.Width, h = r.Height;
            using (var b = new SolidBrush(c))
            using (var pen = new Pen(c, Math.Max(1.2f, w * 0.16f)))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;
                switch (kind)
                {
                    case "play":
                        g.FillPolygon(b, new[] { new PointF(x + w * 0.12f, y), new PointF(x + w, y + h / 2), new PointF(x + w * 0.12f, y + h) });
                        break;
                    case "pause":
                        FillBar(g, b, x + w * 0.10f, y, w * 0.30f, h);
                        FillBar(g, b, x + w * 0.60f, y, w * 0.30f, h);
                        break;
                    case "next":
                        g.FillPolygon(b, new[] { new PointF(x, y), new PointF(x + w * 0.72f, y + h / 2), new PointF(x, y + h) });
                        FillBar(g, b, x + w * 0.78f, y, w * 0.20f, h);
                        break;
                    case "prev":
                        g.FillPolygon(b, new[] { new PointF(x + w, y), new PointF(x + w * 0.28f, y + h / 2), new PointF(x + w, y + h) });
                        FillBar(g, b, x + w * 0.02f, y, w * 0.20f, h);
                        break;
                    case "volup":
                    case "voldown":
                    {
                        g.FillPolygon(b, new[] {
                            new PointF(x, y + h * 0.32f), new PointF(x + w * 0.22f, y + h * 0.32f),
                            new PointF(x + w * 0.50f, y + h * 0.04f), new PointF(x + w * 0.50f, y + h * 0.96f),
                            new PointF(x + w * 0.22f, y + h * 0.68f), new PointF(x, y + h * 0.68f) });
                        float cx = x + w * 0.82f, cy = y + h * 0.5f, d = w * 0.17f;
                        g.DrawLine(pen, cx - d, cy, cx + d, cy);
                        if (kind == "volup") g.DrawLine(pen, cx, cy - d, cx, cy + d);
                        break;
                    }
                    case "pin":
                        g.FillEllipse(b, x + w * 0.15f, y + h * 0.15f, w * 0.7f, h * 0.7f);
                        break;
                    case "ff":
                        g.FillPolygon(b, new[] { new PointF(x, y + h * 0.1f), new PointF(x + w * 0.5f, y + h / 2), new PointF(x, y + h * 0.9f) });
                        g.FillPolygon(b, new[] { new PointF(x + w * 0.5f, y + h * 0.1f), new PointF(x + w, y + h / 2), new PointF(x + w * 0.5f, y + h * 0.9f) });
                        break;
                    case "rew":
                        g.FillPolygon(b, new[] { new PointF(x + w, y + h * 0.1f), new PointF(x + w * 0.5f, y + h / 2), new PointF(x + w, y + h * 0.9f) });
                        g.FillPolygon(b, new[] { new PointF(x + w * 0.5f, y + h * 0.1f), new PointF(x, y + h / 2), new PointF(x + w * 0.5f, y + h * 0.9f) });
                        break;
                    case "gear":
                    {
                        float gx = x + w / 2, gy = y + h / 2, R = w / 2;
                        using (var ring = new Pen(c, R * 0.30f))
                            g.DrawEllipse(ring, gx - R * 0.52f, gy - R * 0.52f, R * 1.04f, R * 1.04f);
                        using (var tooth = new Pen(c, R * 0.36f))
                        {
                            for (int i = 0; i < 8; i++)
                            {
                                double ang = i * Math.PI / 4 + Math.PI / 8;
                                float ca = (float)Math.Cos(ang), sa = (float)Math.Sin(ang);
                                g.DrawLine(tooth, gx + ca * R * 0.60f, gy + sa * R * 0.60f, gx + ca * R * 0.98f, gy + sa * R * 0.98f);
                            }
                        }
                        break;
                    }
                    case "check":
                        g.DrawLines(pen, new[] { new PointF(x + w * 0.08f, y + h * 0.55f), new PointF(x + w * 0.38f, y + h * 0.85f), new PointF(x + w * 0.92f, y + h * 0.18f) });
                        break;
                    default: // stop
                        using (GraphicsPath p = RoundRect(new RectangleF(x + w * 0.1f, y + h * 0.1f, w * 0.8f, h * 0.8f), w * 0.15f)) g.FillPath(b, p);
                        break;
                }
            }
        }

        // Две ноты с перекладиной (♫)
        public static void DrawNote(Graphics g, RectangleF r, Color c)
        {
            float x = r.X, y = r.Y, w = r.Width, h = r.Height;
            using (var b = new SolidBrush(c))
            {
                g.FillPolygon(b, new[] {
                    new PointF(x + w * 0.36f, y + h * 0.14f), new PointF(x + w * 0.92f, y + h * 0.02f),
                    new PointF(x + w * 0.92f, y + h * 0.18f), new PointF(x + w * 0.36f, y + h * 0.30f) });
                g.FillRectangle(b, x + w * 0.36f, y + h * 0.16f, w * 0.09f, h * 0.62f);
                g.FillRectangle(b, x + w * 0.83f, y + h * 0.04f, w * 0.09f, h * 0.62f);
                FillHead(g, b, x + w * 0.27f, y + h * 0.79f, w * 0.19f, h * 0.14f);
                FillHead(g, b, x + w * 0.74f, y + h * 0.67f, w * 0.19f, h * 0.14f);
            }
        }

        static void FillHead(Graphics g, Brush b, float cx, float cy, float rx, float ry)
        {
            GraphicsState st = g.Save();
            g.TranslateTransform(cx, cy);
            g.RotateTransform(-22f);
            g.FillEllipse(b, -rx, -ry, rx * 2, ry * 2);
            g.Restore(st);
        }
    }

    static class IconArt
    {
        public static Bitmap Draw(int size)
        {
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.Clear(Color.Transparent);
                float s = size;
                var r = new RectangleF(s * 0.02f, s * 0.02f, s * 0.96f, s * 0.96f);
                using (GraphicsPath path = Gfx.RoundRect(r, s * 0.24f))
                using (var br = new LinearGradientBrush(r, Color.FromArgb(255, 124, 92, 255), Color.FromArgb(255, 32, 201, 151), 45f))
                    g.FillPath(br, path);
                float inset = s * (size <= 20 ? 0.18f : 0.22f);
                Gfx.DrawNote(g, RectangleF.Inflate(r, -inset, -inset), Color.White);
            }
            return bmp;
        }

        public static void WriteIco(string path, int[] sizes)
        {
            var pngs = new List<byte[]>();
            foreach (int sz in sizes)
            {
                using (Bitmap b = Draw(sz))
                using (var ms = new MemoryStream())
                {
                    b.Save(ms, ImageFormat.Png);
                    pngs.Add(ms.ToArray());
                }
            }
            using (FileStream fs = File.Create(path))
            using (var w = new BinaryWriter(fs))
            {
                w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    int sz = sizes[i];
                    w.Write((byte)(sz >= 256 ? 0 : sz));
                    w.Write((byte)(sz >= 256 ? 0 : sz));
                    w.Write((byte)0); w.Write((byte)0);
                    w.Write((short)1); w.Write((short)32);
                    w.Write(pngs[i].Length);
                    w.Write(offset);
                    offset += pngs[i].Length;
                }
                foreach (byte[] p in pngs) w.Write(p);
            }
        }
    }

    // ------------------------------------------------------------------ WinAPI

    static class Native
    {
        [DllImport("user32.dll", SetLastError = true)] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll")] static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern IntPtr GetShellWindow();
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hWnd, StringBuilder name, int max);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
            IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
        [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr value);
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
        [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hDC);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr hObject);

        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; public POINT(int x, int y) { X = x; Y = y; } }
        [StructLayout(LayoutKind.Sequential)] public struct SIZE { public int cx, cy; public SIZE(int w, int h) { cx = w; cy = h; } }
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)] public struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

        public const int ULW_ALPHA = 2;
        public const byte AC_SRC_OVER = 0, AC_SRC_ALPHA = 1;
        public const int WS_POPUP = unchecked((int)0x80000000);
        public const int WS_EX_LAYERED = 0x80000, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80,
                         WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOPMOST = 0x8;
        public const int WM_HOTKEY = 0x0312, WM_MOUSEACTIVATE = 0x21, MA_NOACTIVATE = 3;
        public const int WM_RBUTTONUP = 0x205;
        public const int WM_MOUSEMOVE = 0x200, WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202, WM_MOUSELEAVE = 0x2A3,
                         WM_CAPTURECHANGED = 0x215, WM_SETCURSOR = 0x20, HTCLIENT = 1, TME_LEAVE = 2, GWL_EXSTYLE = -20;
        public static readonly IntPtr IDC_ARROW = new IntPtr(32512), IDC_HAND = new IntPtr(32649);

        [StructLayout(LayoutKind.Sequential)]
        public struct TRACKMOUSEEVENT { public int cbSize; public int dwFlags; public IntPtr hwndTrack; public int dwHoverTime; }

        [DllImport("user32.dll")] public static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT tme);
        [DllImport("user32.dll")] public static extern IntPtr SetCapture(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern IntPtr GetCapture();
        [DllImport("user32.dll")] public static extern bool ReleaseCapture();
        [DllImport("user32.dll")] public static extern IntPtr SetCursor(IntPtr hCursor);
        [DllImport("user32.dll")] public static extern IntPtr LoadCursor(IntPtr hInstance, IntPtr name);
        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hWnd, int index);
        [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr hWnd, int index, int value);
        public const int SW_HIDE = 0, SW_SHOWNOACTIVATE = 4;
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;
        public const byte VK_MEDIA_NEXT_TRACK = 0xB0, VK_MEDIA_PREV_TRACK = 0xB1, VK_MEDIA_PLAY_PAUSE = 0xB3,
                          VK_VOLUME_DOWN = 0xAE, VK_VOLUME_UP = 0xAF;
        const uint KEYEVENTF_EXTENDEDKEY = 0x1, KEYEVENTF_KEYUP = 0x2;

        public static void TapKey(byte vk)
        {
            keybd_event(vk, 0, KEYEVENTF_EXTENDEDKEY, UIntPtr.Zero);
            keybd_event(vk, 0, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP, UIntPtr.Zero);
        }

        public static void EnableDpiAwareness()
        {
            try { if (SetProcessDpiAwarenessContext(new IntPtr(-4))) return; } catch { }
            try { SetProcessDPIAware(); } catch { }
        }

        public static float DpiScaleAt(Rectangle bounds)
        {
            try
            {
                IntPtr mon = MonitorFromPoint(new POINT(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2), 2);
                uint dx, dy;
                if (mon != IntPtr.Zero && GetDpiForMonitor(mon, 0, out dx, out dy) == 0 && dx > 0) return dx / 96f;
            }
            catch { }
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) return g.DpiX / 96f;
        }

        // Окно на весь монитор (игра в полноэкранном / безрамочном режиме)?
        public static bool IsFullscreen(IntPtr hwnd, Rectangle screen)
        {
            if (hwnd == IntPtr.Zero || hwnd == GetShellWindow()) return false;
            var cls = new StringBuilder(64);
            GetClassName(hwnd, cls, cls.Capacity);
            string c = cls.ToString();
            if (c == "WorkerW" || c == "Progman" || c == "Shell_TrayWnd") return false;
            RECT r;
            if (!GetWindowRect(hwnd, out r)) return false;
            return r.Left <= screen.Left && r.Top <= screen.Top && r.Right >= screen.Right && r.Bottom >= screen.Bottom;
        }
    }
}
