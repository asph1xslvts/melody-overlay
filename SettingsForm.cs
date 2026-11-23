// Окно настроек Melody Overlay: горячие клавиши назначаются нажатием,
// внешний вид оверлея меняется мышкой и сразу показывается на экране.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MelodyOverlay
{
    static class Theme
    {
        public static readonly Color Bg = Color.FromArgb(28, 28, 34);
        public static readonly Color Bar = Color.FromArgb(36, 36, 44);
        public static readonly Color Field = Color.FromArgb(43, 43, 53);
        public static readonly Color FieldActive = Color.FromArgb(48, 52, 56);
        public static readonly Color Border = Color.FromArgb(62, 62, 76);
        public static readonly Color BorderHover = Color.FromArgb(96, 96, 116);
        public static readonly Color Chip = Color.FromArgb(64, 64, 80);
        public static readonly Color Text = Color.FromArgb(240, 241, 245);
        public static readonly Color Label = Color.FromArgb(212, 214, 224);
        public static readonly Color Dim = Color.FromArgb(146, 149, 165);
        public static readonly Color Accent = Color.FromArgb(61, 220, 151);
        public static readonly Color AccentHover = Color.FromArgb(104, 233, 177);
        public static readonly Color AccentText = Color.FromArgb(9, 32, 21);
        public static readonly Color Danger = Color.FromArgb(255, 110, 110);
        public static readonly Color Off = Color.FromArgb(70, 70, 86);
        public static readonly Color OffHover = Color.FromArgb(88, 88, 106);
        public static readonly Color Slot = Color.FromArgb(52, 52, 64);
        public static readonly Color Screen = Color.FromArgb(20, 20, 26);
        public static readonly Color Button = Color.FromArgb(48, 48, 60);
        public static readonly Color ButtonHover = Color.FromArgb(60, 60, 75);
    }

    class SettingsForm : Form
    {
        static string[] ActionNames
        {
            get
            {
                return new[] {
                    L.T("Пауза / играть", "Play / pause"), L.T("Следующий трек", "Next track"),
                    L.T("Предыдущий трек", "Previous track"), L.T("Громкость +", "Volume +"),
                    L.T("Громкость −", "Volume −"), L.T("Не сворачивать", "Keep panel open") };
            }
        }

        static string Hint
        {
            get
            {
                return L.T("Щёлкни по полю и нажми сочетание. Esc — отмена, Backspace — очистить. " +
                           "Если сочетание не ловится, его перехватывает другая программа.",
                           "Click a field and press a shortcut. Esc — cancel, Backspace — clear. " +
                           "If a shortcut isn't caught, another program is grabbing it.");
            }
        }

        Segmented segLang, segShow;
        bool rebuilding;

        readonly App app;
        readonly Config original;
        Config draft;
        readonly float k;
        bool saved, loading;

        readonly HotkeyBox[] boxes = new HotkeyBox[6];
        readonly ToolTip tips = new ToolTip();
        Label status;
        Slider slScale, slTransp, slSeconds;
        Label lbScale, lbTransp, lbSeconds;
        Toggle tgSpin, tgTrack, tgAlways, tgMouse, tgAutostart;
        Button btnSave, btnCancel, btnReset;
        Font fBody, fSmall, fHeader, fButton, fIcon;

        public SettingsForm(App app, Config current, bool autostart)
        {
            this.app = app;
            original = current.Clone();
            draft = current.Clone();
            k = Native.DpiScaleAt(Screen.FromPoint(Cursor.Position).Bounds);

            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            DoubleBuffered = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            fBody = new Font("Segoe UI", 13f * k, FontStyle.Regular, GraphicsUnit.Pixel);
            fSmall = new Font("Segoe UI", 12f * k, FontStyle.Regular, GraphicsUnit.Pixel);
            fHeader = new Font("Segoe UI Semibold", 16f * k, FontStyle.Regular, GraphicsUnit.Pixel);
            fButton = new Font("Segoe UI Semibold", 13f * k, FontStyle.Regular, GraphicsUnit.Pixel);
            fIcon = new Font("Segoe UI", 18f * k, FontStyle.Regular, GraphicsUnit.Pixel);
            Font = fBody;

            BuildUi();
            tgAutostart.Checked = autostart;
            LoadDraft();
            ValidateHotkeys();
        }

        int S(float v) { return (int)Math.Round(v * k); }

        // ---------------------------------------------------------- разметка

        void BuildUi()
        {
            SuspendLayout();
            const int LX = 24, RX = 448, W = 848;
            Text = L.T("Melody Overlay — настройки", "Melody Overlay — settings");
            string[] names = ActionNames;

            // левая колонка — горячие клавиши
            AddHeader(L.T("Горячие клавиши", "Hotkeys"), LX, 18);
            int y = 58;
            for (int i = 0; i < 6; i++)
            {
                int idx = i;
                AddLabel(names[i], LX, y, 150, 32);

                var box = new HotkeyBox(k);
                box.Bounds = new Rectangle(S(LX + 152), S(y), S(194), S(32));
                box.CaptureStarted += (s, e) => { app.SuspendHotkeys(); ShowStatus(Hint, false); };
                box.CaptureEnded += (s, e) => { if (!rebuilding) ValidateHotkeys(); };
                box.ValueChanged += (s, e) => SetCombo(idx, boxes[idx].Value);
                box.Rejected += (s, e) => ShowStatus(boxes[idx].LastError, true);
                Controls.Add(box);
                boxes[i] = box;

                var clr = new Button();
                clr.Text = "×";
                clr.Font = fIcon;
                clr.FlatStyle = FlatStyle.Flat;
                clr.FlatAppearance.BorderSize = 0;
                clr.FlatAppearance.MouseOverBackColor = Theme.Field;
                clr.FlatAppearance.MouseDownBackColor = Theme.Border;
                clr.BackColor = Theme.Bg;
                clr.ForeColor = Theme.Dim;
                clr.TabStop = false;
                clr.Cursor = Cursors.Hand;
                clr.Padding = Padding.Empty;
                clr.Bounds = new Rectangle(S(LX + 350), S(y), S(28), S(32));
                clr.Click += (s, e) => { boxes[idx].Value = "None"; SetCombo(idx, "None"); ValidateHotkeys(); };
                tips.SetToolTip(clr, L.T("Отключить", "Disable"));
                Controls.Add(clr);
                y += 40;
            }

            status = new Label();
            status.Font = fSmall;
            status.AutoSize = false;
            status.BackColor = Theme.Bg;
            status.Bounds = new Rectangle(S(LX), S(y + 2), S(376), S(54));
            Controls.Add(status);
            y += 68;

            AddHeader(L.T("Общее", "General"), LX, y);
            y += 40;
            tgAutostart = new Toggle(k, L.T("Запускать вместе с Windows", "Start with Windows"));
            tgAutostart.Bounds = new Rectangle(S(LX), S(y), S(376), S(28));
            Controls.Add(tgAutostart);
            y += 40;
            AddLabel(L.T("Язык", "Language"), LX, y, 150, 32);
            segLang = new Segmented(k, new[] { "Русский", "English" });
            segLang.Bounds = new Rectangle(S(LX + 152), S(y), S(194), S(32));
            segLang.ValueChanged += (s, e) => OnLanguage();
            Controls.Add(segLang);
            int leftEnd = y + 32;

            // правая колонка — оверлей
            AddHeader(L.T("Оверлей", "Overlay"), RX, 18);
            y = 58;
            AddLabel(L.T("Показывать", "Show"), RX, y, 96, 32);
            segShow = new Segmented(k, new[] { L.T("Всегда", "Always"), L.T("Когда играет", "When playing"), L.T("По событию", "On events") });
            segShow.Bounds = new Rectangle(S(RX + 98), S(y), S(278), S(32));
            segShow.ValueChanged += (s, e) =>
            {
                if (loading) return;
                draft.ShowMode = segShow.SelectedIndex == 1 ? "playing" : segShow.SelectedIndex == 2 ? "events" : "always";
                Preview();
            };
            tips.SetToolTip(segShow, L.T("Всегда — кружок на экране всё время. Когда играет — только пока идёт музыка. " +
                                         "По событию — кружок спрятан и появляется при смене трека, паузе и горячих клавишах.",
                                         "Always — the bubble stays on screen. When playing — only while music plays. " +
                                         "On events — hidden, appears on track change, pause and hotkeys."));
            Controls.Add(segShow);
            y += 44;
            slScale = AddSlider(L.T("Размер", "Size"), RX, ref y, 50, 200, 5, out lbScale);
            slTransp = AddSlider(L.T("Прозрачность", "Transparency"), RX, ref y, 0, 70, 5, out lbTransp);
            slSeconds = AddSlider(L.T("Плашка открыта", "Panel stays open"), RX, ref y, 1, 20, 1, out lbSeconds);

            y += 2;
            tgSpin = new Toggle(k, L.T("Крутить обложку", "Spin the cover"));
            tgSpin.Bounds = new Rectangle(S(RX), S(y), S(376), S(28));
            tgSpin.CheckedChanged += (s, e) => { if (loading) return; draft.Spin = tgSpin.Checked; Preview(); };
            tips.SetToolTip(tgSpin, L.T("Обложка в кружке медленно крутится, пока играет музыка, и останавливается на паузе.",
                                        "The cover in the bubble slowly spins while music plays and stops on pause."));
            Controls.Add(tgSpin);
            y += 36;
            tgTrack = new Toggle(k, L.T("Раскрывать при смене трека и паузе", "Open on track change and pause"));
            tgTrack.Bounds = new Rectangle(S(RX), S(y), S(376), S(28));
            tgTrack.CheckedChanged += (s, e) => { if (loading) return; draft.ShowOnTrackChange = tgTrack.Checked; };
            Controls.Add(tgTrack);
            y += 36;
            tgAlways = new Toggle(k, L.T("Не сворачивать плашку", "Keep the panel open"));
            tgAlways.Bounds = new Rectangle(S(RX), S(y), S(376), S(28));
            tgAlways.CheckedChanged += (s, e) => { if (loading) return; draft.AlwaysVisible = tgAlways.Checked; Preview(); };
            Controls.Add(tgAlways);
            y += 36;
            tgMouse = new Toggle(k, L.T("Управление мышкой", "Mouse control"));
            tgMouse.Bounds = new Rectangle(S(RX), S(y), S(376), S(28));
            tgMouse.CheckedChanged += (s, e) => { if (loading) return; draft.MouseControls = tgMouse.Checked; Preview(); };
            tips.SetToolTip(tgMouse, L.T("Щелчок по кружку раскрывает плашку, кружок можно перетащить, на плашке — ⏮ ⏯ ⏭ и перемотка. " +
                                         "Если выключить, мышь проходит сквозь кружок.",
                                         "Click the bubble to open the panel, drag it anywhere, use ⏮ ⏯ ⏭ and seeking on the panel. " +
                                         "When off, the mouse passes through the bubble."));
            Controls.Add(tgMouse);
            int rightEnd = y + 28;

            // нижняя панель с кнопками
            int barY = Math.Max(leftEnd, rightEnd) + 24;
            var bar = new Panel();
            bar.BackColor = Theme.Bar;
            bar.Bounds = new Rectangle(0, S(barY), S(W), S(64));

            var link = new LinkLabel();
            link.Text = L.T("Открыть файл настроек", "Open settings file");
            link.Font = fBody;
            link.AutoSize = true;
            link.BackColor = Theme.Bar;
            link.LinkColor = Theme.Accent;
            link.ActiveLinkColor = Theme.AccentHover;
            link.VisitedLinkColor = Theme.Accent;
            link.LinkBehavior = LinkBehavior.HoverUnderline;
            link.TabStop = false;
            link.LinkClicked += (s, e) => app.OpenSettingsFile();
            bar.Controls.Add(link);
            link.Location = new Point(S(LX), (S(64) - link.PreferredHeight) / 2);

            btnSave = MakeButton(L.T("Сохранить", "Save"), true);
            btnSave.Bounds = new Rectangle(S(W - 24 - 124), S(14), S(124), S(36));
            btnCancel = MakeButton(L.T("Отмена", "Cancel"), false);
            btnCancel.Bounds = new Rectangle(S(W - 24 - 124 - 10 - 104), S(14), S(104), S(36));
            btnReset = MakeButton(L.T("Сбросить", "Reset"), false);
            btnReset.Bounds = new Rectangle(S(W - 24 - 124 - 10 - 104 - 10 - 112), S(14), S(112), S(36));
            tips.SetToolTip(btnReset, L.T("Вернуть всё как было по умолчанию", "Restore all defaults"));
            bar.Controls.Add(btnReset);
            bar.Controls.Add(btnCancel);
            bar.Controls.Add(btnSave);
            Controls.Add(bar);

            btnSave.Click += (s, e) => Save();
            btnCancel.Click += (s, e) => Close();
            btnReset.Click += (s, e) => ResetDefaults();
            CancelButton = btnCancel;

            ClientSize = new Size(S(W), S(barY + 64));
            ResumeLayout(false);
        }

        void AddHeader(string text, int x, int y)
        {
            var l = new Label();
            l.Text = text;
            l.Font = fHeader;
            l.ForeColor = Theme.Text;
            l.BackColor = Theme.Bg;
            l.AutoSize = true;
            l.Location = new Point(S(x), S(y));
            Controls.Add(l);
        }

        void AddLabel(string text, int x, int y, int w, int h)
        {
            var l = new Label();
            l.Text = text;
            l.ForeColor = Theme.Label;
            l.BackColor = Theme.Bg;
            l.AutoSize = false;
            l.TextAlign = ContentAlignment.MiddleLeft;
            l.Bounds = new Rectangle(S(x), S(y), S(w), S(h));
            Controls.Add(l);
        }

        Slider AddSlider(string text, int x, ref int y, int min, int max, int step, out Label value)
        {
            AddLabel(text, x, y, 124, 28);
            var sl = new Slider(k, min, max, step);
            sl.Bounds = new Rectangle(S(x + 128), S(y), S(190), S(28));
            sl.ValueChanged += (s, e) => OnSlider();
            Controls.Add(sl);
            value = new Label();
            value.AutoSize = false;
            value.TextAlign = ContentAlignment.MiddleRight;
            value.ForeColor = Theme.Dim;
            value.BackColor = Theme.Bg;
            value.Bounds = new Rectangle(S(x + 320), S(y), S(56), S(28));
            Controls.Add(value);
            y += 40;
            return sl;
        }

        Button MakeButton(string text, bool primary)
        {
            var b = new Button();
            b.Text = text;
            b.Font = fButton;
            b.FlatStyle = FlatStyle.Flat;
            b.UseVisualStyleBackColor = false;
            b.Cursor = Cursors.Hand;
            b.FlatAppearance.BorderSize = 0;
            StyleButton(b, primary, true);
            return b;
        }

        static void StyleButton(Button b, bool primary, bool enabled)
        {
            if (primary && enabled)
            {
                b.BackColor = Theme.Accent;
                b.ForeColor = Theme.AccentText;
                b.FlatAppearance.MouseOverBackColor = Theme.AccentHover;
                b.FlatAppearance.MouseDownBackColor = Theme.Accent;
            }
            else
            {
                b.BackColor = Theme.Button;
                b.ForeColor = enabled ? Theme.Text : Theme.Dim;
                b.FlatAppearance.MouseOverBackColor = enabled ? Theme.ButtonHover : Theme.Button;
                b.FlatAppearance.MouseDownBackColor = Theme.Button;
            }
        }

        // ---------------------------------------------------------- данные

        string GetCombo(int i)
        {
            switch (i)
            {
                case 0: return draft.PlayPause;
                case 1: return draft.Next;
                case 2: return draft.Previous;
                case 3: return draft.VolumeUp;
                case 4: return draft.VolumeDown;
                default: return draft.ToggleOverlay;
            }
        }

        void SetCombo(int i, string v)
        {
            switch (i)
            {
                case 0: draft.PlayPause = v; break;
                case 1: draft.Next = v; break;
                case 2: draft.Previous = v; break;
                case 3: draft.VolumeUp = v; break;
                case 4: draft.VolumeDown = v; break;
                default: draft.ToggleOverlay = v; break;
            }
        }

        void LoadDraft()
        {
            loading = true;
            for (int i = 0; i < 6; i++) boxes[i].Value = GetCombo(i);
            slScale.Value = (int)Math.Round(draft.Scale * 100);
            slTransp.Value = 100 - draft.Opacity;
            slSeconds.Value = draft.ShowSeconds;
            tgTrack.Checked = draft.ShowOnTrackChange;
            tgAlways.Checked = draft.AlwaysVisible;
            tgMouse.Checked = draft.MouseControls;
            tgSpin.Checked = draft.Spin;
            segShow.SelectedIndex = draft.ShowMode == "playing" ? 1 : draft.ShowMode == "events" ? 2 : 0;
            segLang.SelectedIndex = L.Resolve(draft.Language) == "en" ? 1 : 0;
            loading = false;
            UpdateValueLabels();
        }

        // Язык меняется сразу: оверлей, меню в трее и это окно перестраиваются на новом языке.
        void OnLanguage()
        {
            if (loading || rebuilding) return;
            draft.Language = segLang.SelectedIndex == 1 ? "en" : "ru";
            app.PreviewLook(draft);
            RebuildUi();
        }

        void RebuildUi()
        {
            bool autostart = tgAutostart.Checked;
            rebuilding = true;
            try { ActiveControl = null; } catch { }
            tips.RemoveAll();
            while (Controls.Count > 0) Controls[0].Dispose();
            BuildUi();
            rebuilding = false;
            tgAutostart.Checked = autostart;
            LoadDraft();
            ValidateHotkeys();
            segLang.Focus();
        }

        void OnSlider()
        {
            UpdateValueLabels();
            if (loading) return;
            draft.Scale = slScale.Value / 100.0;
            draft.Opacity = 100 - slTransp.Value;
            draft.ShowSeconds = slSeconds.Value;
            Preview();
        }

        void UpdateValueLabels()
        {
            lbScale.Text = slScale.Value + " %";
            lbTransp.Text = slTransp.Value + " %";
            lbSeconds.Text = slSeconds.Value + L.T(" с", " s");
        }

        void Preview()
        {
            if (!loading) app.PreviewLook(draft);
        }

        void ShowStatus(string text, bool error)
        {
            status.Text = text;
            status.ForeColor = error ? Theme.Danger : Theme.Dim;
        }

        // Проверяем: всё ли разобралось, нет ли повторов, не занято ли другими программами.
        bool ValidateHotkeys()
        {
            var errors = new List<string>();
            bool blocking = false;
            var code = new uint[6];
            var ok = new bool[6];
            var bad = new bool[6];

            for (int i = 0; i < 6; i++)
            {
                string c = GetCombo(i);
                if (Hotkey.IsDisabled(c)) continue;
                uint m, v;
                if (Hotkey.TryParse(c, out m, out v)) { ok[i] = true; code[i] = (m << 16) | v; }
                else
                {
                    bad[i] = true; blocking = true;
                    errors.Add(L.T("«" + ActionNames[i] + "»: не понял сочетание «" + c + "».",
                                   "\"" + ActionNames[i] + "\": can't read shortcut \"" + c + "\"."));
                }
            }
            for (int i = 0; i < 6; i++)
                for (int j = i + 1; j < 6; j++)
                    if (ok[i] && ok[j] && code[i] == code[j])
                    {
                        bad[i] = bad[j] = true; blocking = true;
                        errors.Add(L.T("«" + ActionNames[i] + "» и «" + ActionNames[j] + "» — одно и то же сочетание.",
                                       "\"" + ActionNames[i] + "\" and \"" + ActionNames[j] + "\" use the same shortcut."));
                    }

            app.SuspendHotkeys();
            try
            {
                for (int i = 0; i < 6; i++)
                    if (ok[i] && !bad[i] && !app.IsComboFree(code[i] >> 16, code[i] & 0xFFFF))
                    {
                        bad[i] = true;
                        errors.Add(Hotkey.Pretty(GetCombo(i)) + L.T(" занято другой программой — выбери другое.",
                                                                     " is taken by another program — pick another one."));
                    }
            }
            finally { app.ResumeHotkeys(); }

            for (int i = 0; i < 6; i++) boxes[i].HasError = bad[i];
            if (errors.Count == 0) ShowStatus(Hint, false);
            else
            {
                if (errors.Count > 2) { errors.RemoveRange(2, errors.Count - 2); errors.Add("…"); }
                ShowStatus(string.Join("\n", errors.ToArray()), true);
            }
            btnSave.Enabled = !blocking;
            StyleButton(btnSave, true, !blocking);
            return !blocking;
        }

        void Save()
        {
            if (!ValidateHotkeys()) return;
            app.ApplyConfig(draft, tgAutostart.Checked);
            saved = true;
            Close();
        }

        void ResetDefaults()
        {
            draft = new Config();
            LoadDraft();
            ValidateHotkeys();
            app.PreviewLook(draft);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (!saved) app.RestoreLook(original);
            app.ResumeHotkeys();
            base.OnFormClosed(e);
        }

        // Тёмный заголовок окна в Windows 11.
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                int on = 1;
                DwmSetWindowAttribute(Handle, 20, ref on, 4);
                int caption = Theme.Bg.R | (Theme.Bg.G << 8) | (Theme.Bg.B << 16);
                DwmSetWindowAttribute(Handle, 35, ref caption, 4);
                int text = Theme.Text.R | (Theme.Text.G << 8) | (Theme.Text.B << 16);
                DwmSetWindowAttribute(Handle, 36, ref text, 4);
            }
            catch { }
        }

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    }

    // ------------------------------------------------------------------ поле горячей клавиши

    // Щелчок — и поле ждёт сочетание. Пока ждёт, горячие клавиши программы отключены,
    // чтобы можно было переназначить и уже занятые ею сочетания.
    class HotkeyBox : Control
    {
        const int WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_SYSKEYDOWN = 0x104, WM_SYSKEYUP = 0x105;

        [DllImport("user32.dll")] static extern short GetKeyState(int vk);

        public event EventHandler CaptureStarted, CaptureEnded, ValueChanged, Rejected;
        public string LastError;

        readonly float k;
        readonly Font chipFont, arrowFont;
        string value = "None";
        bool capturing, hover, hasError;
        uint pendingMods;

        public HotkeyBox(float k)
        {
            this.k = k;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                   | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            TabStop = true;
            Cursor = Cursors.Hand;
            BackColor = Theme.Bg;
            chipFont = new Font("Segoe UI Semibold", 12f * k, FontStyle.Regular, GraphicsUnit.Pixel);
            arrowFont = new Font("Segoe UI Semibold", 16f * k, FontStyle.Regular, GraphicsUnit.Pixel);
        }

        public string Value
        {
            get { return value; }
            set { this.value = string.IsNullOrEmpty(value) ? "None" : value; Invalidate(); }
        }

        public bool HasError
        {
            get { return hasError; }
            set { if (hasError != value) { hasError = value; Invalidate(); } }
        }

        public bool Capturing { get { return capturing; } }

        public void StartCapture() { if (!Focused) Focus(); if (!capturing) Begin(); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (!Focused) Focus();
            if (e.Button == MouseButtons.Left && !capturing) Begin();
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); End(); Invalidate(); }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (capturing) return;
            if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) { Begin(); e.Handled = true; }
            else if (e.KeyCode == Keys.Delete || e.KeyCode == Keys.Back) { SetValue("None"); e.Handled = true; }
        }

        // Перехватываем клавиши раньше формы, чтобы Alt не открывал системное меню,
        // а Esc/Enter/Tab не срабатывали как кнопки окна.
        public override bool PreProcessMessage(ref Message m)
        {
            if (capturing &&(m.Msg == WM_KEYDOWN || m.Msg == WM_SYSKEYDOWN || m.Msg == WM_KEYUP || m.Msg == WM_SYSKEYUP))
            {
                Keys key = (Keys)(int)(m.WParam.ToInt64() & 0xFFFF);
                uint mods = CurrentMods();
                if (m.Msg == WM_KEYDOWN || m.Msg == WM_SYSKEYDOWN) HandleDown(key, mods);
                else { pendingMods = mods; Invalidate(); }
                return true;
            }
            return base.PreProcessMessage(ref m);
        }

        static uint CurrentMods()
        {
            uint m = 0;
            Keys mk = Control.ModifierKeys;
            if ((mk & Keys.Control) != 0) m |= Hotkey.MOD_CONTROL;
            if ((mk & Keys.Alt) != 0) m |= Hotkey.MOD_ALT;
            if ((mk & Keys.Shift) != 0) m |= Hotkey.MOD_SHIFT;
            if ((GetKeyState(0x5B) & 0x8000) != 0 || (GetKeyState(0x5C) & 0x8000) != 0) m |= Hotkey.MOD_WIN;
            return m;
        }

        static bool IsModifier(Keys key)
        {
            switch (key)
            {
                case Keys.ControlKey: case Keys.LControlKey: case Keys.RControlKey:
                case Keys.Menu: case Keys.LMenu: case Keys.RMenu:
                case Keys.ShiftKey: case Keys.LShiftKey: case Keys.RShiftKey:
                case Keys.LWin: case Keys.RWin:
                    return true;
            }
            return false;
        }

        void HandleDown(Keys key, uint mods)
        {
            if (IsModifier(key)) { pendingMods = mods; Invalidate(); return; }
            if (mods == 0 && key == Keys.Escape) { End(); return; }
            if (mods == 0 && (key == Keys.Back || key == Keys.Delete)) { SetValue("None"); End(); return; }

            string err;
            string combo = Hotkey.FromKeys(mods, key, out err);
            if (combo == null)
            {
                LastError = err;
                var h = Rejected;
                if (h != null) h(this, EventArgs.Empty);
                return;
            }
            SetValue(combo);
            End();
        }

        void SetValue(string v)
        {
            value = v;
            Invalidate();
            var h = ValueChanged;
            if (h != null) h(this, EventArgs.Empty);
        }

        void Begin()
        {
            capturing = true;
            pendingMods = 0;
            Invalidate();
            var h = CaptureStarted;
            if (h != null) h(this, EventArgs.Empty);
        }

        void End()
        {
            if (!capturing) return;
            capturing = false;
            pendingMods = 0;
            Invalidate();
            var h = CaptureEnded;
            if (h != null) h(this, EventArgs.Empty);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            float bw = capturing ? Math.Max(1.5f, 1.5f * k) : Math.Max(1f, k);
            var r = new RectangleF(bw / 2, bw / 2, Width - bw - 0.5f, Height - bw - 0.5f);
            Color border = capturing ? Theme.Accent
                         : hasError ? Theme.Danger
                         : (Focused || hover) ? Theme.BorderHover : Theme.Border;
            using (GraphicsPath p = Gfx.RoundRect(r, 7 * k))
            {
                using (var b = new SolidBrush(capturing ? Theme.FieldActive : Theme.Field)) g.FillPath(b, p);
                using (var pen = new Pen(border, bw)) g.DrawPath(pen, p);
            }

            List<string> tokens;
            string trailing = null;
            if (capturing)
            {
                tokens = Hotkey.ModTokens(pendingMods);
                trailing = tokens.Count == 0 ? L.T("Нажми сочетание…", "Press a shortcut…") : "…";
            }
            else if (Hotkey.IsDisabled(value)) { tokens = new List<string>(); trailing = L.T("Не назначено", "Not set"); }
            else tokens = Hotkey.Tokens(value);

            float x = 7 * k;
            foreach (string t in tokens) x = DrawChip(g, Hotkey.DisplayToken(t), x);
            if (trailing != null)
            {
                int tx = (int)(x + (tokens.Count > 0 ? 0 : 3 * k));
                TextRenderer.DrawText(g, trailing, Font, new Rectangle(tx, 0, Math.Max(1, Width - tx - (int)(6 * k)), Height),
                    capturing && tokens.Count == 0 ? Theme.Accent : Theme.Dim,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }
        }

        float DrawChip(Graphics g, string text, float x)
        {
            bool arrow = text.Length == 1 && text[0] >= '←' && text[0] <= '↓';
            Font font = arrow ? arrowFont : chipFont;
            Size ts = TextRenderer.MeasureText(g, text, font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            float h = 22 * k, w = Math.Max(ts.Width + 14 * k, arrow ? 26 * k : 0);
            var r = new RectangleF(x, (Height - h) / 2f, w, h);
            using (GraphicsPath p = Gfx.RoundRect(r, 5 * k))
            using (var b = new SolidBrush(Theme.Chip))
                g.FillPath(b, p);
            TextRenderer.DrawText(g, text, font, Rectangle.Round(r), Theme.Text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            return x + w + 5 * k;
        }
    }

    // ------------------------------------------------------------------ выбор из нескольких вариантов (язык)

    class Segmented : Control
    {
        public event EventHandler ValueChanged;
        readonly float k;
        readonly string[] items;
        readonly Font boldFont;
        int index, hoverIdx = -1;

        public Segmented(float k, string[] items)
        {
            this.k = k;
            this.items = items;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                   | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            TabStop = true;
            Cursor = Cursors.Hand;
            BackColor = Theme.Bg;
            boldFont = new Font("Segoe UI Semibold", 12.5f * k, FontStyle.Regular, GraphicsUnit.Pixel);
        }

        // Без события — для заполнения формы.
        public int SelectedIndex
        {
            get { return index; }
            set { if (value >= 0 && value < items.Length && value != index) { index = value; Invalidate(); } }
        }

        void Pick(int i)
        {
            if (i == index) return;
            index = i;
            Invalidate();
            var h = ValueChanged;
            if (h != null) h(this, EventArgs.Empty);
        }

        // Ширина кнопок — по длине их текста, чтобы длинные подписи не слипались.
        float[] Widths()
        {
            float pad = 3 * k, avail = Math.Max(1f, Width - 2 * pad), sum = 0;
            var w = new float[items.Length];
            for (int i = 0; i < items.Length; i++)
            {
                w[i] = TextRenderer.MeasureText(items[i], boldFont, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width + 16 * k;
                sum += w[i];
            }
            for (int i = 0; i < items.Length; i++) w[i] = w[i] / sum * avail;
            return w;
        }

        int At(int x)
        {
            float[] w = Widths();
            float edge = 3 * k;
            for (int i = 0; i < w.Length; i++) { edge += w[i]; if (x < edge) return i; }
            return items.Length - 1;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            Focus();
            Pick(At(e.X));
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int i = At(e.X);
            if (i != hoverIdx) { hoverIdx = i; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hoverIdx = -1; Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

        protected override bool IsInputKey(Keys keyData)
        {
            Keys kc = keyData & Keys.KeyCode;
            return kc == Keys.Left || kc == Keys.Right || base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Left) { Pick(Math.Max(0, index - 1)); e.Handled = true; }
            else if (e.KeyCode == Keys.Right) { Pick(Math.Min(items.Length - 1, index + 1)); e.Handled = true; }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float bw = Math.Max(1f, k);
            var r = new RectangleF(bw / 2, bw / 2, Width - bw - 0.5f, Height - bw - 0.5f);
            using (GraphicsPath p = Gfx.RoundRect(r, 7 * k))
            {
                using (var b = new SolidBrush(Theme.Field)) g.FillPath(b, p);
                using (var pen = new Pen(Focused ? Theme.BorderHover : Theme.Border, bw)) g.DrawPath(pen, p);
            }
            float pad = 3 * k;
            float[] sw = Widths();
            float sx = pad;
            for (int i = 0; i < items.Length; i++)
            {
                var seg = new RectangleF(sx, pad, sw[i], Height - 2 * pad);
                sx += sw[i];
                bool sel = i == index;
                if (sel || i == hoverIdx)
                    using (GraphicsPath sp = Gfx.RoundRect(seg, 5 * k))
                    using (var sb = new SolidBrush(sel ? Theme.Accent : Theme.FieldActive))
                        g.FillPath(sb, sp);
                TextRenderer.DrawText(g, items[i], sel ? boldFont : Font, Rectangle.Round(seg), sel ? Theme.AccentText : Theme.Label,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
        }
    }

    // ------------------------------------------------------------------ переключатель

    class Toggle : Control
    {
        public event EventHandler CheckedChanged;
        readonly float k;
        bool on, hover;

        public Toggle(float k, string text)
        {
            this.k = k;
            Text = text;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                   | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            TabStop = true;
            Cursor = Cursors.Hand;
            BackColor = Theme.Bg;
            ForeColor = Theme.Label;
        }

        public bool Checked
        {
            get { return on; }
            set { if (on != value) { on = value; Invalidate(); var h = CheckedChanged; if (h != null) h(this, EventArgs.Empty); } }
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button != MouseButtons.Left) return;
            Focus();
            Checked = !Checked;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Space) { Checked = !Checked; e.Handled = true; }
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float sw = 38 * k, sh = 20 * k;
            var r = new RectangleF(2 * k, (Height - sh) / 2f, sw, sh);
            if (Focused)
                using (GraphicsPath fp = Gfx.RoundRect(RectangleF.Inflate(r, 2.5f * k, 2.5f * k), sh / 2 + 2.5f * k))
                using (var fpen = new Pen(Color.FromArgb(150, Theme.Accent), Math.Max(1f, 1.5f * k)))
                    g.DrawPath(fpen, fp);
            using (GraphicsPath p = Gfx.RoundRect(r, sh / 2))
            using (var b = new SolidBrush(on ? (hover ? Theme.AccentHover : Theme.Accent) : (hover ? Theme.OffHover : Theme.Off)))
                g.FillPath(b, p);
            float d = sh - 6 * k;
            float kx = on ? r.Right - 3 * k - d : r.X + 3 * k;
            using (var kb = new SolidBrush(on ? Color.White : Color.FromArgb(214, 216, 224)))
                g.FillEllipse(kb, kx, r.Y + 3 * k, d, d);

            int tx = (int)(r.Right + 12 * k);
            TextRenderer.DrawText(g, Text, Font, new Rectangle(tx, 0, Math.Max(1, Width - tx), Height), ForeColor,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }

    // ------------------------------------------------------------------ ползунок

    class Slider : Control
    {
        public event EventHandler ValueChanged;
        readonly float k;
        readonly int min, max, step;
        int val;
        bool drag, hover;

        public Slider(float k, int min, int max, int step)
        {
            this.k = k; this.min = min; this.max = max; this.step = Math.Max(1, step);
            val = min;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                   | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            TabStop = true;
            Cursor = Cursors.Hand;
            BackColor = Theme.Bg;
        }

        public int Value
        {
            get { return val; }
            set { int v = Snap(value); if (v != val) { val = v; Invalidate(); Raise(); } }
        }

        int Snap(int v)
        {
            v = Math.Max(min, Math.Min(max, v));
            v = min + (int)Math.Round((v - min) / (double)step) * step;
            return Math.Max(min, Math.Min(max, v));
        }

        void Raise() { var h = ValueChanged; if (h != null) h(this, EventArgs.Empty); }

        float Pad { get { return 10 * k; } }

        int ValueAt(int x)
        {
            float w = Math.Max(1f, Width - 2 * Pad);
            double t = Math.Max(0, Math.Min(1, (x - Pad) / w));
            return (int)Math.Round(min + t * (max - min));
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            Focus();
            drag = true;
            Capture = true;
            Value = ValueAt(e.X);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (drag) Value = ValueAt(e.X);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            drag = false;
            Capture = false;
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            Value = val + Math.Sign(e.Delta) * step;
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

        protected override bool IsInputKey(Keys keyData)
        {
            switch (keyData & Keys.KeyCode)
            {
                case Keys.Left: case Keys.Right: case Keys.Up: case Keys.Down: return true;
            }
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            switch (e.KeyCode)
            {
                case Keys.Left: case Keys.Down: Value = val - step; break;
                case Keys.Right: case Keys.Up: Value = val + step; break;
                case Keys.PageDown: Value = val - step * 5; break;
                case Keys.PageUp: Value = val + step * 5; break;
                case Keys.Home: Value = min; break;
                case Keys.End: Value = max; break;
                default: return;
            }
            e.Handled = true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float cy = Height / 2f, x0 = Pad, x1 = Width - Pad;
            float t = max > min ? (val - min) / (float)(max - min) : 0f;
            float tx = x0 + t * (x1 - x0);
            float th = 4 * k;

            using (GraphicsPath p = Gfx.RoundRect(new RectangleF(x0, cy - th / 2, x1 - x0, th), th / 2))
            using (var b = new SolidBrush(Theme.Off))
                g.FillPath(b, p);
            if (tx - x0 > 0.5f)
                using (GraphicsPath p = Gfx.RoundRect(new RectangleF(x0, cy - th / 2, tx - x0, th), th / 2))
                using (var b = new SolidBrush(Theme.Accent))
                    g.FillPath(b, p);

            float rad = (drag || hover) ? 8.5f * k : 7.5f * k;
            if (Focused)
                using (var fb = new SolidBrush(Color.FromArgb(70, Theme.Accent)))
                    g.FillEllipse(fb, tx - rad - 3.5f * k, cy - rad - 3.5f * k, (rad + 3.5f * k) * 2, (rad + 3.5f * k) * 2);
            using (var kb = new SolidBrush(Color.White))
                g.FillEllipse(kb, tx - rad, cy - rad, rad * 2, rad * 2);
        }
    }
}
