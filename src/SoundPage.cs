using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RedTune
{
    // 18.11: «Звук». The output device, its volume, and Red Tune's own processing in Windows' chain of effects for it
    // (AudioFx): equalizer, compressor, limiter. Every device has its own settings. Moving anything goes to the effect at
    // once (the shared file); the system itself is touched only by the switch «Обработка звука» the first time it is
    // turned on for a device, and by «Убрать», both after a question.
    class SoundPage : Page
    {
        readonly Settings settings;
        List<SoundDevice> devices = new List<SoundDevice>();
        SoundDevice dev; SoundParams p = SoundParams.Start(); AudioFx.FxState state = new AudioFx.FxState();
        bool loading, busy;

        readonly DropButton devBtn = new DropButton(), presetBtn = new DropButton();
        readonly FlatButton makeDefault = new FlatButton("Сделать основным"), savePreset = new FlatButton("Сохранить как…"), dropPreset = new FlatButton("Удалить"), remove = new FlatButton("Убрать");
        readonly Toggle onT = new Toggle(), eqT = new Toggle(), compT = new Toggle(), limT = new Toggle(), muteT = new Toggle(), nahimicT = new Toggle();
        readonly Slider vol = new Slider(0, 100, 50, "%"), thr = new Slider(-60, 0, -18, "дБ"), ratio = new Slider(10, 200, 30, ""),
            mk = new Slider(-12, 24, 0, "дБ"), split = new Slider(80, 600, 250, "Гц"), split2 = new Slider(1200, 12000, 4000, "Гц");
        // 19.11 (the user's wish): the compressor on the whole sound or in two or three bands, each with settings of its own.
        // band: which of SoundParams.Parts the sliders, the points and the dials show (0 the bass, 1 the middle, 2 the top)
        readonly Segmented partsSeg = new Segmented("Одна", "Две", "Три"), bandSeg = new Segmented("Низ", "Середина", "Верх");        int band, bandNames = 3;
        readonly Row devRow, onRow, volRow, presetRow, eqRow, compRow, limRow, nahimicRow, removeRow, split1Row, split2Row, bandRow, partsRow, thrRow, ratioRow, mkRow;
        bool fitting, rowsBands, rowsSplits, rowsDials; // rows*: the rows of the bands / of the splits are needed (the picture has no room for them)
        readonly EqGraph graph = new EqGraph();
        readonly CompGraph cgraph = new CompGraph();
        readonly LevelMeter meter = new LevelMeter();
        readonly Timer tick = new Timer { Interval = 33 }, saver = new Timer { Interval = 600 };
        uint lastBeat; int quiet = 100, ticks;

        // 19.11 (the user's wish): Ctrl+Z / Ctrl+Y on «Звук», as «Видеокарта» has for its curve. A step is everything of
        // the sound settings of the device shown (the equalizer, the compressor and its bands, the limiter, a preset
        // picked) except the processing switch itself, which attaches the effect in Windows and is not a setting to
        // step through. A run of changes with less than half a second between them, or made while a mouse button is
        // held (a point dragged, a wheel turned), is one step. The steps are kept per device, until another is shown.
        readonly List<SoundParams> back = new List<SoundParams>(), forward = new List<SoundParams>();
        SoundParams seen; Guid seenDev; bool gesture; // seen: the settings as of the last change taken note of (a copy)
        readonly Timer gestureEnd = new Timer { Interval = 500 };
        internal bool Dry; // the check: nothing goes to the effect or to the disk
        static string Key(SoundParams x) { var c = x.Clone(); c.On = false; return c.ToString(); }

        // the device shown (anew, or another): the steps stay only if these are the settings they belong to
        void Baseline()
        {
            if (dev != null && seen != null && seenDev == dev.Id && Key(seen) == Key(p)) return;
            back.Clear(); forward.Clear(); gesture = false; gestureEnd.Stop();
            seen = dev == null ? null : p.Clone(); if (dev != null) seenDev = dev.Id;
        }

        // a change went to the effect: the settings before it are a step back (once per run of changes)
        void Record()
        {
            if (dev == null || seen == null || Key(p) == Key(seen)) return;
            if (!gesture) { back.Add(seen); if (back.Count > 200) back.RemoveAt(0); forward.Clear(); gesture = true; }
            seen = p.Clone();
            gestureEnd.Stop(); gestureEnd.Start();
        }

        bool Step(List<SoundParams> from, List<SoundParams> to)
        {
            if (dev == null || from.Count == 0) return false;
            to.Add(p.Clone());
            var snap = from[from.Count - 1]; from.RemoveAt(from.Count - 1);
            bool keepOn = p.On; p = snap.Clone(); p.On = keepOn; AudioFx.ByDevice[dev.Id] = p;
            seen = p.Clone(); gesture = false; gestureEnd.Stop();
            Show(p); Push();
            return true;
        }

        // Ctrl+Z, Ctrl+Y, Ctrl+Shift+Z from the main window while this page is shown
        public bool HandleKey(Keys keyData)
        {
            bool undo = keyData == (Keys.Control | Keys.Z), redo = keyData == (Keys.Control | Keys.Y) || keyData == (Keys.Control | Keys.Shift | Keys.Z);
            if (!Visible || dev == null || (!undo && !redo)) return false;
            if (Control.MouseButtons != MouseButtons.None) return true; // something is being dragged: not under its hand
            if (undo) Step(back, forward); else Step(forward, back);
            return true;
        }

        // --sound-undo-check: the steps back and forward on the device shown, with nothing sent to the effect or saved
        internal string UndoCheck()
        {
            var sb = new System.Text.StringBuilder();
            Action<bool, string> line = (ok, text) => sb.AppendLine((ok ? "ok    " : "FAIL  ") + text);
            if (dev == null) return "no output device: nothing to check\r\n";
            Dry = true; Baseline();
            var z = Keys.Control | Keys.Z; var y = Keys.Control | Keys.Y;
            string k0 = Key(p); int t0 = thr.Value, r0 = ratio.Value, m0 = mk.Value;
            line(back.Count == 0 && HandleKey(z) && Key(p) == k0, "nothing changed yet: Ctrl+Z is taken and changes nothing");
            thr.Value = t0 == -20 ? -21 : -20; Changed(); ratio.Value = r0 == 40 ? 41 : 40; Changed(); // (a slider set from code reports nothing: the check does, as the slider would)
            line(back.Count == 1 && p.Parts[band].Thr == thr.Value && Key(p) != k0, "the threshold and then the ratio moved in one run: one step back (" + back.Count + ")");
            gesture = false; gestureEnd.Stop();
            mk.Value = m0 == 5 ? 6 : 5; Changed(); string k2 = Key(p);
            line(back.Count == 2, "after a pause the gain after moved: a second step (" + back.Count + ")");
            HandleKey(z);
            line(mk.Value == m0 && p.Parts[band].Makeup == m0 && thr.Value != t0 && back.Count == 1 && forward.Count == 1, "Ctrl+Z: the gain after is back at " + mk.Value + " dB, the threshold stays moved (" + thr.Value + "); the slider follows");
            HandleKey(z);
            line(Key(p) == k0 && thr.Value == t0 && ratio.Value == r0 && back.Count == 0 && forward.Count == 2, "Ctrl+Z again: the settings are those of the start");
            HandleKey(z);
            line(Key(p) == k0 && forward.Count == 2, "a third Ctrl+Z: nothing more to take back");
            HandleKey(y); HandleKey(Keys.Control | Keys.Shift | Keys.Z);
            line(Key(p) == k2 && back.Count == 2 && forward.Count == 0, "Ctrl+Y and Ctrl+Shift+Z: both steps forward again");
            bool onWas = p.On; HandleKey(z); HandleKey(z);
            line(p.On == onWas, "the processing switch is not touched by the steps");
            HandleKey(y); gesture = false; thr.Value = thr.Value - 3; Changed();
            line(forward.Count == 0 && back.Count == 2, "a new change after a step back: the steps forward are dropped");
            HandleKey(z); HandleKey(z);
            line(Key(p) == k0, "all taken back: the settings of the start (" + (Key(p) == k0 ? "same" : "DIFFERENT") + ")");
            return sb.ToString();
        }

        static string Signed(int v, string unit) { return (v > 0 ? "+" : v < 0 ? "−" : "") + Math.Abs(v) + " " + unit; }

        public SoundPage(Settings s)
        {
            settings = s;
            Section("Устройство");
            devRow = Row("Устройство вывода", null, makeDefault, devBtn);
            volRow = Row("Громкость", null, muteT, vol);
            onRow = Row("Обработка звука", null, onT);
            Add(meter);

            Section("Эквалайзер");
            presetRow = Row("Пресет", "Готовые настройки эквалайзера и компрессора; свои можно сохранить", dropPreset, savePreset, presetBtn);
            eqRow = Row("Эквалайзер", "Точки на графике тянутся мышью: влево-вправо частота, вверх-вниз усиление. Колесо на точке меняет ширину", eqT);
            Add(graph);
            // 20.11: «Общее усиление» is no longer a row here: it is the first strip of the equalizer (EqGraph, Part.Master)

            Section("Компрессор и лимитер"); // 19.11: one section and one picture for both (the user's wish)
            compRow = Row("Компрессор", "Убавляет громкие места: тихое слышнее, громкое не бьёт по ушам", compT);
            // 20.11 (the user's wish): the limiter's switch right under the compressor's; its ceiling is a fader in the picture,
            // right of the monitor (CompGraph, Part.CeilFader), no longer a row
            limRow = Row("Лимитер", "Не выпускает звук выше потолка, сколько бы ни поднял эквалайзер: без хрипа и перегруза", limT);
            Add(cgraph); // 19.11: the compressor drawn, with its settings as points and dials (the user's wish)
            cgraph.ByFreq = s.CompByFreq;
            // the bands: how many, where they part, and which one the rows and the picture show. (Two bands is what «Бас
            // отдельно» was: Nahimic's stabilizer measured on the R7000P works so, with the split near 280 Hz.)
            // 19.11 (the user's order): both choices stand in the picture, between the dials and the monitor; these two rows
            // show only in a window too narrow for that column (ShowParts)
            partsRow = Row("Полосы сжатия", "Каждая полоса сжимается порознь и со своими настройками", partsSeg);
            split.Step = 10; split2.Step = 100;
            // the choice of the band right under their number (the user's order, 19.11), then where the bands part
            bandRow = Row("Настраиваемая полоса", "Точки, ручки и ползунки ниже относятся к ней", bandSeg);
            split1Row = Row("Граница низа", "Всё, что ниже, — полоса «Низ»", split);
            split2Row = Row("Граница верха", "Всё, что выше, — полоса «Верх»", split2);
            // 19.11 (the user's wish): a split moved turns the picture's monitor to the bands, so it is seen moving. 20.11 (the
            // user): the number of bands and the band being set leave the monitor as it is
            partsSeg.Changed += (o, e) =>
            {
                if (loading) return;
                int n = Math.Max(0, partsSeg.Selected) + 1; p.Split = n >= 2 ? split.Value : 0; p.Split2 = n >= 3 ? split2.Value : 0;
                Changed(); ShowParts();
            };
            split.Changed += (o, e) => { if (!loading) View(true); };
            split2.Changed += (o, e) => { if (!loading) View(true); };
            bandSeg.Changed += (o, e) =>
            {
                if (loading) return;
                var act = p.ActiveParts; band = act[Math.Max(0, Math.Min(act.Length - 1, bandSeg.Selected))];
                ShowBand();
            };
            // 19.11 (the user's wish): these three are dials of the picture; their rows show only in a window too narrow for them
            thrRow = Row("Порог", "Громкость, с которой начинается сжатие", thr);
            ratio.Caption = v => (v / 10.0).ToString("0.#", CultureInfo.InvariantCulture) + " : 1";
            ratioRow = Row("Степень сжатия", "Во сколько раз убавляется то, что выше порога", ratio);
            // 19.11: the attack, the release and the knee have no rows any more (the user's wish): they are the dials of the picture
            mk.Caption = v => Signed(v, "дБ");
            mkRow = Row("Усиление после", "Возвращает громкость, которую забрало сжатие", mk);


            Section("Система");
            nahimicRow = Row("Эффекты производителя", null, nahimicT);
            nahimicT.CheckedChanged += (o, e) => { if (!loading) SwitchNahimic(nahimicT.Checked); };
            removeRow = Row("Убрать обработку с устройства", "Red Tune уберёт свой обработчик из цепочки эффектов этого устройства; настройки останутся", remove);

            devBtn.Changed += (o, e) => { if (loading || devBtn.Index < 0 || devBtn.Index >= devices.Count) return; EndListen(); dev = devices[devBtn.Index]; LoadDevice(); };
            makeDefault.Click += (o, e) =>
            {
                if (dev == null) return;
                var why = AudioFx.SetDefault(dev.Id);
                if (why != null) devRow.SetSub("Не удалось: " + why); else Reload();
            };
            vol.Changed += (o, e) => { if (!loading && dev != null) AudioFx.SetVolume(dev.Id, vol.Value); };
            muteT.CheckedChanged += (o, e) => { if (!loading && dev != null) AudioFx.SetMute(dev.Id, !muteT.Checked); };
            onT.CheckedChanged += (o, e) => { if (!loading) SwitchOn(onT.Checked); };
            eqT.CheckedChanged += (o, e) => Changed(o);
            compT.CheckedChanged += (o, e) => Changed(o);
            limT.CheckedChanged += (o, e) => Changed(o);
            foreach (var sl in new[] { thr, ratio, mk, split, split2 }) sl.Changed += (o, e) => Changed(o);
            graph.Changed += (o, e) => Changed(o);
            // a point or a dial of the compressor's picture moved: it wrote into the settings itself, the sliders follow
            cgraph.Changed += (o, e) =>
            {
                if (loading || dev == null) return;
                TickIfDue();
                band = cgraph.Band; // the number of bands changed in the picture may have moved it
                loading = true;
                var q = p.Parts[band];
                thr.Value = (int)Math.Round(q.Thr); ratio.Value = (int)Math.Round(q.Ratio * 10); mk.Value = (int)Math.Round(q.Makeup);
                if (p.Split > 0) split.Value = (int)Math.Round(p.Split); // a split dragged in the picture
                if (p.Split2 > 0) split2.Value = (int)Math.Round(p.Split2);
                cgraph.Ghost1 = split.Value; cgraph.Ghost2 = split2.Value;
                loading = false;
                ShowParts(); // the rows of the splits follow the number of bands
                Push(); ShowPreset();
            };
            // the picture got or lost the room for its bands' column: the two rows go or come back
            cgraph.SizeChanged += (o, e) =>
            {
                if (fitting || loading || (rowsBands == !cgraph.HasBands && rowsSplits == !cgraph.HasSixDials && rowsDials == !cgraph.HasSixDials)) return;
                fitting = true; try { ShowParts(); } finally { fitting = false; }
            };
            // S or M of a band pressed: to the effect at once
            cgraph.ListenChanged += (o, e) => { if (!loading) Push(); };
            // a column of the picture clicked: that band is the one being set
            cgraph.BandPicked += (o, e) => { if (loading) return; band = cgraph.Band; ShowParts(); };
            cgraph.ViewPicked += (o, e) => View(cgraph.ByFreq);
            presetBtn.Changed += (o, e) =>
            {
                if (loading || presetBtn.Index < 0 || presetBtn.Index >= AudioFx.Presets.Count) return;
                var keepOn = p.On;
                p = AudioFx.Presets[presetBtn.Index].P.Clone(); p.On = keepOn;
                if (dev != null) AudioFx.ByDevice[dev.Id] = p;
                Show(p); Push();
            };
            savePreset.Click += (o, e) =>
            {
                var name = InputDialog.Ask(FindForm(), "Новый пресет", "Название", L.T("Мой пресет"));
                if (name == null) return;
                var old = AudioFx.Presets.FirstOrDefault(x => x.Name == name);
                if (old != null && old.BuiltIn) { Msg.Show(FindForm(), "Такое название уже есть", "Red Tune"); return; }
                if (old != null) AudioFx.Presets.Remove(old);
                AudioFx.Presets.Add(new SoundPreset { Name = name, P = p.Clone() });
                AudioFx.Save(); ShowPreset();
            };
            dropPreset.Click += (o, e) =>
            {
                int i = presetBtn.Index; if (i < 0 || i >= AudioFx.Presets.Count || AudioFx.Presets[i].BuiltIn) return;
                AudioFx.Presets.RemoveAt(i); AudioFx.Save(); ShowPreset();
            };
            remove.Click += (o, e) => RemoveFromDevice();
            saver.Tick += (o, e) => { saver.Stop(); AudioFx.Save(); };
            tick.Tick += (o, e) => Tick();
            VisibleChanged += (o, e) => { if (Visible) { Reload(); tick.Start(); } else { tick.Stop(); EndListen(); } };
            // a run of changes ends half a second after the last one, but not while the mouse still holds something
            gestureEnd.Tick += (o, e) => { if (Control.MouseButtons != MouseButtons.None) return; gestureEnd.Stop(); gesture = false; };
            Disposed += (o, e) => { try { EndListen(); } catch { } tick.Dispose(); saver.Dispose(); gestureEnd.Dispose(); };
        }

        // the devices anew (one may have been plugged in), the same one kept when it is still there
        void Reload()
        {
            AudioFx.Refresh();
            devices = AudioFx.Devices();
            var keep = dev == null ? null : devices.FirstOrDefault(d => d.Id == dev.Id);
            dev = keep ?? devices.FirstOrDefault(d => d.Default) ?? devices.FirstOrDefault();
            loading = true;
            devBtn.Set(devices.Select(d => d.Full).ToArray(), dev == null ? -1 : devices.IndexOf(dev));
            loading = false;
            LoadDevice();
        }

        void LoadDevice()
        {
            if (dev == null)
            {
                devRow.SetSub("Нет включённых устройств вывода"); state = new AudioFx.FxState(); p = SoundParams.Start();
                Show(p); Status(null); return;
            }
            p = AudioFx.For(dev.Id);
            state = AudioFx.State(dev.Id);
            if (!state.Attached && p.On) p.On = false; // the effect was taken off outside the program (a driver update): the switch says so
            makeDefault.Visible = !dev.Default;
            devRow.SetSub(dev.Default ? "Основное: сюда играют все программы" : "Не основное: программы играют в другое устройство");
            devRow.PerformLayout();
            loading = true;
            int v = AudioFx.GetVolume(dev.Id); vol.Enabled = v >= 0; if (v >= 0) vol.Value = v;
            var mute = AudioFx.GetMute(dev.Id); muteT.Set(mute != true);
            loading = false;
            lastBeat = 0; quiet = 100;
            Baseline();
            Show(p); Status(AudioFx.Meters(dev.Id));
        }

        // the settings onto the controls
        void Show(SoundParams sp)
        {
            loading = true;
            onT.Set(sp.On && state.Attached); eqT.Set(sp.Eq); compT.Set(sp.Comp); limT.Set(sp.Limit);
            if (sp.Split > 0) split.Value = (int)Math.Round(sp.Split);
            if (sp.Split2 > 0) split2.Value = (int)Math.Round(sp.Split2);
            graph.P = sp; graph.Invalidate();
            cgraph.P = sp; cgraph.Ghost1 = split.Value; cgraph.Ghost2 = split2.Value; cgraph.Invalidate();
            loading = false;
            ShowParts(); ShowPreset(); Dim();
        }

        // how many bands the compressor works in: the rows that belong to more than one band, and the choice of the band
        void ShowParts()
        {
            int n = p.PartCount; var act = p.ActiveParts;
            if (!act.Contains(band)) band = act[0];
            bool was = loading; loading = true;
            partsSeg.Selected = n - 1;
            int want = n == 2 ? 2 : 3;
            if (bandNames != want) { bandSeg.SetItems(want == 2 ? new[] { "Низ", "Верх" } : new[] { "Низ", "Середина", "Верх" }); bandNames = want; bandRow.PerformLayout(); }
            bandSeg.Selected = Array.IndexOf(act, band);
            loading = was;
            // 19.11 (the user's wish): the splits are moved in the picture only (the lines between the columns of the monitor
            // by frequencies); their two rows, like the two of the bands, show only where the picture has no room for that.
            // The sliders themselves stay as the keepers of the values.
            rowsBands = !cgraph.HasBands; rowsSplits = !cgraph.HasSixDials; rowsDials = !cgraph.HasSixDials; // (the splits have dials of their own in the second column's picture)
            bool r1 = n >= 2 && rowsSplits, r2 = n >= 3 && rowsSplits, rp = rowsBands, rb = rp && n >= 2;
            if (thrRow.Visible != rowsDials || ratioRow.Visible != rowsDials || mkRow.Visible != rowsDials) { thrRow.Visible = ratioRow.Visible = mkRow.Visible = rowsDials; Relayout(); }
            if (split1Row.Visible != r1 || split2Row.Visible != r2 || bandRow.Visible != rb || partsRow.Visible != rp) { split1Row.Visible = r1; split2Row.Visible = r2; bandRow.Visible = rb; partsRow.Visible = rp; Relayout(); }
            ShowBand();
        }

        // the band the rows and the picture show: its settings onto the sliders
        void ShowBand()
        {
            var q = p.Parts[band]; bool was = loading; loading = true;
            thr.Value = (int)Math.Round(q.Thr); ratio.Value = (int)Math.Round(q.Ratio * 10); mk.Value = (int)Math.Round(q.Makeup);
            loading = was;
            cgraph.Band = band; cgraph.Invalidate();
        }

        // which monitor the compressor's picture shows (false the last seconds, true the bands); kept between runs
        void View(bool freq)
        {
            cgraph.SetView(freq);
            if (settings.CompByFreq != freq) { settings.CompByFreq = freq; Store.SaveSettings(settings); }
        }

        // Listening to the bands (S and M in the picture) ends when the page is left, another device is shown or the
        // program goes: a band left silent by mistake would otherwise stay so with nothing on the screen to say why.
        void EndListen()
        {
            cgraph.ClearListen();
            if (dev == null || p.Mute == 0) return;
            p.Mute = 0;
            if (!Dry && state.Attached) AudioFx.Write(dev.Id, p);
        }

        // what is switched off shows grey
        void Dim() { Dim(true, true, true); }

        // 20.11 (user: «Общее усиление» and «Потолок» lagged under the hand, the volume did not): every move of a slider
        // used to repaint BOTH big pictures at once (~25 ms at 150 %), even when it changed nothing in them — the
        // pre-gain is drawn in neither. Now only the picture the control shows something in is repainted at once;
        // the others only when the page's moment was due (so the spectrum and the history keep moving during a drag).
        // now = false (a toggle clicked): the pictures are only marked; painting them at once (12-25 ms) ate the start of the
        // toggle's slide, and nothing there follows the hand
        void Dim(bool eqPic, bool compPic, bool due, bool now = true)
        {
            bool eqWas = graph.Dim, compWas = cgraph.Dim;
            graph.Dim = !p.Eq;
            foreach (var sl in new[] { thr, ratio, mk, split, split2 }) sl.Enabled = p.Comp;
            partsSeg.Enabled = bandSeg.Enabled = p.Comp;
            cgraph.Dim = !p.Comp;
            eqPic |= graph.Dim != eqWas; compPic |= cgraph.Dim != compWas;
            if (eqPic) graph.Invalidate();
            if (compPic) cgraph.Invalidate();
            // a slider of the page dragged: the pictures follow at once, not when the mouse rests (see EqGraph.Fire)
            if (!now) return;
            if (eqPic || due) graph.Update();
            if (compPic || due) cgraph.Update();
        }

        // for the off-screen render: the compressor's picture with a sound in it, scrolled into view
        public void DemoComp() { var v = Environment.GetEnvironmentVariable("RT_COMP_VIEW"); if (v != null) cgraph.ByFreq = v == "freq"; cgraph.Demo(); AutoScrollPosition = new Point(0, cgraph.Top - AutoScrollPosition.Y - Theme.S(150)); }

        static bool Same(SoundParams a, SoundParams b)
        {
            var x = a.Clone(); var y = b.Clone(); x.On = y.On = false;
            // the settings of a band that is not at work do not make two sets differ
            foreach (var z in new[] { x, y }) { var act = z.ActiveParts; for (int i = 1; i < 3; i++) if (!act.Contains(i)) z.Parts[i] = z.Parts[0].Clone(); }
            return x.ToString() == y.ToString();
        }

        void ShowPreset()
        {
            int at = AudioFx.Presets.FindIndex(x => Same(x.P, p));
            bool was = loading; loading = true;
            presetBtn.Set(AudioFx.Presets.Select(x => x.Name).ToArray(), at);
            if (at < 0) presetBtn.Text = "Свои настройки" + "  ▾";
            loading = was;
            dropPreset.Visible = at >= 0 && !AudioFx.Presets[at].BuiltIn;
            presetRow.PerformLayout();
        }

        // a control moved: into the settings, to the effect, and (a little later) to the disk
        void Changed(object from)
        {
            if (loading || dev == null) return;
            bool due = TickIfDue();
            p.Eq = eqT.Checked; p.Comp = compT.Checked; p.Limit = limT.Checked;
            int parts = p.PartCount;
            // the pre-gain and the limiter's ceiling are set in the pictures themselves (EqGraph's first strip, CompGraph's fader)
            p.Split = parts >= 2 ? split.Value : 0; p.Split2 = parts >= 3 ? split2.Value : 0;
            var q = p.Parts[band]; q.Thr = thr.Value; q.Ratio = ratio.Value / 10f; q.Makeup = mk.Value;
            cgraph.Ghost1 = split.Value; cgraph.Ghost2 = split2.Value;
            Push();
            // what this control shows itself in: the EQ picture draws itself (EqGraph.Fire)
            ShowPreset(); Dim(from == eqT, from != graph && from != eqT, due, !(from is Toggle));
        }

        void Push()
        {
            if (dev == null) return;
            Record();
            p.Mute = cgraph.Silent; // listening to the bands: what is silent now (not a setting, see EndListen)
            if (Dry) return;
            if (state.Attached) AudioFx.Write(dev.Id, p);
            saver.Stop(); saver.Start();
        }

        void SwitchOn(bool on)
        {
            if (dev == null || busy) { loading = true; onT.Set(!on); loading = false; return; }
            if (!on || state.Attached) { p.On = on; Push(); Status(AudioFx.Meters(dev.Id)); return; }

            // the first time for this device: the effect goes into Windows' chain, and Windows has to start its sound anew
            bool nahimic = state.Nahimic;
            var text = "Red Tune добавит свой обработчик звука в цепочку эффектов Windows для устройства «" + dev.Full + "» и перезапустит службу звука Windows.\n\n"
                + "Звук прервётся на пару секунд; программе, которая сейчас играет, может понадобиться пауза и снова пуск.\n\n"
                + "Чужие эффекты (" + (nahimic ? "программа производителя, " : "") + "драйвер) остаются на месте. Убрать обработчик можно здесь же, внизу страницы.";
            if (!ConfirmDialog.Ask(FindForm(), "Обработка звука", text, "Подключить", "Отмена")) { loading = true; onT.Set(false); loading = false; return; }
            var id = dev.Id; var sp = p;
            busy = true; onT.Enabled = false; onRow.SetSub("Подключаю обработчик и перезапускаю звук Windows…");
            Task.Run(() =>
            {
                string err = AudioFx.Attach(id);
                if (err == null) { sp.On = true; if (!AudioFx.Write(id, sp, true)) err = "не создался общий файл настроек"; }
                if (err == null) err = AudioFx.RestartAudio();
                if (err != null) Store.Log("звук: подключение: " + err);
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        busy = false; onT.Enabled = true;
                        AudioFx.Save();
                        if (dev != null && dev.Id == id) { LoadDevice(); if (err != null) onRow.SetSub("Не удалось: " + err); }
                    }));
                }
                catch { }
            });
        }

        void RemoveFromDevice()
        {
            if (dev == null || busy || !state.Attached) return;
            if (!ConfirmDialog.Ask(FindForm(), "Убрать обработку", "Red Tune уберёт свой обработчик из цепочки эффектов устройства «" + dev.Full + "» и перезапустит службу звука Windows. Звук прервётся на пару секунд.", "Убрать", "Отмена")) return;
            var id = dev.Id; var sp = p;
            busy = true; onT.Enabled = false; onRow.SetSub("Убираю обработчик и перезапускаю звук Windows…");
            Task.Run(() =>
            {
                string err = AudioFx.Detach(id);
                sp.On = false;
                if (err == null) err = AudioFx.RestartAudio();
                if (err != null) Store.Log("звук: отключение: " + err);
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        busy = false; onT.Enabled = true;
                        AudioFx.Save();
                        if (dev != null && dev.Id == id) { LoadDevice(); if (err != null) onRow.SetSub("Не удалось: " + err); }
                    }));
                }
                catch { }
            });
        }

        // the line under «Обработка звука» and the rows of «Система»
        void Status(SoundMeters m)
        {
            if (busy) return;
            bool alive = m != null && quiet < 15;
            string sub;
            if (dev == null) sub = "Нет устройства";
            else if (!AudioFx.Available) sub = "В этой сборке нет файла обработчика (audio\\RedTuneApo.dll)";
            else if (!state.HasKey) sub = "У этого устройства нет раздела эффектов Windows: обработку к нему пока не подключить";
            else if (state.OtherEndEffect) sub = "Конечный эффект устройства занят другим обработчиком: обработку к нему пока не подключить";
            else if (!state.Attached) sub = "Свой эквалайзер, компрессор и лимитер для этого устройства. Сейчас не подключена";
            else if (state.EnhancementsOff) sub = "Подключена, но в Windows для устройства выключены улучшения звука: эффекты не работают";
            else if (alive) sub = (p.On ? "Работает" : "Подключена, выключена: звук идёт без изменений") + " · " + (m.Rate / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + " кГц · " + "каналов: " + m.Channels + (m.Faults > 0 ? " · сбоев: " + m.Faults : "");
            else sub = p.On ? "Включена · звук сейчас не идёт" : "Подключена, выключена";
            if (onRow.Sub != sub) onRow.SetSub(sub);
            bool can = dev != null && AudioFx.Available && state.HasKey && !state.OtherEndEffect;
            if (onT.Enabled != can) onT.Enabled = can;

            // 19.11: Nahimic's effects on this device, on (as installed) or taken out of its chain by the switch (AudioFx.SetNahimic)
            string nh = state.Nahimic ? "Идут до Red Tune и складываются с ним. Выключите, чтобы слышать только Red Tune: вернуть можно здесь же"
                : state.NahimicOff ? "Убраны из цепочки этого устройства. Программа производителя установлена, как была; выключатель вернёт эффекты на место" : null;
            bool showNh = nh != null, showRemove = state.Attached;
            if (nahimicRow.Visible != showNh || removeRow.Visible != showRemove) { nahimicRow.Visible = showNh; removeRow.Visible = showRemove; Relayout(); }
            if (showNh && nahimicRow.Sub != nh) nahimicRow.SetSub(nh);
            if (showNh && nahimicT.Checked != state.Nahimic) { loading = true; nahimicT.Set(state.Nahimic); loading = false; }
        }

        void SwitchNahimic(bool on)
        {
            Action back = () => { loading = true; nahimicT.Set(!on); loading = false; };
            if (dev == null || busy) { back(); return; }
            var text = on
                ? "Red Tune вернёт эффекты производителя в цепочку устройства «" + dev.Full + "» и перезапустит службу звука Windows. Звук прервётся на пару секунд."
                : "Red Tune уберёт эффекты производителя из цепочки устройства «" + dev.Full + "» и перезапустит службу звука Windows. Звук прервётся на пару секунд.\n\n"
                    // 19.11, measured on the R7000P: a tone played at -30 dB reaches Red Tune at -39 dB with Nahimic in the chain and at -30 dB without it
                    + "Звук станет другим и, скорее всего, заметно громче: эффекты производителя убавляют уровень и делают часть настройки динамиков. Убавьте громкость заранее. Списки эффектов сохраняются как были, выключатель вернёт их на место.";
            if (!ConfirmDialog.Ask(FindForm(), "Эффекты производителя", text, on ? "Вернуть" : "Выключить", "Отмена")) { back(); return; }
            var id = dev.Id;
            busy = true; nahimicT.Enabled = false; nahimicRow.SetSub("Перезапускаю звук Windows…");
            Task.Run(() =>
            {
                string err = AudioFx.SetNahimic(id, on);
                if (err == null) err = AudioFx.RestartAudio();
                if (err != null) Store.Log("звук: эффекты производителя: " + err);
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        busy = false; nahimicT.Enabled = true;
                        if (dev != null && dev.Id == id) { LoadDevice(); if (err != null) nahimicRow.SetSub("Не удалось: " + err); }
                    }));
                }
                catch { }
            });
        }

        // The moments of the sound come from a timer, and Windows hands a timer its turn only when no other message waits —
        // like a paint. While a point is dragged or a dial turned the mouse never leaves the queue empty, so the spectrum
        // and the monitor stood still (20.11, the user: the curves move, the picture lags). Every change by the mouse also
        // takes the moment of the sound that is due.
        readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        long lastTick = -1000;
        bool TickIfDue() { if (clock.ElapsedMilliseconds - lastTick < tick.Interval - 3) return false; Tick(); return true; }
        void Changed() { Changed(null); }

        void Tick()
        {
            if (dev == null || !Visible) return;
            lastTick = clock.ElapsedMilliseconds;
            ticks++;
            var m = state.Attached ? AudioFx.Meters(dev.Id) : null;
            if (m != null && m.Beat != lastBeat) { lastBeat = m.Beat; quiet = 0; } else if (quiet < 1000) quiet++;
            bool alive = m != null && quiet < 15;
            meter.Set(alive, alive ? m.InPeak : 0, alive ? m.OutPeak : 0, alive && p.On && p.Comp ? m.GrDb : 0, alive && p.On && p.Limit ? m.LimDb : 0);
            if (alive && p.On) { graph.Rate = m.Rate > 8000 ? m.Rate : 48000; graph.Feed(AudioFx.Spectrum(dev.Id)); } else graph.Feed(null);
            cgraph.FeedBands(alive && p.On && p.Comp ? m.PartGr : null, alive && p.On ? m.PartLv : null);
            cgraph.Feed(alive && p.On, alive ? m.InPeak : 0, alive ? m.OutPeak : 0, alive && p.Comp ? m.PartGr[band] : 0, alive && p.Limit ? m.LimDb : 0, alive ? m.PartLv[band] : -140);
            if (ticks % 8 == 0) Status(m);
            if (ticks % 30 == 0 && !vol.Capture)
            {
                // the volume keys and other programs move it too
                int v = AudioFx.GetVolume(dev.Id); var mute = AudioFx.GetMute(dev.Id);
                // 19.11: the slider is locked while the device does not answer, and unlocked again when it does. (It
                // was locked only when the device was picked: one that did not answer at that very moment — Windows was
                // starting its sound anew, the headphones were just going away — left the volume dead until the page
                // was opened again.)
                loading = true; if (vol.Enabled != (v >= 0)) vol.Enabled = v >= 0;
                if (v >= 0 && v != vol.Value) vol.Value = v; if (mute != null && muteT.Checked == mute.Value) muteT.Set(!mute.Value); loading = false;
            }
            // 19.11: the devices change under the page (headphones come and go, the mini panel or Windows picks another
            // default): the list, «Основное / Не основное» and the button follow. Not while something is held by the mouse.
            if (ticks % 60 == 0 && !busy && Control.MouseButtons == MouseButtons.None)
            {
                var now = AudioFx.Devices();
                bool moved = now.Count != devices.Count || now.Any(d => { var old = devices.FirstOrDefault(x => x.Id == d.Id); return old == null || old.Default != d.Default; });
                if (moved) Reload();
            }
        }
    }
}
