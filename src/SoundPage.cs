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

    // In and out levels and how much the compressor and the limiter take off, live
    class LevelMeter : Painted
    {
        bool alive; float inDb = -90, outDb = -90, gr, lim;

        public LevelMeter() { BackColor = Theme.Window; Height = Theme.S(84); }

        static float Db(float lin) { return lin <= 0.00001f ? -100f : 20f * (float)Math.Log10(lin); }

        public void Set(bool on, float inPeak, float outPeak, float grDb, float limDb)
        {
            float i = Db(inPeak), o = Db(outPeak);
            if (on == alive && Math.Abs(i - inDb) < 0.2f && Math.Abs(o - outDb) < 0.2f && Math.Abs(grDb - gr) < 0.1f && Math.Abs(limDb - lim) < 0.1f) return;
            alive = on; inDb = i; outDb = o; gr = grDb; lim = limDb;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            Theme.Fill(g, r, Theme.S(8), Theme.Card);
            Theme.Stroke(g, r, Theme.S(8), Theme.Border);
            int x = Theme.S(16), label = Theme.S(96), val = Theme.S(84), rowH = Theme.S(22), y = Theme.S(9);
            int bx = x + label, bw = Width - bx - val - Theme.S(16);
            Bar(g, "На входе", y, bx, bw, rowH, (inDb + 60) / 60f, alive ? Num(inDb) : "—", false);
            Bar(g, "На выходе", y + rowH, bx, bw, rowH, (outDb + 60) / 60f, alive ? Num(outDb) : "—", false);
            float take = -(gr + lim);
            Bar(g, "Сжатие", y + 2 * rowH, bx, bw, rowH, take / 24f, alive && take > 0.05f ? "−" + take.ToString("0.0", CultureInfo.InvariantCulture) + " дБ" : alive ? "0 дБ" : "—", true);
        }

        static string Num(float db) { return db < -90 ? "тишина" : (db < 0 ? "−" : "") + Math.Abs(db).ToString("0.0", CultureInfo.InvariantCulture) + " дБ"; }

        void Bar(Graphics g, string name, int y, int bx, int bw, int h, float f, string value, bool warn)
        {
            f = Math.Max(0, Math.Min(1, f));
            Theme.Text1(g, name, Theme.Small, new Rectangle(Theme.S(16), y, bx - Theme.S(16), h), Theme.Text2);
            float th = Theme.S(5), cy = y + h / 2f;
            Theme.Tube(g, new RectangleF(bx, cy - th / 2f, bw, th));
            if (f > 0.002f) Theme.FillGradient(g, new RectangleF(bx, cy - th / 2f, bw * f, th), th / 2f, warn, bw);
            Theme.Text1(g, value, Theme.Small, new Rectangle(bx + bw + Theme.S(8), y, Width - bx - bw - Theme.S(24), h), alive ? Theme.Text : Theme.Muted, TextFormatFlags.Right);
        }
    }

    // The compressor drawn (19.11, the user's wish: a picture with the settings in it, as the equalizer has).
    // Left: what comes out for what goes in, both in dB — the curve the threshold, the ratio, the knee and the gain after
    // make, with three points on it: the threshold (dragged left and right), the ratio (the right end, up and down) and
    // the gain after (the left part, up and down). The dot is the sound right now.
    // Beside it: dials for the attack, the release and the knee, one under another (they have no rows on the page).
    // Then, to the right edge: the last ten seconds on the same dB scale — the level coming in, the level going out, and
    // from the top down how much the compressor takes off; the dashed line is the threshold and can be dragged too.
    // The points write into the same settings as the sliders under the picture; the page keeps the two in step.
    // The limiter, when it is on, is in the same picture (19.11, the user's wish): its ceiling is a line across the curve
    // (the curve goes flat under it) and across the last seconds, where what the limiter takes off hangs under what the
    // compressor takes off, in its own colour. The line can be dragged, also while the compressor is off.
    class CompGraph : Painted
    {
        public SoundParams P = SoundParams.Start();
        public bool Dim;
        // 19.11: the compressor in bands. Band: the one of SoundParams.Parts the points, the dials and the last seconds
        // show (the page's choice); the curves of the other bands at work are drawn thin behind it, each in its colour.
        public int Band;
        CompPart Q { get { return P.Parts[Math.Max(0, Math.Min(2, Band))]; } }
        internal static readonly Color[] PartColor = { Theme.Accent, Color.FromArgb(0x6f, 0xc2, 0x95), Color.FromArgb(0x6a, 0xa9, 0xf2) }; // the bass, the middle, the top
        static Color Lighter(Color c) { return Color.FromArgb(c.R + (255 - c.R) / 4, c.G + (255 - c.G) / 4, c.B + (255 - c.B) / 4); }
        public event EventHandler Changed;
        const double Lo = -60, Hi = 0;
        // the dials' column: as wide as the longest name («Восстановление»), with the same gap to the curve on its left and to
        // the last seconds on its right; 300 samples = ten seconds at the page's 30 per second
        const int PanelUnits = 124, BandUnits = 120, Gap = 26, Hist = 300;
        // 19.11: a text in the middle of a small button or over a dial is drawn with no side room of Windows' own
        // (Mid): that room is wider on the right, and the text stood about two pixels left of the middle
        const TextFormatFlags Mid = TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding;
        // A single digit or letter in a small button (1 2 3, S, M) stands in the middle by its ink, not by the box the
        // font keeps for it: the box has uneven room at the sides and more room above a digit than under it, so at
        // 150 % the digits sat a pixel and a half low (measured on the rendered picture). The shift from the box to the ink is found once per glyph, font and scale by drawing the
        // glyph the very way it is drawn here into a small picture and looking where its pixels are.
        static readonly Dictionary<string, Point> glyphOff = new Dictionary<string, Point>();
        static void Glyph(Graphics g, string text, Font f, Rectangle r, Color c)
        {
            string key = text + "|" + f.Name + "|" + f.SizeInPoints + "|" + (int)f.Style + "|" + g.DpiY;
            Point off;
            if (!glyphOff.TryGetValue(key, out off))
            {
                off = Point.Empty;
                try
                {
                    var box = TextRenderer.MeasureText(g, text, f, new Size(1000, 100), TextFormatFlags.NoPadding);
                    int pad = 6, bw = box.Width + 2 * pad, bh = box.Height + 2 * pad;
                    using (var bmp = new Bitmap(bw, bh, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                    {
                        bmp.SetResolution(g.DpiX, g.DpiY);
                        using (var bg = Graphics.FromImage(bmp))
                        {
                            bg.Clear(Color.Black);
                            TextRenderer.DrawText(bg, text, f, new Rectangle(pad, pad, box.Width, box.Height), Color.White, Color.Black, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
                        }
                        int x0 = bw, x1 = -1, y0 = bh, y1 = -1;
                        for (int y = 0; y < bh; y++)
                            for (int x = 0; x < bw; x++)
                            {
                                var px = bmp.GetPixel(x, y);
                                if (px.R + px.G + px.B > 300) { if (x < x0) x0 = x; if (x > x1) x1 = x; if (y < y0) y0 = y; if (y > y1) y1 = y; }
                            }
                        if (x1 >= 0) off = new Point((int)Math.Round(pad + box.Width / 2f - (x0 + x1 + 1) / 2f), (int)Math.Round(pad + box.Height / 2f - (y0 + y1 + 1) / 2f));
                    }
                }
                catch { }
                glyphOff[key] = off;
            }
            r.Offset(off);
            TextRenderer.DrawText(g, text, f, r, c, Mid | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        }
        internal enum Part { None, Thr, Ratio, Makeup, Line, Attack, Release, Knee, Ceil, Split1, Split2, Zone, ViewTime, ViewFreq, Count, Pill, DialThr, DialRatio, DialMakeup, Solo, Mute, DialSplit1, DialSplit2, CeilFader }
        Part hot, drag; bool armed, demo; int dragY; double dragFrom; float grabDx, grabDy;

        // 19.11 (the user's wish: the bands seen, not only named). The place of the last seconds holds one of two monitors,
        // switched by the two words under it at the right. ByFreq: the bands across the frequencies, as the equalizer has
        // its axis — each band at work a column in its colour with its name on top, in it the level the band reads now
        // (from the bottom), what is taken off it (from the top) and its threshold (the dashed line, dragged up and down);
        // between the columns the splits (dragged left and right). A click on a column makes that band the one the points,
        // the dials and the rows show. A split that is not at work stays as a faint dashed line where the page keeps it,
        // so the picture always shows where the bands would part.
        public bool ByFreq;
        public float Ghost1 = 250, Ghost2 = 4000;
        public event EventHandler BandPicked, ViewPicked;
        const double MinF = 20, MaxF = 20000;
        int zoneHot = -1; // the part of the settings whose column is under the mouse
        readonly float[] bLv = { -140, -140, -140 }, bGr = new float[3]; // per band, live: the level it reads and what is taken off it

        // 19.11 (the user's wish): listening to the bands. Under the name of each column of the monitor by frequencies two
        // small buttons: S (solo: only the bands with S are heard) and M (mute: this band is not heard). SoloMask and
        // MuteMask hold a bit per part of the settings; Silent is what the effect gets — the parts that do not go out.
        // It is for listening while setting up, not a setting: the page takes it off when it is left.
        public int SoloMask, MuteMask;
        public event EventHandler ListenChanged;
        internal static readonly Color SoloC = Color.FromArgb(0xf2, 0xc2, 0x4e), MuteC = Color.FromArgb(0xe8, 0x6a, 0x3a);
        public int Silent
        {
            get
            {
                if (!P.Comp || P.PartCount < 2) return 0;
                int act = 0; foreach (int i in P.ActiveParts) act |= 1 << i;
                int solo = SoloMask & act;
                return (MuteMask | (solo != 0 ? ~solo : 0)) & act;
            }
        }
        public void ClearListen() { if (SoloMask == 0 && MuteMask == 0) return; SoloMask = MuteMask = 0; Invalidate(); }
        void Listen(Part part, int band)
        {
            if (band < 0 || band > 2) return;
            if (part == Part.Solo) SoloMask ^= 1 << band; else MuteMask ^= 1 << band;
            hot = part; Invalidate();
            var h = ListenChanged; if (h != null) h(this, EventArgs.Empty);
        }
        // the S (mute = false) or the M button of a band's column; empty where there is none (one band, another monitor,
        // a column too narrow for the two)
        internal Rectangle ListenRect(int part, bool mute)
        {
            var hh = History; if (!ByFreq || hh.Width <= 0 || P.PartCount < 2) return Rectangle.Empty;
            foreach (var z in Zones())
            {
                if (z.Part != part) continue;
                float x0 = FX(z.From), x1 = FX(z.To); int w = Theme.S(24), h = Theme.S(20), gap = Theme.S(4);
                if (x1 - x0 < 2 * w + gap + Theme.S(8)) return Rectangle.Empty;
                int cx = (int)((x0 + x1) / 2), y = hh.Y + Theme.S(31);
                return mute ? new Rectangle(cx + gap / 2, y, w, h) : new Rectangle(cx - gap / 2 - w, y, w, h);
            }
            return Rectangle.Empty;
        }

        internal struct Zone { public int Part; public double From, To; }
        internal Zone[] Zones()
        {
            int n = P.PartCount;
            if (n == 1) return new[] { new Zone { Part = 0, From = MinF, To = MaxF } };
            if (n == 2) return new[] { new Zone { Part = 0, From = MinF, To = P.Split }, new Zone { Part = 2, From = P.Split, To = MaxF } };
            return new[] { new Zone { Part = 0, From = MinF, To = P.Split }, new Zone { Part = 1, From = P.Split, To = P.Split2 }, new Zone { Part = 2, From = P.Split2, To = MaxF } };
        }
        internal static readonly string[] ZoneNames = { "Низ", "Середина", "Верх" };
        internal float FX(double hz) { var h = History; return (float)(h.X + h.Width * Math.Log(Clamp(hz, MinF, MaxF) / MinF) / Math.Log(MaxF / MinF)); }
        double HzAtX(float x) { var h = History; return MinF * Math.Pow(MaxF / MinF, Clamp((x - h.X) / (double)Math.Max(1, h.Width), 0, 1)); }
        // the grip of a split (0 the lower, 1 the upper): on its line, in the lower part of the plot, clear of the names on top
        internal PointF SplitAt(int k) { var h = History; return new PointF(FX(k == 0 ? P.Split : P.Split2), h.Y + h.Height * 0.76f); }
        int ZoneAt(float x) { double f = HzAtX(x); foreach (var z in Zones()) if (f < z.To) return z.Part; return 2; }

        // the two words that switch the monitor, at the right end of the line under it
        string viewKey; int viewW0, viewW1;
        internal Rectangle ViewRect(bool freq)
        {
            var h = History; if (h.Width <= 0) return Rectangle.Empty;
            string a = L.T("Во времени"), b = L.T("По частотам");
            if (viewKey != a + "|" + b) { viewKey = a + "|" + b; viewW0 = TextRenderer.MeasureText(a, Theme.Small).Width + Theme.S(12); viewW1 = TextRenderer.MeasureText(b, Theme.Small).Width + Theme.S(12); }
            int y = Curve.Bottom + Theme.S(3), hh = Theme.S(20);
            return freq ? new Rectangle(h.Right - viewW1, y, viewW1, hh) : new Rectangle(h.Right - viewW1 - Theme.S(4) - viewW0, y, viewW0, hh);
        }
        public void SetView(bool freq) { if (ByFreq == freq) return; ByFreq = freq; hot = Part.None; Invalidate(); }
        readonly float[] hIn = new float[Hist], hOut = new float[Hist], hLim = new float[Hist];
        // 20.11 (the user): what the compressor took off, kept per part of the settings (the bass or the whole sound, the
        // middle, the top), so the band picked shows its own last ten seconds at once; there was one line for whichever
        // band was shown, and a band picked only took over its colour
        readonly float[][] hGrB = { new float[Hist], new float[Hist], new float[Hist] };
        int hCount; bool live; float nowIn = -100, nowGr, nowLim, nowLv = -140; // nowLv: the level the shown band reads, for the dot on the curve

        public CompGraph() { BackColor = Theme.Window; Height = Theme.S(400); anim.Tick += (o, e) => AnimTick(); Disposed += (o, e) => anim.Dispose(); } // 300 -> 350: three dials with a name above and a value under each need the height

        // 19.11 (the user's order: first how many bands and which one, then its settings): the bands' column stands first,
        // the curve after it, with the room for the numbers of its scale between them
        int Side { get { return Math.Max(Theme.S(60), Height - Theme.S(72)); } }
        const int BandsLeft = 24, CurveAfterBands = 54; // as far from the card's edge as from the numbers of the curve's scale
        internal Rectangle Curve { get { int side = Side; return new Rectangle(HasBands ? Theme.S(BandsLeft + BandUnits + CurveAfterBands) : Theme.S(46), Theme.S(14), side, side); } }
        // 19.11 (the user's order): the curve, then the dials one under another, then the last seconds to the right edge.
        // In a narrow window the last seconds go first, then the dials.
        // 19.11 (the user's wish): six dials — the threshold, the ratio and the gain after came from their rows under the
        // picture and stand in a column of their own, left of the attack, the release and the knee. Where the two columns
        // do not fit there is the one of before (and the page shows the three rows again); narrower still, none.
        const int DialUnits = 104, DialGap = 8;
        int DialCols
        {
            get
            {
                int free = Width - Curve.Right - Theme.S(Gap) - Theme.S(12);
                return free >= Theme.S(2 * DialUnits + DialGap) ? 2 : free >= Theme.S(PanelUnits) ? 1 : 0;
            }
        }
        bool HasPanel { get { return DialCols > 0; } }
        public bool HasSixDials { get { return DialCols == 2; } }
        internal Rectangle Panel { get { var c = Curve; int n = DialCols; return n > 0 ? new Rectangle(c.Right + Theme.S(Gap), c.Y, n == 2 ? Theme.S(2 * DialUnits + DialGap) : Theme.S(PanelUnits), c.Height) : Rectangle.Empty; } }
        // the cell of a dial: 0 the attack, 1 the release, 2 the knee (the right column, or the only one), 3 the threshold,
        // 4 the ratio, 5 the gain after (the left column, only with two)
        // 19.11, later (the user's wish): under the six, two more — the splits between the bands, 6 the lower (the left
        // column) and 7 the upper (the right one). With two columns the dials stand in four rows (the picture is 400
        // tall for that, it was 350). A split that is not at work has its dial grey and still: with one band both,
        // with two the upper one.
        internal Rectangle DialCell(int k)
        {
            var pn = Panel;
            if (DialCols < 2) { int h3 = pn.Height / 3; return k < 3 ? new Rectangle(pn.X, pn.Y + h3 * k, pn.Width, h3) : Rectangle.Empty; }
            int rowH = pn.Height / 4, row = k >= 6 ? 3 : k % 3, w = Theme.S(DialUnits);
            bool rightCol = k < 3 || k == 7;
            return new Rectangle(rightCol ? pn.Right - w : pn.X, pn.Y + rowH * row, w, rowH);
        }
        int DialCount { get { return DialCols == 2 ? 8 : 3; } }
        bool DialOn(int k) { return k == 6 ? P.PartCount >= 2 : k == 7 ? P.PartCount >= 3 : true; }
        internal Rectangle History
        {
            get
            {
                var c = Curve; if (!HasPanel) return Rectangle.Empty;
                int x = Panel.Right + Theme.S(Gap), w = Width - Theme.S(18) - Theme.S(CeilUnits) - x; // the ceiling's fader at the right end
                return w >= Theme.S(150) ? new Rectangle(x, c.Y, w, c.Height) : Rectangle.Empty;
            }
        }
        // 19.11 (the user's order: the bands, the curve, the dials, the monitor). The bands: how many (1, 2, 3) and under
        // that the one being set — a plate per band with its colour; a band that is not at work keeps its place, grey,
        // and lights up when the number takes it in. They were two rows under the picture; in a window too narrow for
        // the column beside the curve and the dials the page shows those rows instead (HasBands).
        // 20.11 (the user's wish): the limiter's ceiling as a fader right of the monitor, 0 dB on top, −12 at the bottom, by
        // half a dB; a flat line with an arrow up to it on top, the value under it. Only where the monitor has its room.
        const int CeilUnits = 44, CeilStripW = 30;
        // with no room for the monitor it still stands at the right end when it fits there itself (the line over the curve
        // is then the other way to the ceiling)
        internal Rectangle CeilStrip
        {
            get
            {
                var c = Curve; if (!HasPanel) return Rectangle.Empty;
                int x = Width - Theme.S(18) - Theme.S(CeilStripW);
                return History.Width > 0 || x - Panel.Right >= Theme.S(CeilUnits - CeilStripW) ? new Rectangle(x, c.Y, Theme.S(CeilStripW), c.Height) : Rectangle.Empty;
            }
        }
        internal int CeilTop { get { return CeilStrip.Y + Theme.S(26); } }
        internal int CeilBottom { get { return CeilStrip.Bottom - Theme.S(6); } }
        internal float CeilY(double db) { return (float)(CeilTop + (CeilBottom - CeilTop) * Clamp(-db / 12.0, 0, 1)); }
        public bool HasMonitor { get { return History.Width > 0; } } // room for the monitor, where the splits are dragged
        public bool HasBands { get { return Width >= Theme.S(BandsLeft + BandUnits + CurveAfterBands) + Side + Theme.S(Gap) + Theme.S(PanelUnits) + Theme.S(12); } }
        internal Rectangle BandsCol { get { return HasBands ? new Rectangle(Theme.S(BandsLeft), Theme.S(14), Theme.S(BandUnits), Side) : Rectangle.Empty; } }
        // 19.11 (the user: it stood high, put it in order). One block in the middle of the plot's height: a title, 6 to its
        // numbers, 22 to the next title, 6 to its plates, the plates 8 apart.
        // 19.11, later (the user: a grey «Середина» reads as something switched off): only the bands at work have a plate,
        // so the block is as tall as their number and stays in the middle. A plate comes and goes with a short movement:
        // plate[i] runs 0..1 (how much of plate i is there — its height and its ink), the plates under it and the whole
        // block move with it. What is under the mouse is always judged by where things will stand (live = false).
        const int BandsTitle2 = 78;
        readonly float[] plate = { 1, 0, 0 }; SoundParams plateP;
        readonly Timer anim = new Timer { Interval = 15 };
        float PlateOf(int part, bool live) { return live ? plate[part] : P.ActiveParts.Contains(part) ? 1 : 0; }
        // Where the block stands. With another number of plates its middle place is another, and the numbers would move
        // from under the mouse that has just clicked one (20 units a plate: the next click would miss). So the block
        // stays where it is while the mouse is over the column and glides to the middle when the mouse has left.
        // What is drawn and what is clicked use the same place.
        float topNow = float.NaN; bool inCol;
        float TopWanted { get { var b = BandsCol; return b.Y + (b.Height - Theme.S(96) - Theme.S(40) * P.PartCount) / 2f; } }
        float BandsTopF { get { return float.IsNaN(topNow) ? TopWanted : topNow; } }
        void SetInCol(bool v) { if (inCol == v) return; inCol = v; Invalidate(); }
        internal void Settle() { plateP = null; inCol = false; SyncPlates(); Invalidate(); } // everything in its place at once (the check)
        internal Rectangle CountRect(int i) { var b = BandsCol; int gap = Theme.S(4), w = (b.Width - 2 * gap) / 3; return new Rectangle(b.X + i * (w + gap), (int)Math.Round(BandsTopF) + Theme.S(26), w, Theme.S(30)); }
        RectangleF PlateRect(int part, bool live)
        {
            var b = BandsCol; float y = BandsTopF + Theme.S(BandsTitle2 + 26);
            for (int j = 0; j < part; j++) y += Theme.S(40) * PlateOf(j, live);
            return new RectangleF(b.X, y, b.Width, Theme.S(32) * PlateOf(part, live));
        }
        internal Rectangle BandRect(int part) { return Rectangle.Round(PlateRect(part, false)); }
        internal float PlateShown(int part) { return plate[part]; } // for the check: how much of a plate is drawn now

        // the plates follow the bands at work: at once for other settings (a device, a preset) and in the off-screen
        // render, with the movement when the number of bands changes in the settings shown
        void SyncPlates()
        {
            var act = P.ActiveParts; bool snap = demo || !ReferenceEquals(P, plateP) || !IsHandleCreated, moving = false; plateP = P;
            for (int i = 0; i < 3; i++) { float t = act.Contains(i) ? 1 : 0; if (snap) plate[i] = t; else if (plate[i] != t) moving = true; }
            if (snap || float.IsNaN(topNow)) topNow = TopWanted; else if (!inCol && Math.Abs(topNow - TopWanted) > 0.01f) moving = true;
            if (moving && !anim.Enabled) anim.Start();
        }
        void AnimTick()
        {
            var act = P.ActiveParts; bool moving = false;
            for (int i = 0; i < 3; i++)
            {
                float t = act.Contains(i) ? 1 : 0;
                plate[i] += (t - plate[i]) * 0.22f; // quick at first, slowing at the end; about a quarter of a second
                if (Math.Abs(t - plate[i]) < 0.012f) plate[i] = t; else moving = true;
            }
            float want = TopWanted; if (float.IsNaN(topNow)) topNow = want;
            if (!inCol && topNow != want) { topNow += (want - topNow) * 0.22f; if (Math.Abs(want - topNow) < 0.4f) topNow = want; else moving = true; }
            if (!moving) anim.Stop();
            Invalidate();
        }
        int idxHot = -1; // which of the three numbers or plates is under the mouse

        // the number of bands from the picture: the splits go on where the page keeps them
        void SetCount(int n)
        {
            if (n == P.PartCount) return;
            P.Split = n >= 2 ? Ghost1 : 0; P.Split2 = n >= 3 ? Ghost2 : 0;
            SoloMask = MuteMask = 0; // other bands: nothing of them is listened to apart yet
            var act = P.ActiveParts; if (!act.Contains(Band)) Band = act[0];
            Fire();
        }
        // the middle of a dial: 0 the attack, 1 the release, 2 the knee
        internal Point DialAt(int k) { var r = DialCell(k); return new Point(r.X + r.Width / 2, r.Y + r.Height / 2); }
        static readonly Part[] Dials = { Part.Attack, Part.Release, Part.Knee, Part.DialThr, Part.DialRatio, Part.DialMakeup, Part.DialSplit1, Part.DialSplit2 };

        static double Clamp(double v, double a, double b) { return v < a ? a : v > b ? b : v; }
        internal float X(double db) { var c = Curve; return (float)(c.X + c.Width * (Clamp(db, Lo, Hi) - Lo) / (Hi - Lo)); }
        internal float Y(double db) { var c = Curve; return (float)(c.Bottom - c.Height * (Clamp(db, Lo, Hi) - Lo) / (Hi - Lo)); }
        double DbAtX(float x) { var c = Curve; return Lo + (x - c.X) / (double)c.Width * (Hi - Lo); }
        double DbAtY(float y) { var c = Curve; return Lo + (c.Bottom - y) / (double)c.Height * (Hi - Lo); }

        // what the compressor takes off at a level, dB (<= 0): the same formula as the effect's (Want in rtdsp.h)
        internal double Gr(double x) { return GrOf(Q, x); }
        internal float GrAgo(int part, int k) { return hGrB[part][Hist - 1 - k]; } // the check: what part took off k moments ago
        static double GrOf(CompPart q, double x)
        {
            double over = x - q.Thr, slope = 1.0 / Math.Max(1, q.Ratio) - 1, knee = q.Knee;
            if (2 * over < -knee) return 0;
            if (knee > 0 && 2 * over <= knee) { double t = over + knee * 0.5; return slope * t * t / (2 * knee); }
            return slope * over;
        }
        double Out(double x) { return x + Gr(x) + Q.Makeup; }
        // where the point of the gain after stands: on the part the compressor does not touch, halfway to the threshold
        double MakeupX { get { return Math.Max(Lo + 6, (Lo + Q.Thr) / 2); } }

        internal PointF KnobAt(Part p)
        {
            double x = p == Part.Thr ? Q.Thr : p == Part.Ratio ? Hi : MakeupX;
            return new PointF(X(x), Y(Out(x)));
        }

        Part Hit(Point m)
        {
            Part best = Part.None; float bd = Theme.S(15) * Theme.S(15); zoneHot = -1; idxHot = -1;
            if (!Dim) foreach (var p in new[] { Part.Thr, Part.Ratio, Part.Makeup }) { var k = KnobAt(p); float d = (k.X - m.X) * (k.X - m.X) + (k.Y - m.Y) * (k.Y - m.Y); if (d < bd) { bd = d; best = p; } }
            if (best != Part.None) return best;
            // the two words under the monitor
            if (ViewRect(false).Contains(m)) return Part.ViewTime;
            if (ViewRect(true).Contains(m)) return Part.ViewFreq;
            { var cs = CeilStrip; if (cs.Width > 0 && Rectangle.Inflate(cs, 0, Theme.S(4)).Contains(m)) return P.Limit ? Part.CeilFader : Part.None; }
            // the bands' column: a number, or the plate of a band at work
            if (!Dim && BandsCol.Contains(m))
            {
                for (int i = 0; i < 3; i++) if (CountRect(i).Contains(m)) { idxHot = i; return Part.Count; }
                for (int i = 0; i < 3; i++) if (BandRect(i).Contains(m) && P.ActiveParts.Contains(i)) { idxHot = i; return Part.Pill; }
                return Part.None;
            }
            // the two lines: the limiter's ceiling (over the curve and over the last seconds; with the compressor off too)
            // and the threshold (over the last seconds); where they lie close, the nearer one
            int near = Theme.S(6);
            var h = Rectangle.Inflate(History, 0, near); var c = Rectangle.Inflate(Curve, 0, near);
            bool inH = h.Width > 0 && h.Contains(m), bands = ByFreq && inH && !Dim;
            // the bands: a split first (its grip, or its line), then the lines, then the column itself
            if (bands)
            {
                int n = P.PartCount;
                for (int k = 0; k < n - 1; k++)
                {
                    var s = SplitAt(k); float dx = m.X - s.X, dy = m.Y - s.Y;
                    if (Math.Abs(dx) <= Theme.S(5) || dx * dx + dy * dy < Theme.S(13) * Theme.S(13)) return k == 0 ? Part.Split1 : Part.Split2;
                }
                zoneHot = ZoneAt(m.X);
                if (ListenRect(zoneHot, false).Contains(m)) return Part.Solo;
                if (ListenRect(zoneHot, true).Contains(m)) return Part.Mute;
            }
            // by frequencies each column has its own threshold line: the one of the column under the mouse
            float dc = Math.Abs(m.Y - Y(P.Ceil)), dt = Math.Abs(m.Y - Y(bands ? P.Parts[zoneHot].Thr : Q.Thr));
            bool onCeil = P.Limit && dc <= near && (inH || c.Contains(m));
            bool onLine = !Dim && dt <= near && inH;
            if (onCeil && (!onLine || dc <= dt)) return Part.Ceil;
            if (onLine) return Part.Line;
            if (bands) return Part.Zone;
            if (Dim) return Part.None;
            var pn = Panel;
            if (pn.Width > 0 && pn.Contains(m)) { for (int k = 0; k < DialCount; k++) if (DialCell(k).Contains(m)) return DialOn(k) ? Dials[k] : Part.None; }
            return Part.None;
        }

        // a change by the mouse (a drag, a dial, the wheel) is painted at once: Windows paints a window only when no other
        // message waits, and a fast mouse sends its moves without a pause — the picture stood still while it was dragged
        // and moved again only when the mouse stopped (20.11, the user: «двигаешь и даже не видишь, куда двигаешь»)
        void Fire() { Invalidate(); var h = Changed; if (h != null) h(this, EventArgs.Empty); Update(); }

        static float Tenth(double v) { return (float)(Math.Round(v * 10) / 10); }

        void DragTo(Point m)
        {
            float mx = m.X + grabDx, my = m.Y + grabDy;
            double turn = (dragY - m.Y) / (double)Theme.S(220); // a dial: its whole range in 220 units of travel
            switch (drag)
            {
                case Part.Thr: Q.Thr = (float)Math.Round(Clamp(DbAtX(mx), -60, 0)); break;
                case Part.Line: Q.Thr = (float)Math.Round(Clamp(DbAtY(my), -60, 0)); break;
                case Part.Makeup: { double x = MakeupX; Q.Makeup = (float)Math.Round(Clamp(DbAtY(my) - x - Gr(x), -12, 24)); break; }
                case Part.Ratio:
                {
                    // the right end of the curve follows the mouse: the ratio that puts the output there. With the
                    // threshold at the very top there is no such stretch, the point then turns as a dial does.
                    double span = Hi - Q.Thr;
                    if (span >= 6) { double o = DbAtY(my) - Q.Makeup; Q.Ratio = Tenth(o >= Hi - 0.01 ? 1 : o <= Q.Thr + span / 20 ? 20 : Clamp(span / (o - Q.Thr), 1, 20)); }
                    else Q.Ratio = Tenth(Clamp(dragFrom * Math.Pow(20, -turn), 1, 20));
                    break;
                }
                case Part.Attack: Q.Attack = (float)Math.Round(Clamp(dragFrom * Math.Pow(200, turn), 1, 200)); break;
                case Part.Release: Q.Release = (float)(Math.Round(Clamp(dragFrom * Math.Pow(200, turn), 10, 2000) / 10) * 10); break;
                case Part.Knee: Q.Knee = (float)Math.Round(Clamp(dragFrom + 24 * turn, 0, 24)); break;
                case Part.DialThr: Q.Thr = (float)Math.Round(Clamp(dragFrom + 60 * turn, -60, 0)); break;
                case Part.DialRatio: Q.Ratio = Tenth(Clamp(dragFrom * Math.Pow(20, turn), 1, 20)); break;
                case Part.DialMakeup: Q.Makeup = (float)Math.Round(Clamp(dragFrom + 36 * turn, -12, 24)); break;
                // the splits as dials: the whole range of each (80..600 Hz, 1.2..12 kHz) in the same travel, evenly by ear
                case Part.DialSplit1: P.Split = (float)(Math.Round(Clamp(dragFrom * Math.Pow(600 / 80.0, turn), 80, 600) / 10) * 10); break;
                case Part.DialSplit2: P.Split2 = (float)(Math.Round(Clamp(dragFrom * Math.Pow(10, turn), 1200, 12000) / 100) * 100); break;
                case Part.Ceil: P.Ceil = (float)(Math.Round(Clamp(DbAtY(my), -12, 0) * 2) / 2); break; // by half a dB
                case Part.CeilFader: P.Ceil = (float)(Math.Round(Clamp(dragFrom + (dragY - m.Y) * 12.0 / Math.Max(1, CeilBottom - CeilTop), -12, 0) * 2) / 2); break; // from where it was taken
                // the splits: within what the page's sliders allow, by their steps
                case Part.Split1: P.Split = (float)(Math.Round(Clamp(HzAtX(mx), 80, 600) / 10) * 10); break;
                case Part.Split2: P.Split2 = (float)(Math.Round(Clamp(HzAtX(mx), 1200, 12000) / 100) * 100); break;
            }
            Fire();
        }

        // a column clicked: that band is the one shown from now on (the page moves its rows after it)
        void Pick(int part)
        {
            if (part < 0 || part == Band || !P.ActiveParts.Contains(part)) return;
            Band = part; Invalidate();
            var h = BandPicked; if (h != null) h(this, EventArgs.Empty);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            armed = true; SetInCol(BandsCol.Contains(e.Location));
            var part = Hit(e.Location);
            if (e.Button == MouseButtons.Left && (part == Part.Solo || part == Part.Mute)) { Listen(part, zoneHot); base.OnMouseDown(e); return; }
            if (e.Button == MouseButtons.Left && (part == Part.Count || part == Part.Pill))
            {
                // 20.11 (the user): the monitor stays as it is — over time or by frequencies — when the number of bands or
                // the band being set is picked (it used to turn to the bands)
                hot = part;
                if (part == Part.Count) SetCount(idxHot + 1); else Pick(idxHot);
                Invalidate(); base.OnMouseDown(e); return;
            }
            if (e.Button == MouseButtons.Left && (part == Part.ViewTime || part == Part.ViewFreq))
            {
                SetView(part == Part.ViewFreq);
                var h = ViewPicked; if (h != null) h(this, EventArgs.Empty);
                base.OnMouseDown(e); return;
            }
            // by frequencies: a press on a column or on its threshold line takes that band first; the line is then dragged at once
            if (e.Button == MouseButtons.Left && ByFreq && (part == Part.Zone || part == Part.Line)) Pick(zoneHot);
            if (e.Button == MouseButtons.Left && part == Part.Zone) { hot = part; Invalidate(); base.OnMouseDown(e); return; }
            if (e.Button == MouseButtons.Left && part != Part.None)
            {
                drag = hot = part; dragY = e.Y; grabDx = grabDy = 0;
                dragFrom = part == Part.Attack ? Q.Attack : part == Part.Release ? Q.Release : part == Part.Knee ? Q.Knee : part == Part.DialThr ? Q.Thr : part == Part.DialMakeup ? Q.Makeup : part == Part.DialSplit1 ? P.Split : part == Part.DialSplit2 ? P.Split2 : Q.Ratio;
                // a split's dial taken: the monitor turns to the bands, where the split is seen moving
                if ((part == Part.DialSplit1 || part == Part.DialSplit2) && !ByFreq) { SetView(true); drag = hot = part; var hv = ViewPicked; if (hv != null) hv(this, EventArgs.Empty); }
                // the point stays under the mouse where it was taken, it does not jump to the pointer
                if (part == Part.Thr || part == Part.Ratio || part == Part.Makeup) { var k = KnobAt(part); grabDx = k.X - e.X; grabDy = k.Y - e.Y; }
                else if (part == Part.Line) grabDy = Y(Q.Thr) - e.Y;
                else if (part == Part.Ceil) grabDy = Y(P.Ceil) - e.Y;
                else if (part == Part.CeilFader)
                {
                    // the pointer onto the knob, the knob stays (20.11, as the equalizer's faders)
                    dragFrom = P.Ceil; var cs = CeilStrip; int ky = (int)Math.Round(CeilY(P.Ceil));
                    if (Theme.Warp(this, cs.X + cs.Width / 2f, ky)) dragY = ky;
                }
                else if (part == Part.Split1 || part == Part.Split2) grabDx = SplitAt(part == Part.Split1 ? 0 : 1).X - e.X;
                Invalidate();
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e) { drag = Part.None; Invalidate(); base.OnMouseUp(e); }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            SetInCol(BandsCol.Contains(e.Location));
            if (drag != Part.None) { DragTo(e.Location); base.OnMouseMove(e); return; }
            int zoneWas = zoneHot, idxWas = idxHot;
            var part = Hit(e.Location);
            bool click = part == Part.ViewTime || part == Part.ViewFreq || part == Part.Solo || part == Part.Mute || (part == Part.Zone && zoneHot != Band) || (part == Part.Count && idxHot != P.PartCount - 1) || (part == Part.Pill && idxHot != Band);
            Cursor = part == Part.Thr || part == Part.Split1 || part == Part.Split2 ? Cursors.SizeWE : click ? Cursors.Hand : part == Part.Zone || part == Part.Count || part == Part.Pill ? Cursors.Default : part != Part.None ? Cursors.SizeNS : Cursors.Default;
            if (part != hot || zoneHot != zoneWas || idxHot != idxWas) { hot = part; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            armed = false; SetInCol(false);
            if (drag == Part.None && (hot != Part.None || zoneHot >= 0)) { hot = Part.None; zoneHot = -1; idxHot = -1; Invalidate(); }
            base.OnMouseLeave(e);
        }

        // the wheel works over a point or a dial and after a click on the picture; otherwise the page scrolls.
        // Over the threshold point it changes the knee, elsewhere the value under the mouse
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (!armed || hot == Part.None || hot == Part.Zone || hot == Part.ViewTime || hot == Part.ViewFreq || hot == Part.Count || hot == Part.Pill || hot == Part.Solo || hot == Part.Mute || (Dim && hot != Part.Ceil && hot != Part.CeilFader)) { base.OnMouseWheel(e); return; }
            if (hot == Part.Line && ByFreq && zoneHot != Band) { base.OnMouseWheel(e); return; } // the line of a band not taken yet
            var he = e as HandledMouseEventArgs; if (he != null) he.Handled = true;
            // 20.11 (the user: a notch moved a dial by three): one notch (120) = one step of the value, whatever the mouse
            // sends — a smooth wheel sends a notch in several small parts, each of them used to be a whole step
            int notches = Theme.WheelNotches(ref wheelAcc, e.Delta);
            for (int i = 0; i < Math.Abs(notches); i++) WheelStep(notches > 0 ? 1 : -1);
            if (notches != 0) Fire();
        }
        int wheelAcc;

        // one step of the value under the mouse (20.11: by one step of its scale; the attack, the release and the ratio
        // went by 15 and 10 %, three milliseconds a notch at 20 ms)
        void WheelStep(int d)
        {
            switch (hot)
            {
                case Part.Split1: case Part.DialSplit1: P.Split = (float)Clamp(Math.Round(P.Split / 10) * 10 + d * 10, 80, 600); break;
                case Part.Split2: case Part.DialSplit2: P.Split2 = (float)Clamp(Math.Round(P.Split2 / 100) * 100 + d * 100, 1200, 12000); break;
                case Part.Ceil: case Part.CeilFader: P.Ceil = Tenth(Clamp(P.Ceil + d * 0.5, -12, 0)); break;
                case Part.Thr: Q.Knee = (float)Clamp(Math.Round(Q.Knee) + d, 0, 24); break;
                case Part.Knee: Q.Knee = (float)Clamp(Math.Round(Q.Knee) + d, 0, 24); break;
                case Part.DialThr: Q.Thr = (float)Clamp(Math.Round(Q.Thr) + d, -60, 0); break;
                case Part.DialMakeup: Q.Makeup = (float)Clamp(Math.Round(Q.Makeup) + d, -12, 24); break;
                case Part.DialRatio: Q.Ratio = Tenth(Clamp(Q.Ratio + d * 0.1, 1, 20)); break; // a dial: up = more
                case Part.Line: Q.Thr = (float)Clamp(Math.Round(Q.Thr) + d, -60, 0); break;
                case Part.Makeup: Q.Makeup = (float)Clamp(Math.Round(Q.Makeup) + d, -12, 24); break;
                case Part.Ratio: Q.Ratio = Tenth(Clamp(Q.Ratio - d * 0.1, 1, 20)); break; // up = less squeezing, as the point goes up
                case Part.Attack: Q.Attack = (float)Clamp(Math.Round(Q.Attack) + d, 1, 200); break;
                case Part.Release: Q.Release = (float)Clamp(Math.Round(Q.Release / 10) * 10 + d * 10, 10, 2000); break;
            }
        }

        // a double click puts the value under the mouse back to where a new device starts
        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            var part = Hit(e.Location);
            // (the second press of a quick pair on S or M has already come as a press of its own: nothing more to do here)
            if (part == Part.Solo || part == Part.Mute) { base.OnMouseDoubleClick(e); return; }
            if (e.Button == MouseButtons.Left && part != Part.None && part != Part.Zone && part != Part.ViewTime && part != Part.ViewFreq && part != Part.Count && part != Part.Pill)
            {
                drag = Part.None; var s = SoundParams.Start();
                if (part == Part.Makeup || part == Part.DialMakeup) Q.Makeup = 0;
                else if (part == Part.DialThr) Q.Thr = s.Thr;
                else if (part == Part.DialRatio) Q.Ratio = s.Ratio;
                else if (part == Part.DialSplit1) P.Split = 250; // where the page's sliders start
                else if (part == Part.DialSplit2) P.Split2 = 4000;
                else if (part == Part.Attack) Q.Attack = s.Attack;
                else if (part == Part.Release) Q.Release = s.Release;
                else if (part == Part.Knee) Q.Knee = s.Knee;
                else if (part == Part.CeilFader) P.Ceil = s.Ceil;
                else { base.OnMouseDoubleClick(e); return; }
                Fire();
            }
            base.OnMouseDoubleClick(e);
        }

        static float Db(float lin) { return lin <= 0.00001f ? -100f : 20f * (float)Math.Log10(lin); }

        // one more moment of the sound (30 a second from the page): the peaks in and out, linear, and what the compressor
        // and the limiter took off, dB
        public void Feed(bool on, float inPeak, float outPeak, float grDb, float limDb, float levelDb)
        {
            if (demo) return;
            if (!on) { if (live) { live = false; hCount = 0; nowIn = -100; nowLv = -140; nowGr = nowLim = 0; Invalidate(); } return; }
            Push(Db(inPeak), Db(outPeak), grDb, limDb, bGr); nowLv = levelDb; // bGr: all the bands, from FeedBands just before
            live = true;
            Invalidate(); // the whole picture every moment (20.11, the user: no caches, everything live)
        }

        // the same moment per band, for the monitor by frequencies (before Feed, which draws): the level each band reads
        // and what is taken off it, dB; null = nothing. The levels rise at once and fall slower, so the columns do not flicker
        public void FeedBands(float[] gr, float[] lv)
        {
            if (demo) return;
            for (int i = 0; i < 3; i++)
            {
                float l = lv == null ? -140 : Math.Max(-140, lv[i]);
                bLv[i] = l > bLv[i] ? bLv[i] + (l - bLv[i]) * 0.7f : bLv[i] + (l - bLv[i]) * 0.25f;
                bGr[i] = gr == null ? 0 : gr[i];
            }
        }

        void Push(float i, float o, float gr, float lim, float[] grs)
        {
            Array.Copy(hIn, 1, hIn, 0, Hist - 1); Array.Copy(hOut, 1, hOut, 0, Hist - 1); Array.Copy(hLim, 1, hLim, 0, Hist - 1);
            hIn[Hist - 1] = i; hOut[Hist - 1] = o; hLim[Hist - 1] = lim; if (hCount < Hist) hCount++;
            for (int b = 0; b < 3; b++) { Array.Copy(hGrB[b], 1, hGrB[b], 0, Hist - 1); hGrB[b][Hist - 1] = grs[b]; }
            nowIn = i; nowGr = gr; nowLim = lim;
        }

        // for the off-screen render: a made-up sound that goes over the threshold now and then
        public void Demo()
        {
            demo = true; hCount = 0;
            for (int k = 0; k < Hist; k++)
            {
                float i = (float)(-27 + 13 * Math.Sin(k / 17.0) + 7 * Math.Sin(k / 3.1) + 4 * Math.Sin(k / 1.3)); if (i > -1) i = -1;
                float gr = Dim ? 0 : (float)Gr(i), o = i + gr + (Dim ? 0 : Q.Makeup);
                float lim = P.Limit && o > P.Ceil ? P.Ceil - o : 0;
                var grs = new float[3]; for (int b = 0; b < 3; b++) grs[b] = Dim ? 0 : (float)GrOf(P.Parts[b], i);
                Push(i, Math.Min(0, o + lim), gr, lim, grs); nowLv = i;
            }
            float[] lv = { -11, -21, -30 };
            for (int b = 0; b < 3; b++) { bLv[b] = lv[b]; bGr[b] = Dim ? 0 : (float)GrOf(P.Parts[b], lv[b]); }
            live = true; Invalidate();
        }

        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }

        // the monitor by frequencies (see ByFreq): the columns of the bands at work, the splits between them
        // the last ten seconds as lines: what comes in, what goes out, what the compressor and the limiter take off, the
        // threshold (all of it moves, so it is drawn through ILive: GDI+ or the graphics card)
        void HistTime(ILive cv, Rectangle hist, Color line, Color lit, Color limC, bool lim)
        {
            if (live && hCount > 1)
            {
                int n = hCount; float dx = hist.Width / (float)(Hist - 1); var hGr = hGrB[Math.Max(0, Math.Min(2, Band))]; // the band shown
                var pin = new List<PointF>(); var pout = new PointF[n]; var pgr = new List<PointF>(); var pall = new List<PointF>();
                pin.Add(new PointF(hist.Right - (n - 1) * dx, hist.Bottom)); pgr.Add(new PointF(hist.Right - (n - 1) * dx, hist.Y)); pall.Add(pgr[0]);
                for (int k = 0; k < n; k++)
                {
                    int at = Hist - n + k; float x = hist.Right - (n - 1 - k) * dx;
                    pin.Add(new PointF(x, Y(hIn[at]))); pout[k] = new PointF(x, Y(hOut[at]));
                    pgr.Add(new PointF(x, hist.Y + hist.Height * (float)(Clamp(-hGr[at], 0, Hi - Lo) / (Hi - Lo))));
                    pall.Add(new PointF(x, hist.Y + hist.Height * (float)(Clamp(-hGr[at] - hLim[at], 0, Hi - Lo) / (Hi - Lo))));
                }
                pin.Add(new PointF(hist.Right, hist.Bottom)); pgr.Add(new PointF(hist.Right, hist.Y)); pall.Add(new PointF(hist.Right, hist.Y));
                cv.FillPoly(Color.FromArgb(Dim ? 20 : 40, Theme.Text), pin.ToArray());
                cv.Lines(Color.FromArgb(Dim ? 90 : 210, Theme.Text), Theme.S(1.4f), pout);
                // from the top down: what the compressor takes off, and under it what the limiter adds to that
                if (lim)
                {
                    cv.FillPoly(Color.FromArgb(80, limC), pall.ToArray());
                    cv.Lines(limC, Theme.S(1.4f), pall.Skip(1).Take(n).ToArray());
                }
                if (!Dim)
                {
                    if (lim) cv.FillPoly(Theme.Card, pgr.ToArray()); // the compressor's part is not tinted by the limiter's colour
                    cv.FillPoly(Color.FromArgb(70, line), pgr.ToArray());
                    cv.Lines(line, Theme.S(1.4f), pgr.Skip(1).Take(n).ToArray());
                }
            }
            bool lh = hot == Part.Line || drag == Part.Line;
            cv.Line(Dim ? Theme.Muted : lh ? lit : line, Theme.S(lh ? 1.8f : 1.2f), hist.X, Y(Q.Thr), hist.Right, Y(Q.Thr), true);
        }

        // the monitor by frequencies; stage: 1 what stands under the levels (the bands' ground, the decade lines), 2 the
        // levels and what is taken off (they move: through cv), 4 what lies over them (thresholds, names, S and M, the
        // splits). A paint by the program does all three at once; the graphics card gets 1 and 4 as pictures.
        void PaintBands(Graphics g, Rectangle hist, Color limC, bool lim, int stage, ILive cv)
        {
            int n = P.PartCount; float pad = Theme.S(5);
            bool under = (stage & 1) != 0, moving = (stage & 2) != 0, top = (stage & 4) != 0;
            // the frequency scale: a line per decade, the numbers along the bottom edge
            if (under) using (var pen = new Pen(Theme.Border)) foreach (var f in new[] { 100, 1000, 10000 }) g.DrawLine(pen, FX(f), hist.Y, FX(f), hist.Bottom);
            foreach (var z in Zones())
            {
                float x0 = FX(z.From), x1 = FX(z.To); var q = P.Parts[z.Part];
                bool sel = n == 1 || z.Part == Band, over = !Dim && zoneHot == z.Part && (hot == Part.Zone || hot == Part.Line) && drag == Part.None;
                Color col = Dim ? Theme.Muted : n == 1 ? Theme.Accent : PartColor[z.Part];
                if (under) using (var b = new SolidBrush(Color.FromArgb(Dim ? 8 : sel ? 34 : over ? 24 : 12, col))) g.FillRectangle(b, x0, hist.Y, x1 - x0, hist.Height);
                float bx = x0 + pad, bw = Math.Max(Theme.S(2), x1 - x0 - 2 * pad);
                // from the bottom: the level the band reads now
                if (moving && live && bLv[z.Part] > Lo)
                {
                    float y = Y(bLv[z.Part]);
                    cv.FillRect(Color.FromArgb(Dim ? 20 : sel ? 60 : 38, Theme.Text), bx, y, bw, hist.Bottom - y);
                    cv.Line(Color.FromArgb(Dim ? 90 : sel ? 220 : 140, Theme.Text), Theme.S(1.4f), bx, y, bx + bw, y);
                }
                // from the top: what is taken off the band, and under it what the limiter adds to that (as in the last seconds)
                if (moving && live)
                {
                    double gr = Dim ? 0 : -bGr[z.Part], all = gr + (lim ? -nowLim : 0);
                    float hg = hist.Height * (float)(Clamp(gr, 0, Hi - Lo) / (Hi - Lo)), ha = hist.Height * (float)(Clamp(all, 0, Hi - Lo) / (Hi - Lo));
                    if (lim && ha - hg > 0.5f)
                    {
                        cv.FillRect(Color.FromArgb(80, limC), bx, hist.Y + hg, bw, ha - hg);
                        cv.Line(limC, Theme.S(1.4f), bx, hist.Y + ha, bx + bw, hist.Y + ha);
                    }
                    if (hg > 0.5f)
                    {
                        cv.FillRect(Color.FromArgb(sel ? 90 : 60, col), bx, hist.Y, bw, hg);
                        cv.Line(col, Theme.S(1.4f), bx, hist.Y + hg, bx + bw, hist.Y + hg);
                    }
                }
                // its threshold (with the moving parts: dragging it must not make the pictures of the card anew)
                if (moving)
                {
                    bool lh = sel && (hot == Part.Line || drag == Part.Line) && (drag == Part.Line || zoneHot == z.Part);
                    // (lighter than the band's colour: the colour itself was lost over the grey of the level)
                    cv.Line(Dim ? Theme.Muted : lh ? Color.White : Lighter(Lighter(col)), Theme.S(lh ? 2f : sel ? 1.6f : 1.3f), x0, Y(q.Thr), x1, Y(q.Thr), true);
                }
                if (!top) continue;
                // a band that does not go out now (its M, or another band's S): its column under a veil
                bool silent = (Silent >> z.Part & 1) != 0;
                if (silent) using (var b = new SolidBrush(Color.FromArgb(165, Theme.Card))) g.FillRectangle(b, x0, hist.Y, x1 - x0, hist.Height);
                // the band shown is framed; its name on top, with its colour beside it
                if (sel && n > 1 && !Dim) using (var pen = new Pen(Color.FromArgb(200, col), Theme.S(1.4f))) g.DrawRectangle(pen, x0 + 0.5f, hist.Y + 0.5f, x1 - x0 - 1f, hist.Height - 1f);
                string name = n == 1 ? "Весь звук" : ZoneNames[z.Part];
                int tw = TextRenderer.MeasureText(g, L.T(name), Theme.Small, new Size(1000, 100), TextFormatFlags.NoPadding).Width, dot = Theme.S(8), dgap = Theme.S(6), padX = Theme.S(9);
                Color ink = Dim || silent ? Theme.Muted : sel ? Theme.Text : over ? Theme.Text : Theme.Text2;
                int ty = hist.Y + Theme.S(6), th = Theme.S(20);
                if (!Dim)
                    foreach (bool mute in new[] { false, true })
                    {
                        var lr = ListenRect(z.Part, mute); if (lr.Width == 0) continue;
                        bool on = ((mute ? MuteMask : SoloMask) >> z.Part & 1) != 0, bh = hot == (mute ? Part.Mute : Part.Solo) && zoneHot == z.Part;
                        var lf = new RectangleF(lr.X + 0.5f, lr.Y + 0.5f, lr.Width - 1f, lr.Height - 1f); Color lc = mute ? MuteC : SoloC;
                        Theme.Fill(g, lf, Theme.S(5), on ? lc : bh ? Theme.Raised : Color.FromArgb(215, Theme.Card));
                        Theme.Stroke(g, lf, Theme.S(5), on ? lc : bh ? Theme.BorderStrong : Theme.Border);
                        Glyph(g, mute ? "M" : "S", Theme.Small, lr, on ? Color.FromArgb(0x1c, 0x14, 0x0a) : bh ? Theme.Text : Theme.Text2);
                    }
                if (dot + dgap + tw + 2 * padX + Theme.S(4) <= x1 - x0)
                {
                    float whole = dot + dgap + tw, sx = (x0 + x1) / 2 - whole / 2f;
                    // on a plate of its own: what is taken off hangs from the top and its edge would run through the name
                    Theme.Fill(g, new RectangleF(sx - padX, ty, whole + 2 * padX, th), th / 2f, Color.FromArgb(205, Theme.Card));
                    using (var b = new SolidBrush(col)) g.FillEllipse(b, sx, ty + (th - dot) / 2f, dot, dot);
                    Theme.Text1(g, name, Theme.Small, new Rectangle((int)Math.Round(sx + dot + dgap), ty, tw + Theme.S(3), th), ink, TextFormatFlags.NoPadding);
                }
                else Theme.Text1(g, name, Theme.Small, new Rectangle((int)x0, ty, (int)(x1 - x0), th), ink, Mid);
            }
            if (!top) return;
            // the numbers of the scale
            foreach (var f in hist.Width > Theme.S(520) ? new[] { 50, 100, 200, 500, 1000, 2000, 5000, 10000 } : new[] { 100, 1000, 10000 })
                Theme.Text1(g, f >= 1000 ? f / 1000 + "k" : f.ToString(), Theme.Small, new Rectangle((int)FX(f) - Theme.S(30), hist.Bottom - Theme.S(19), Theme.S(60), Theme.S(18)), Theme.Muted, TextFormatFlags.HorizontalCenter);
            // a split that is not at work: where it would stand
            using (var dots = new Pen(Color.FromArgb(Dim ? 60 : 150, Theme.Text2), Theme.S(1.2f)) { DashStyle = DashStyle.Dash })
            {
                if (n < 2) g.DrawLine(dots, FX(Ghost1), hist.Y, FX(Ghost1), hist.Bottom);
                if (n < 3) g.DrawLine(dots, FX(Ghost2), hist.Y, FX(Ghost2), hist.Bottom);
            }
            // the splits at work: a line with a grip and its frequency beside the grip
            for (int k = 0; k < n - 1; k++)
            {
                var s = SplitAt(k); var pk = k == 0 ? Part.Split1 : Part.Split2; bool h = !Dim && (hot == pk || drag == pk);
                using (var pen = new Pen(Color.FromArgb(Dim ? 70 : h ? 255 : 150, Theme.Text), Theme.S(h ? 1.8f : 1.2f))) g.DrawLine(pen, s.X, hist.Y, s.X, hist.Bottom);
                float size = Theme.S(h ? 19 : 16);
                Theme.GlassKnob(g, s.X, s.Y, size, 1f, Dim ? Theme.Muted : h ? Theme.Text : Theme.Text2, Theme.S(4.5f), Dim, 180f, null, -1f, new[] { new PointF(s.X, s.Y - size), new PointF(s.X, s.Y + size) });
                string hz = EqGraph.Hz(k == 0 ? P.Split : P.Split2); int lw = Theme.S(76);
                bool left = s.X + Theme.S(12) + lw > hist.Right;
                Theme.Text1(g, hz, Theme.Small, new Rectangle(left ? (int)s.X - Theme.S(12) - lw : (int)s.X + Theme.S(12), (int)s.Y - Theme.S(10), lw, Theme.S(20)), Dim ? Theme.Muted : h ? Theme.Text : Theme.Text2, left ? TextFormatFlags.Right : 0);
            }
        }

        static string DbText(double v) { int n = (int)Math.Round(v); return (n < 0 ? "−" : "") + Math.Abs(n) + " дБ"; }
        static string Plus(double v) { int n = (int)Math.Round(v); return (n > 0 ? "+" : n < 0 ? "−" : "") + Math.Abs(n) + " дБ"; }
        string RatioText { get { return Q.Ratio.ToString("0.#", CultureInfo.InvariantCulture) + " : 1"; } }
        // the limiter's ceiling fader (see CeilStrip): grey and still while the limiter is off
        void CeilFader(Graphics g, Color limC, bool lim, bool ch)
        {
            var cs = CeilStrip; if (cs.Width <= 0) return;
            float cx = cs.X + cs.Width / 2f, th = Theme.S(5), top = CeilTop, bottom = CeilBottom;
            if (ch && lim) Theme.Fill(g, new RectangleF(cs.X + 1, cs.Y - Theme.S(4), cs.Width - 2, cs.Height + Theme.S(8)), Theme.S(6), Color.FromArgb(46, Theme.Text));
            var ic = lim ? limC : Theme.Muted;
            using (var pen = new Pen(ic, Theme.S(1.6f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            {
                float iy = cs.Y + Theme.S(6), w = Theme.S(15), a = Theme.S(2.5f);
                g.DrawLine(pen, cx - w / 2, iy, cx + w / 2, iy);
                g.DrawLine(pen, cx, iy + Theme.S(3), cx, iy + Theme.S(11));
                g.DrawLines(pen, new[] { new PointF(cx - a, iy + Theme.S(3) + a), new PointF(cx, iy + Theme.S(3)), new PointF(cx + a, iy + Theme.S(3) + a) });
            }
            Theme.Tube(g, new RectangleF(cx - th / 2f, top, th, bottom - top));
            float y = CeilY(P.Ceil);
            if (y - top > 1f) Theme.Fill(g, new RectangleF(cx - th / 2f, top, th, y - top), th / 2f, lim ? limC : Theme.Muted);
            Theme.GlassKnob(g, cx, y, Theme.S(ch && lim ? 16 : 14), lim ? 1f : 0f, lim ? limC : Theme.Muted, th, !lim, 270f, null, lim ? y - top : 0f); // lit towards the top: what is cut off
            string v = P.Ceil > -0.05f ? "0" : "−" + (-P.Ceil).ToString("0.#", CultureInfo.InvariantCulture);
            Theme.Text1(g, v, Theme.Small, new Rectangle((int)cx - Theme.S(24), Curve.Bottom + Theme.S(3), Theme.S(48), Theme.S(20)), lim ? (ch ? Theme.Text : Theme.Text2) : Theme.Muted, TextFormatFlags.HorizontalCenter);
        }

        string CeilText { get { return (P.Ceil > -0.05f ? "0" : "−" + (-P.Ceil).ToString("0.0", CultureInfo.InvariantCulture)) + " дБ"; } }

        // the transfer curve with its fill, the other bands, the threshold, the ceiling and the three points; the dot of the
        // sound now only with withDot
        void CurveBlock(Graphics g, Rectangle c, bool withDot)
        {
            Color line = Dim ? Theme.Muted : P.PartCount == 1 ? Theme.Accent : PartColor[Math.Max(0, Math.Min(2, Band))];
            Color lit = Dim ? Theme.Muted : Lighter(line);
            Color limC = Theme.Warning; bool lim = P.Limit, ch = hot == Part.Ceil || drag == Part.Ceil || hot == Part.CeilFader || drag == Part.CeilFader;
            float cy = Y(P.Ceil);
            var clip = g.Clip;
            int steps = Math.Max(40, c.Width / 2);
            var curve = new PointF[steps + 1];
            for (int i = 0; i <= steps; i++) { double x = Lo + (Hi - Lo) * i / steps; curve[i] = new PointF(X(x), Y(Out(x))); }
            // with the limiter on the curve goes flat at the ceiling; what the compressor alone gives stays as a dashed line above it
            var real = curve;
            if (lim) { real = new PointF[curve.Length]; for (int i = 0; i < curve.Length; i++) real[i] = new PointF(curve[i].X, Math.Max(curve[i].Y, cy)); }
            g.SetClip(c);
            using (var path = new GraphicsPath())
            {
                path.AddLines(real);
                path.AddLine(real[steps], new PointF(c.Right, c.Bottom)); path.AddLine(new PointF(c.Right, c.Bottom), new PointF(c.X, c.Bottom)); path.CloseFigure();
                using (var b = new SolidBrush(Color.FromArgb(Dim ? 14 : 30, line))) g.FillPath(b, path);
            }
            using (var dash = new Pen(Color.FromArgb(Dim ? 60 : 110, Theme.Text2)) { DashStyle = DashStyle.Dash }) g.DrawLine(dash, X(Q.Thr), c.Y, X(Q.Thr), c.Bottom);
            // the other bands at work, thin, each in its colour
            foreach (int other in P.ActiveParts)
            {
                if (other == Band) continue;
                var q = P.Parts[other]; var pts = new PointF[steps + 1];
                for (int i = 0; i <= steps; i++) { double x = Lo + (Hi - Lo) * i / steps; double o = x + GrOf(q, x) + q.Makeup; pts[i] = new PointF(X(x), lim ? Math.Max(Y(o), cy) : Y(o)); }
                using (var pen = new Pen(Color.FromArgb(Dim ? 70 : 170, Dim ? Theme.Muted : PartColor[other]), Theme.S(1.4f)) { LineJoin = LineJoin.Round }) g.DrawLines(pen, pts);
            }
            if (lim) using (var pen = new Pen(Color.FromArgb(Dim ? 70 : 120, line), Theme.S(1.4f)) { DashStyle = DashStyle.Dash, LineJoin = LineJoin.Round }) g.DrawLines(pen, curve);
            using (var pen = new Pen(line, Theme.S(2)) { LineJoin = LineJoin.Round }) g.DrawLines(pen, real);
            g.Clip = clip;
            // the ceiling itself (it may lie on the very top edge of the plot, so it is drawn unclipped)
            if (lim) using (var pen = new Pen(ch ? Color.FromArgb(0xf6, 0xd0, 0x7a) : limC, Theme.S(ch ? 2.2f : 1.5f))) g.DrawLine(pen, c.X, cy, c.Right, cy);
            if (withDot) { g.SetClip(c); NowDot(new GdiLive(g), c); g.Clip = clip; }

            foreach (var p in new[] { Part.Makeup, Part.Ratio, Part.Thr })
            {
                var k = KnobAt(p); bool h = p == hot || p == drag;
                float size = Theme.S(h ? 19 : 16);
                int a = 0, b2 = curve.Length - 1;
                while (a + 1 < curve.Length && curve[a + 1].X < k.X - size) a++;
                while (b2 - 1 >= 0 && curve[b2 - 1].X > k.X + size) b2--;
                var piece = new PointF[Math.Max(0, b2 - a + 1)]; if (piece.Length > 0) Array.Copy(curve, a, piece, 0, piece.Length);
                Theme.GlassKnob(g, k.X, k.Y, size, 1f, h ? lit : line, Theme.S(4.5f), Dim, 180f, null, -1f, piece);
            }
        }

        // the sound now: from where it would be without the compressor down to where it is
        void NowDot(ILive cv, Rectangle c)
        {
            if (!live || Dim || nowLv <= Lo) return;
            float x = X(nowLv), y0 = Y(nowLv + Q.Makeup), y1 = Y(nowLv + nowGr + Q.Makeup + nowLim);
            if (y1 - y0 > 1.5f) cv.Line(Color.FromArgb(170, Theme.Text), Theme.S(1.4f), x, y0, x, y1);
            float d = Theme.S(7);
            cv.Ellipse(Theme.Text, x - d / 2, y1 - d / 2, d, d);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var card = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            Theme.Fill(g, card, Theme.S(8), Theme.Card);
            Theme.Stroke(g, card, Theme.S(8), Theme.Border);
            SyncPlates();
            var c = Curve; var hist = History; var pn = Panel;
            if (c.Width < 20 || Width < c.Right + Theme.S(10)) return;
            // one band: the program's red; in bands: the colour of the band shown
            Color line = Dim ? Theme.Muted : P.PartCount == 1 ? Theme.Accent : PartColor[Math.Max(0, Math.Min(2, Band))];
            Color lit = Dim ? Theme.Muted : Lighter(line);
            Color limC = Theme.Warning; bool lim = P.Limit, ch = hot == Part.Ceil || drag == Part.Ceil || hot == Part.CeilFader || drag == Part.CeilFader;
            float cy = Y(P.Ceil);
            // a paint of a part of the picture (Windows uncovering it) skips what lies outside its clip
            var want = e.ClipRectangle;
            bool doCurve = want.IntersectsWith(Rectangle.Inflate(c, Theme.S(14), Theme.S(14))), doHist = hist.Width > 0 && want.IntersectsWith(Rectangle.Inflate(hist, 2, 2));
            var clip = g.Clip;

            // the scale: the same dB for the levels in (across) and out (up), a line every 12
            using (var pen = new Pen(Theme.Border))
            using (var strong = new Pen(Theme.BorderStrong))
            {
                for (int db = (int)Lo; db <= Hi; db += 12)
                {
                    float y = Y(db), x = X(db);
                    string t = db == 0 ? "0" : "−" + (-db);
                    g.DrawLine(db == 0 ? strong : pen, c.X, y, c.Right, y);
                    g.DrawLine(db == 0 ? strong : pen, x, c.Y, x, c.Bottom);
                    Theme.Text1(g, t, Theme.Small, new Rectangle(0, (int)y - Theme.S(10), c.X - Theme.S(8), Theme.S(20)), Theme.Muted, TextFormatFlags.Right);
                    Theme.Text1(g, t, Theme.Small, new Rectangle((int)x - Theme.S(30), c.Bottom + Theme.S(4), Theme.S(60), Theme.S(18)), Theme.Muted, TextFormatFlags.HorizontalCenter);
                    if (hist.Width > 0) g.DrawLine(db == 0 ? strong : pen, hist.X, y, hist.Right, y);
                }
                if (hist.Width > 0) { g.DrawLine(pen, hist.X, hist.Y, hist.X, hist.Bottom); g.DrawLine(pen, hist.Right, hist.Y, hist.Right, hist.Bottom); }
                // out = in: where the curve would run with no compressor
                using (var dash = new Pen(Theme.BorderStrong) { DashStyle = DashStyle.Dash }) g.DrawLine(dash, c.X, c.Bottom, c.Right, c.Y);
            }
            CeilFader(g, limC, lim, ch);

            // the curve
            if (doCurve) CurveBlock(g, c, true);

            // the last ten seconds
            if (hist.Width > 0)
            {
                if (doHist)
                {
                g.SetClip(hist);
                var gl = new GdiLive(g);
                if (ByFreq) PaintBands(g, hist, limC, lim, 7, gl);
                else HistTime(gl, hist, line, lit, limC, lim);
                g.Clip = clip;
                if (lim) using (var pen = new Pen(ch ? Color.FromArgb(0xf6, 0xd0, 0x7a) : limC, Theme.S(ch ? 2.2f : 1.5f))) g.DrawLine(pen, hist.X, cy, hist.Right, cy);
                }
                // which monitor: the two words at the right end of the line under it
                var vt = ViewRect(false);
                if (want.IntersectsWith(new Rectangle(hist.X - Theme.S(4), c.Bottom + 2, Width - hist.X + Theme.S(4), Theme.S(28))))
                {
                foreach (bool fr in new[] { false, true })
                {
                    var vr = ViewRect(fr); bool on = ByFreq == fr, vh = hot == (fr ? Part.ViewFreq : Part.ViewTime);
                    if (on || vh) Theme.Fill(g, vr, Theme.S(5), Color.FromArgb(on ? 46 : 22, Theme.Text));
                    Theme.Text1(g, fr ? "По частотам" : "Во времени", Theme.Small, vr, on ? Theme.Text : vh ? Theme.Text2 : Theme.Muted, Mid);
                }
                // what is what, under the plot
                int lx = hist.X, ly = c.Bottom + Theme.S(4), sw = Theme.S(10);
                var legend = new List<KeyValuePair<string, Color>> { new KeyValuePair<string, Color>("На входе", Color.FromArgb(110, Theme.Text)), new KeyValuePair<string, Color>("На выходе", Theme.Text), new KeyValuePair<string, Color>("Сжатие", line), new KeyValuePair<string, Color>("Порог", line) };
                if (ByFreq) legend.RemoveAt(1); // the columns show what comes into each band, not what leaves
                if (lim) legend.Add(new KeyValuePair<string, Color>("Лимитер", limC));
                foreach (var it in legend)
                {
                    string name = L.T(it.Key);
                    int w = TextRenderer.MeasureText(g, name, Theme.Small, new Size(1000, 100), TextFormatFlags.NoPadding).Width + Theme.S(12); // Text1 draws with its own margins
                    if (lx + sw + Theme.S(6) + w > vt.X - Theme.S(8)) break;
                    if (it.Key == "На входе" || it.Key == "Сжатие" || it.Key == "Лимитер") Theme.Fill(g, new RectangleF(lx, ly + Theme.S(5), sw, Theme.S(8)), Theme.S(2), it.Value);
                    else using (var pen = new Pen(it.Value, Theme.S(1.6f)) { DashStyle = it.Key == "Порог" ? DashStyle.Dash : DashStyle.Solid }) g.DrawLine(pen, lx, ly + Theme.S(9), lx + sw, ly + Theme.S(9));
                    Theme.Text1(g, it.Key, Theme.Small, new Rectangle(lx + sw + Theme.S(6), ly, w, Theme.S(18)), Theme.Muted);
                    lx += sw + Theme.S(6) + w + Theme.S(8);
                }
                }
            }

            // the dials
            if (pn.Width > 0)
            {
                var parts = Dials;
                // a split that is not at work shows where the page keeps it, grey
                float sp1 = P.Split > 0 ? P.Split : Ghost1, sp2 = P.Split2 > 0 ? P.Split2 : Ghost2;
                string[] names = { "Атака", "Восстановление", "Колено", "Порог", "Степень сжатия", "Усиление после", "Граница низа", "Граница верха" };
                string[] values = { (int)Math.Round(Q.Attack) + " мс", (int)Math.Round(Q.Release) + " мс", (int)Math.Round(Q.Knee) + " дБ", DbText(Q.Thr), RatioText, Plus(Q.Makeup), EqGraph.Hz(sp1), EqGraph.Hz(sp2) };
                float[] fr = { (float)(Math.Log(Math.Max(1, Q.Attack)) / Math.Log(200)), (float)(Math.Log(Math.Max(10, Q.Release) / 10) / Math.Log(200)), Q.Knee / 24f,
                    (Q.Thr + 60) / 60f, (float)(Math.Log(Math.Max(1, Q.Ratio)) / Math.Log(20)), (Q.Makeup + 12) / 36f,
                    (float)(Math.Log(Math.Max(80, sp1) / 80) / Math.Log(600 / 80.0)), (float)(Math.Log(Math.Max(1200, sp2) / 1200) / Math.Log(10)) };
                // in four rows a dial's name and value keep closer to it, so the room is between the dials, not inside one
                // (with the name 42 over the middle and the value 21 under it, one dial's value touched the next one's name)
                bool four = DialCols == 2; int nameUp = Theme.S(four ? 38 : 42), valueDown = Theme.S(four ? 19 : 21); float dialR = four ? 16f : 17f;
                for (int k = 0; k < DialCount; k++)
                {
                    bool off = Dim || !DialOn(k), h = !off && (hot == parts[k] || drag == parts[k]); var at = DialAt(k); var cell = DialCell(k);
                    if (!want.IntersectsWith(new Rectangle(cell.X, at.Y - nameUp - Theme.S(2), cell.Width, nameUp + valueDown + Theme.S(26)))) continue;
                    // all eight in one colour while they work (the user: the splits too, grey only when not at work)
                    Color dc = h ? lit : line;
                    // 19.11 (the user's order): the name on top, the dial under it, the value under the dial, all on one middle
                    if (h) Theme.Fill(g, new RectangleF(cell.X, at.Y - nameUp - Theme.S(1), cell.Width, nameUp + valueDown + Theme.S(22)), Theme.S(6), Color.FromArgb(40, Theme.Text));
                    Theme.Text1(g, names[k], Theme.Small, new Rectangle(cell.X, at.Y - nameUp, cell.Width, Theme.S(20)), off ? Theme.Muted : Theme.Text2, Mid | TextFormatFlags.EndEllipsis);
                    EqGraph.Dial(g, at.X, at.Y, fr[k], off ? Theme.Muted : dc, h, off, dialR);
                    Theme.Text1(g, values[k], Theme.Small, new Rectangle(cell.X, at.Y + valueDown, cell.Width, Theme.S(20)), off ? Theme.Muted : Theme.Text, Mid);
                }
            }

            // the bands: how many, and the one being set
            var bc = BandsCol;
            if (bc.Width > 0 && want.IntersectsWith(bc))
            {
                int n = P.PartCount; var act = P.ActiveParts;
                int bandsTop = (int)Math.Round(BandsTopF);
                Theme.Text1(g, "Полосы", Theme.Small, new Rectangle(bc.X, bandsTop, bc.Width, Theme.S(20)), Dim ? Theme.Muted : Theme.Text2, Mid);
                for (int i = 0; i < 3; i++)
                {
                    var r = CountRect(i); var rf = new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1f, r.Height - 1f);
                    bool on = i == n - 1, h = !Dim && hot == Part.Count && idxHot == i;
                    Theme.Fill(g, rf, Theme.S(6), Dim ? (on ? Theme.Raised : Color.FromArgb(14, Theme.Text)) : on ? Theme.AccentBg : h ? Theme.Raised : Color.FromArgb(14, Theme.Text));
                    Theme.Stroke(g, rf, Theme.S(6), on && !Dim ? Theme.Accent : Theme.Border);
                    Glyph(g, (i + 1).ToString(), Theme.Body, r, Dim ? Theme.Muted : on ? Theme.AccentFg : Theme.Text2);
                }
                Theme.Text1(g, "Настраиваемая", Theme.Small, new Rectangle(bc.X, bandsTop + Theme.S(BandsTitle2), bc.Width, Theme.S(20)), Dim ? Theme.Muted : Theme.Text2, Mid);
                for (int i = 0; i < 3; i++)
                {
                    float a = plate[i]; if (a < 0.03f) continue; // only the bands at work, and the one coming or going
                    var pr = PlateRect(i, true); var rf = new RectangleF(pr.X + 0.5f, pr.Y + 0.5f, pr.Width - 1f, Math.Max(1f, pr.Height - 1f));
                    bool at = act.Contains(i), on = at && (n == 1 || i == Band), h = !Dim && hot == Part.Pill && idxHot == i;
                    Color col = Dim ? Theme.Muted : PartColor[i];
                    Func<Color, Color> fade = c0 => Color.FromArgb((int)(c0.A * a), c0);
                    float rad = Math.Min(Theme.S(6), rf.Height / 2f);
                    Theme.Fill(g, rf, rad, fade(on && !Dim ? Color.FromArgb(58, col) : h ? Theme.Raised : Color.FromArgb(14, Theme.Text)));
                    Theme.Stroke(g, rf, rad, fade(on && !Dim ? col : Theme.Border));
                    float dot = Theme.S(8) * a;
                    using (var b = new SolidBrush(fade(col))) g.FillEllipse(b, pr.X + Theme.S(15) - dot / 2f, pr.Y + (pr.Height - dot) / 2f, dot, dot);
                    if (a > 0.5f) // the name comes in over the second half, from the colour of the card
                    {
                        Color ink = Dim ? Theme.Muted : on || h ? Theme.Text : Theme.Text2, from = Theme.Card; float t = (a - 0.5f) * 2f;
                        ink = Color.FromArgb((int)(from.R + (ink.R - from.R) * t), (int)(from.G + (ink.G - from.G) * t), (int)(from.B + (ink.B - from.B) * t));
                        Theme.Text1(g, n == 1 && i == 0 ? "Весь звук" : ZoneNames[i], Theme.Small, new Rectangle((int)pr.X + Theme.S(23), (int)Math.Round(pr.Y), (int)pr.Width - Theme.S(27), (int)Math.Round(pr.Height)), ink);
                        // listened to apart: the letter at the plate's end (seen also while the other monitor is shown)
                        bool quiet = (Silent >> i & 1) != 0, solo = !quiet && P.Comp && n > 1 && (SoloMask >> i & 1) != 0;
                        if ((quiet || solo) && a > 0.9f) Glyph(g, quiet ? "M" : "S", Theme.Small, new Rectangle((int)pr.Right - Theme.S(26), (int)Math.Round(pr.Y), Theme.S(22), (int)Math.Round(pr.Height)), quiet ? MuteC : SoloC);
                    }
                }
            }

            var shown = drag != Part.None ? drag : hot;
            string hint;
            if (shown == Part.Ceil) hint = "Потолок: " + CeilText + " · " + "тянется вверх-вниз";
            else if (shown == Part.CeilFader) hint = "Потолок: " + CeilText + " · " + L.T("Не выпускает звук выше потолка, сколько бы ни поднял эквалайзер: без хрипа и перегруза") + " · " + "тяните вверх-вниз или крутите колесо";
            else if (Dim) hint = "Компрессор выключен";
            else if (shown == Part.Thr || shown == Part.Line) hint = "Порог: " + DbText(Q.Thr) + " · " + "колено: " + DbText(Q.Knee) + (shown == Part.Thr ? " · " + "колесо меняет колено" : "");
            else if (shown == Part.Ratio) hint = "Степень сжатия: " + RatioText + " · " + "тянется вверх-вниз";
            else if (shown == Part.Makeup) hint = "Усиление после: " + Plus(Q.Makeup) + " · " + "тянется вверх-вниз";
            else if (shown == Part.Attack) hint = "Атака: " + (int)Math.Round(Q.Attack) + " мс" + " · " + L.T("Как быстро компрессор берётся за громкий звук") + " · " + "тяните вверх-вниз или крутите колесо";
            else if (shown == Part.Release) hint = "Восстановление: " + (int)Math.Round(Q.Release) + " мс" + " · " + L.T("Как быстро он отпускает, когда стало тише") + " · " + "тяните вверх-вниз или крутите колесо";
            else if (shown == Part.Knee) hint = "Колено: " + DbText(Q.Knee) + " · " + L.T("Плавность перехода у порога: 0 — резкий") + " · " + "тяните вверх-вниз или крутите колесо";
            else if (shown == Part.DialThr) hint = "Порог: " + DbText(Q.Thr) + " · " + L.T("Громкость, с которой начинается сжатие") + " · " + "тяните вверх-вниз или крутите колесо";
            else if (shown == Part.DialRatio) hint = "Степень сжатия: " + RatioText + " · " + L.T("Во сколько раз убавляется то, что выше порога") + " · " + "тяните вверх-вниз или крутите колесо";
            else if (shown == Part.DialMakeup) hint = "Усиление после: " + Plus(Q.Makeup) + " · " + L.T("Возвращает громкость, которую забрало сжатие") + " · " + "тяните вверх-вниз или крутите колесо";
            else if (shown == Part.Solo) hint = "Соло: слышны только полосы с «S». Это для прослушивания — снимется само, когда вы уйдёте со страницы";
            else if (shown == Part.Mute) hint = "Без звука: эта полоса не слышна, остальные играют. Это для прослушивания — снимется само, когда вы уйдёте со страницы";
            else if (shown == Part.DialSplit1) hint = "Граница низа: " + EqGraph.Hz(P.Split) + " · " + L.T("Всё, что ниже, — полоса «Низ»") + " · " + "тяните вверх-вниз или крутите колесо";
            else if (shown == Part.DialSplit2) hint = "Граница верха: " + EqGraph.Hz(P.Split2) + " · " + L.T("Всё, что выше, — полоса «Верх»") + " · " + "тяните вверх-вниз или крутите колесо";
            else if (shown == Part.Count) hint = "Каждая полоса сжимается порознь и со своими настройками";
            else if (shown == Part.Pill) hint = idxHot == Band || P.PartCount == 1 ? "Точки, ручки и ползунки относятся к этой полосе" : "Щёлкните, чтобы настраивать эту полосу";
            else if (shown == Part.Split1) hint = "Граница низа: " + EqGraph.Hz(P.Split) + " · " + "тянется влево-вправо";
            else if (shown == Part.Split2) hint = "Граница верха: " + EqGraph.Hz(P.Split2) + " · " + "тянется влево-вправо";
            else if (shown == Part.Zone && P.PartCount == 1) hint = "Одна полоса на весь звук. Разделить его на две или три можно в «Полосах сжатия» ниже";
            else if (shown == Part.Zone && zoneHot != Band) hint = "Щёлкните, чтобы настраивать эту полосу";
            else if (shown == Part.Zone || (ByFreq && shown == Part.ViewFreq)) hint = "Щёлкните по полосе, чтобы настраивать её. Границы полос тянутся влево-вправо, пунктир порога — вверх-вниз";
            else if (Silent != 0) hint = "Сейчас слышны не все полосы: включено прослушивание (S или M на мониторе «По частотам»)";
            else hint = "Точки тянутся мышью: порог — влево-вправо, степень сжатия (справа) и усиление после (слева) — вверх-вниз. Колесо на точке порога меняет колено";
            int hintX = bc.Width > 0 ? bc.X : c.X;
            var hintR = new Rectangle(hintX, c.Bottom + Theme.S(28), Width - hintX - Theme.S(14), Theme.S(22));
            if (want.IntersectsWith(hintR)) Theme.Text1(g, hint, Theme.Small, hintR, Theme.Text2, TextFormatFlags.EndEllipsis);
        }
    }

    // The equalizer: the curve over a frequency axis, a knob per band. A knob is dragged (frequency and gain), the
    // wheel over it changes its width, a right click picks the kind of filter or deletes it, a double click on empty
    // space adds one. Behind the curve, what the device plays right now (the spectrum of the processed sound).
    class EqGraph : Painted
    {
        public SoundParams P = SoundParams.Start();
        public double Rate = 48000;
        public bool Dim;
        public event EventHandler Changed;
        const double MinF = 20, MaxF = 20000;
        // 19.11: the scale is ±18 dB, and ±24 while the curve goes beyond that (a preset fitted to Nahimic's measured
        // response raises the bass by 23 dB): otherwise the curve ran along the edge of the plot. Not changed while a
        // point is held, so the scale does not jump under the mouse.
        float Range = 18;
        void FitRange()
        {
            if (drag >= 0 || dragPart != Part.None) return;
            double top = 0;
            for (int i = 0; i <= 30; i++) top = Math.Max(top, Math.Abs(AudioFx.Response(P, MinF * Math.Pow(MaxF / MinF, i / 30.0), Rate)));
            foreach (var b in P.Bands) top = Math.Max(top, Math.Abs((b.HasGain ? b.Gain : 0) + P.Preamp));
            Range = top > 17.9 ? 24 : 18;
        }
        int hot = -1, drag = -1; bool armed;
        float[] spec; bool specOn;

        public static readonly string[] Kinds = { "", "Колокол", "Низкая полка", "Высокая полка", "Срез низких", "Срез высоких", "Режектор" };

        public EqGraph() { BackColor = Theme.Window; Height = Theme.S(390); } // 19.11: 300 -> 390, the user asked for the block 30 % taller

        // 19.11, the strips on the right of the plot (the user: the same bands as knobs, as mixing programs have them).
        // One strip per band, in the order of the bands' frequencies, so a strip stands where its point stands along the
        // plot: the kind of filter on top (a click opens the list), a fader for the gain, a dial for the frequency and
        // a dial for the width. They are the same values as the points: moving one moves the other, a band removed on
        // either side is gone on both, and after the last strip stands «+» while there are fewer than 12 bands.
        // The order is not changed while something is held, so a strip does not jump from under the mouse.
        // 20.11 (the user): the pre-gain («Общее усиление», before the equalizer) moved from a row of the page into the
        // picture, as the first strip left of the bands' ones: a fader of ±24 dB by half a dB, its value under it.
        enum Part { None, Kind, Fader, Freq, Wide, Add, Master }
        const int StripW = 27, Gutter = 24, MasterW = 33, MasterDb = 24;
        int[] order = new int[0];
        Part hotPart, dragPart; int dragBand = -1, dragY; float dragFrom;

        internal int[] Order()
        {
            if (order.Length != P.Bands.Count || (drag < 0 && dragPart == Part.None))
                order = Enumerable.Range(0, P.Bands.Count).OrderBy(i => P.Bands[i].Freq).ThenBy(i => i).ToArray();
            return order;
        }

        int Slots { get { return P.Bands.Count + (P.Bands.Count < SoundParams.MaxBands ? 1 : 0); } }
        // no strips in a window so narrow that the plot would have nothing left
        int PanelW { get { int w = Theme.S(MasterW + Gutter + Slots * StripW); return Width - Theme.S(46 + 14 + 12) - w < Theme.S(170) ? 0 : w; } }
        Rectangle Plot { get { int pw = PanelW; return new Rectangle(Theme.S(46), Theme.S(14), Width - Theme.S(46) - (pw > 0 ? pw + Theme.S(14 + 12) : Theme.S(18)), Height - Theme.S(72)); } }
        // 19.11: as tall as the plot, but standing in the middle of the card: the kind pictures as far from its top edge as
        // the lower dials from its bottom edge (level with the plot it stood high, the plot having its scale and the hint under it)
        internal Rectangle Panel { get { var r = Plot; return new Rectangle(r.Right + Theme.S(14), (Height - r.Height - Theme.S(2)) / 2, PanelW, r.Height); } }
        internal int StripX(int slot) { return Panel.X + Theme.S(MasterW + Gutter) + slot * Theme.S(StripW) + Theme.S(StripW) / 2; } // its middle
        internal int MasterX { get { return Panel.X + Theme.S(StripW) / 2; } }
        float MasterY(double db) { return (float)(FaderTop + (FaderBottom - FaderTop) * (0.5 - Math.Max(-MasterDb, Math.Min(MasterDb, db)) / (2.0 * MasterDb))); }
        internal int FaderTop { get { return Panel.Y + Theme.S(30); } }
        internal int FaderBottom { get { return Panel.Bottom - Theme.S(64); } }
        internal int FreqY { get { return Panel.Bottom - Theme.S(40); } }
        internal int WideY { get { return Panel.Bottom - Theme.S(12); } }
        float FaderY(double db) { return (float)(FaderTop + (FaderBottom - FaderTop) * (0.5 - Math.Max(-Range, Math.Min(Range, db)) / (2 * Range))); }

        // what of the strips is under the mouse; band: its index among the bands
        Part HitPanel(Point m, out int band)
        {
            band = -1;
            var p = Panel; if (p.Width <= 0 || m.X < p.X || m.X >= p.Right || m.Y < p.Y - Theme.S(4) || m.Y > p.Bottom + Theme.S(4)) return Part.None;
            if (m.X < p.X + Theme.S(StripW)) return Part.Master;
            if (m.X < p.X + Theme.S(MasterW + Gutter)) return Part.None;
            int slot = (m.X - p.X - Theme.S(MasterW + Gutter)) / Theme.S(StripW);
            var ord = Order();
            if (slot == ord.Length && ord.Length < SoundParams.MaxBands) return Part.Add;
            if (slot < 0 || slot >= ord.Length) return Part.None;
            band = ord[slot];
            if (m.Y < FaderTop - Theme.S(8)) return Part.Kind;
            if (m.Y <= FaderBottom + Theme.S(8)) return Part.Fader;
            if (Math.Abs(m.Y - FreqY) <= Theme.S(13)) return Part.Freq;
            return Part.Wide;
        }

        static double RoundHz(double f) { return f < 100 ? Math.Round(f) : f < 1000 ? Math.Round(f / 5) * 5 : f < 10000 ? Math.Round(f / 50) * 50 : Math.Round(f / 500) * 500; }

        // a new band from «+»: a bell with no gain in the widest gap between the bands there are
        void AddBand()
        {
            if (P.Bands.Count >= SoundParams.MaxBands) return;
            var fs = new List<double> { MinF }; fs.AddRange(P.Bands.Select(b => (double)b.Freq).OrderBy(f => f)); fs.Add(MaxF);
            int at = 0; double widest = 0;
            for (int i = 0; i + 1 < fs.Count; i++) { double gap = Math.Log(fs[i + 1] / fs[i]); if (gap > widest) { widest = gap; at = i; } }
            P.Bands.Add(new EqBand { Type = 1, Freq = (float)RoundHz(Math.Sqrt(fs[at] * fs[at + 1])), Gain = 0, Q = 1f });
            hot = P.Bands.Count - 1;
            Fire();
        }
        float X(double f) { var r = Plot; return (float)(r.X + r.Width * Math.Log(f / MinF) / Math.Log(MaxF / MinF)); }
        double FreqAt(float x) { var r = Plot; return MinF * Math.Pow(MaxF / MinF, Math.Max(0, Math.Min(1, (x - r.X) / (double)r.Width))); }
        float Y(double db) { var r = Plot; return (float)(r.Y + r.Height * (0.5 - Math.Max(-Range - 2, Math.Min(Range + 2, db)) / (2 * Range))); }
        double DbAt(float y) { var r = Plot; return (0.5 - (y - r.Y) / (double)r.Height) * 2 * Range; }
        internal PointF Knob(int i) { var b = P.Bands[i]; return new PointF(X(b.Freq), Y((b.HasGain ? b.Gain : 0) + P.Preamp)); } // on the line, which the pre-gain moves

        int Hit(Point m)
        {
            int best = -1; float bd = Theme.S(15) * Theme.S(15);
            for (int i = 0; i < P.Bands.Count; i++) { var k = Knob(i); float d = (k.X - m.X) * (k.X - m.X) + (k.Y - m.Y) * (k.Y - m.Y); if (d < bd) { bd = d; best = i; } }
            return best;
        }

        // a change by the mouse (a drag, a dial, the wheel) is painted at once: Windows paints a window only when no other
        // message waits, and a fast mouse sends its moves without a pause — the picture stood still while it was dragged
        // and moved again only when the mouse stopped (20.11, the user: «двигаешь и даже не видишь, куда двигаешь»)
        void Fire() { Invalidate(); var h = Changed; if (h != null) h(this, EventArgs.Empty); Update(); }

        internal static string Hz(double f) { return f >= 1000 ? (f / 1000).ToString(f >= 10000 ? "0.#" : "0.##", CultureInfo.InvariantCulture) + " кГц" : f.ToString("0", CultureInfo.InvariantCulture) + " Гц"; }
        static string Gain(double g) { return (g > 0.04 ? "+" : g < -0.04 ? "−" : "") + Math.Abs(g).ToString("0.0", CultureInfo.InvariantCulture) + " дБ"; }

        // the fader follows the mouse; a dial turns with the mouse going up and down from where it was taken
        void DragStrip(int y)
        {
            if (dragPart == Part.Master)
            {
                // from where it was taken, by the mouse's travel (20.11: the knob no longer jumps to the press)
                double pdb = Math.Round((dragFrom + (dragY - y) * 2.0 * MasterDb / Math.Max(1, FaderBottom - FaderTop)) * 2) / 2;
                if (Math.Abs(pdb) < 0.35) pdb = 0;
                P.Preamp = (float)Math.Max(-MasterDb, Math.Min(MasterDb, pdb));
                Fire(); return;
            }
            if (dragBand < 0 || dragBand >= P.Bands.Count) return;
            var b = P.Bands[dragBand];
            if (dragPart == Part.Fader)
            {
                if (!b.HasGain) return;
                double gdb = Math.Round((dragFrom + (dragY - y) * 2.0 * Range / Math.Max(1, FaderBottom - FaderTop)) * 10) / 10; // from where it was taken
                if (Math.Abs(gdb) < 0.35) gdb = 0;
                b.Gain = (float)Math.Max(-Range, Math.Min(Range, gdb));
            }
            else
            {
                double turn = (dragY - y) / (double)Theme.S(220); // the whole range in 220 units of travel
                if (dragPart == Part.Freq) b.Freq = (float)Math.Max(MinF, Math.Min(MaxF, RoundHz(dragFrom * Math.Pow(MaxF / MinF, turn))));
                else b.Q = (float)Math.Max(0.2, Math.Min(12, Math.Round(dragFrom * Math.Pow(60, turn) * 100) / 100));
            }
            Fire();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            armed = true;
            int band = -1; var part = Dim ? Part.None : HitPanel(e.Location, out band);
            if (part != Part.None)
            {
                if (part == Part.Add) { if (e.Button == MouseButtons.Left) AddBand(); }
                else if (part == Part.Master)
                {
                    // the pointer onto the knob, the knob stays (20.11)
                    if (e.Button == MouseButtons.Left) { dragPart = part; dragBand = -1; dragFrom = P.Preamp; int ky = (int)Math.Round(MasterY(P.Preamp)); dragY = Theme.Warp(this, MasterX, ky) ? ky : e.Y; Invalidate(); }
                }
                else if (e.Button == MouseButtons.Right || part == Part.Kind) Menu(band, e.Location);
                else if (e.Button == MouseButtons.Left)
                {
                    dragPart = part; dragBand = band; dragY = e.Y; dragFrom = part == Part.Freq ? P.Bands[band].Freq : P.Bands[band].Q; hot = band;
                    // a fader: the pointer goes onto its knob and the knob stays where it is; the drag starts from there (20.11)
                    if (part == Part.Fader && P.Bands[band].HasGain)
                    {
                        dragFrom = P.Bands[band].Gain; int ky = (int)Math.Round(FaderY(dragFrom));
                        if (Theme.Warp(this, StripX(Array.IndexOf(Order(), band)), ky)) dragY = ky;
                    }
                    Invalidate();
                }
                base.OnMouseDown(e); return;
            }
            int h = Hit(e.Location);
            if (e.Button == MouseButtons.Left) drag = Dim ? -1 : h;
            else if (e.Button == MouseButtons.Right && !Dim) Menu(h, e.Location);
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e) { drag = -1; dragPart = Part.None; dragBand = -1; Invalidate(); base.OnMouseUp(e); }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (dragPart != Part.None) { DragStrip(e.Y); base.OnMouseMove(e); return; }
            if (drag >= 0 && drag < P.Bands.Count)
            {
                var b = P.Bands[drag];
                b.Freq = (float)Math.Max(MinF, Math.Min(MaxF, RoundHz(FreqAt(e.X))));
                if (b.HasGain) { double gdb = Math.Round((DbAt(e.Y) - P.Preamp) * 10) / 10; if (Math.Abs(gdb) < 0.35) gdb = 0; b.Gain = (float)Math.Max(-Range, Math.Min(Range, gdb)); }
                Fire();
            }
            int band = -1; var part = drag >= 0 || Dim ? Part.None : HitPanel(e.Location, out band);
            int ht = drag >= 0 ? drag : Dim ? -1 : part != Part.None ? band : Hit(e.Location);
            Cursor = part == Part.Kind || part == Part.Add ? Cursors.Hand : part != Part.None ? Cursors.SizeNS : ht >= 0 ? Cursors.SizeAll : Cursors.Default;
            if (ht != hot || part != hotPart) { hot = ht; hotPart = part; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            armed = false;
            if (drag < 0 && dragPart == Part.None && (hot >= 0 || hotPart != Part.None)) { hot = -1; hotPart = Part.None; Invalidate(); }
            base.OnMouseLeave(e);
        }

        // the wheel works only over a point or a strip and after a click on the graph; otherwise the page scrolls.
        // Over a point or a width dial it changes the width, over a fader the gain by half a dB, over a frequency dial the frequency
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            bool master = armed && !Dim && hotPart == Part.Master;
            if (!master && (!armed || hot < 0 || hot >= P.Bands.Count || Dim || hotPart == Part.Kind || hotPart == Part.Add)) { base.OnMouseWheel(e); return; }
            var he = e as HandledMouseEventArgs; if (he != null) he.Handled = true;
            // 20.11: one notch = one step, also from a smooth wheel that sends a notch in parts (Theme.WheelNotches)
            int n = Theme.WheelNotches(ref wheelAcc, e.Delta);
            for (int i = 0; i < Math.Abs(n); i++)
            {
                bool up = n > 0;
                if (master) { P.Preamp = (float)Math.Max(-MasterDb, Math.Min(MasterDb, Math.Round((P.Preamp + (up ? 0.5 : -0.5)) * 2) / 2)); continue; }
                var b = P.Bands[hot];
                if (hotPart == Part.Fader) { if (b.HasGain) b.Gain = (float)Math.Max(-Range, Math.Min(Range, Math.Round((b.Gain + (up ? 0.5 : -0.5)) * 2) / 2)); }
                else if (hotPart == Part.Freq) b.Freq = (float)Math.Max(MinF, Math.Min(MaxF, RoundHz(b.Freq * (up ? 1.06 : 1 / 1.06))));
                else b.Q = (float)Math.Max(0.2, Math.Min(12, Math.Round(b.Q * (up ? 1.15 : 1 / 1.15) * 100) / 100));
            }
            if (n != 0) Fire();
        }
        int wheelAcc;

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            int band = -1; var part = Dim ? Part.None : HitPanel(e.Location, out band);
            if (e.Button == MouseButtons.Left && part == Part.Master) { dragPart = Part.None; P.Preamp = 0; Fire(); } // a double click: back to no gain
            else if (e.Button == MouseButtons.Left && band >= 0 && band < P.Bands.Count)
            {
                dragPart = Part.None; dragBand = -1; // the second press of the pair took hold of the strip again
                // a double click on a fader puts the gain to zero, on the width dial the width to 1
                if (part == Part.Fader && P.Bands[band].HasGain) { P.Bands[band].Gain = 0; Fire(); }
                else if (part == Part.Wide) { P.Bands[band].Q = 1f; Fire(); }
            }
            else if (e.Button == MouseButtons.Left && !Dim && part == Part.None && Hit(e.Location) < 0 && Plot.Contains(e.Location)) AddAt(e.Location);
            base.OnMouseDoubleClick(e);
        }

        void AddAt(Point at)
        {
            if (P.Bands.Count >= SoundParams.MaxBands) return;
            double gdb = Math.Round((DbAt(at.Y) - P.Preamp) * 2) / 2;
            P.Bands.Add(new EqBand { Type = 1, Freq = (float)Math.Round(FreqAt(at.X)), Gain = (float)Math.Max(-Range, Math.Min(Range, gdb)), Q = 1f });
            hot = P.Bands.Count - 1;
            Fire();
        }

        void Menu(int band, Point at)
        {
            var m = Page.Menu();
            if (band >= 0)
            {
                var b = P.Bands[band];
                for (int t = 1; t < Kinds.Length; t++)
                {
                    int type = t;
                    var it = new ToolStripMenuItem(Kinds[t]) { Checked = b.Type == t };
                    it.Click += (s, e) => { b.Type = type; if (!b.HasGain) b.Gain = 0; if (type >= 4 && b.Q > 3 && type != 6) b.Q = 0.7f; Fire(); };
                    m.Items.Add(it);
                }
                m.Items.Add(new ToolStripSeparator());
                var zero = new ToolStripMenuItem("Усиление в ноль"); zero.Click += (s, e) => { b.Gain = 0; Fire(); }; zero.Enabled = b.HasGain; m.Items.Add(zero);
                var del = new ToolStripMenuItem("Удалить точку"); del.Click += (s, e) => { P.Bands.Remove(b); hot = -1; Fire(); }; m.Items.Add(del);
            }
            else
            {
                var add = new ToolStripMenuItem("Добавить точку"); add.Enabled = P.Bands.Count < SoundParams.MaxBands && Plot.Contains(at); add.Click += (s, e) => AddAt(at); m.Items.Add(add);
                var flat = new ToolStripMenuItem("Все усиления в ноль"); flat.Click += (s, e) => { foreach (var b in P.Bands) b.Gain = 0; Fire(); }; m.Items.Add(flat);
            }
            m.Show(this, at);
        }

        // the spectrum of what plays (dB per bin of AudioFx.Spectrum), null = nothing to show; drawn rising at once and
        // falling slowly, per pixel column
        public void Feed(double[] db)
        {
            if (db == null) { if (specOn) { specOn = false; Invalidate(); } return; }
            var r = Plot; int n = Math.Max(1, r.Width);
            if (spec == null || spec.Length != n) { spec = new float[n]; for (int i = 0; i < n; i++) spec[i] = -120; }
            double binHz = Rate / AudioFx.SpectrumSize;
            for (int px = 0; px < n; px++)
            {
                double f0 = FreqAt(r.X + px - 0.5f), f1 = FreqAt(r.X + px + 0.5f);
                int b0 = (int)Math.Floor(f0 / binHz), b1 = (int)Math.Ceiling(f1 / binHz);
                double v;
                if (b1 - b0 <= 1)
                {
                    // low frequencies: fewer bins than pixels, a straight line between the two nearest
                    double pos = (f0 + f1) / 2 / binHz; int a = Math.Max(1, Math.Min(db.Length - 2, (int)Math.Floor(pos))); double t = Math.Max(0, Math.Min(1, pos - a));
                    v = db[a] + (db[a + 1] - db[a]) * t;
                }
                else { v = -200; for (int b = Math.Max(1, b0); b <= b1 && b < db.Length; b++) if (db[b] > v) v = db[b]; }
                // tilted by 3 dB per octave around 1 kHz, as analyzers show music: flat to the eye when it sounds balanced
                v += 3 * Math.Log((f0 + f1) / 2 / 1000, 2);
                float now = (float)v;
                spec[px] = now > spec[px] ? spec[px] + (now - spec[px]) * 0.6f : spec[px] + (now - spec[px]) * 0.12f;
            }
            specOn = true;
            // the whole picture every moment, as it always was: 20.11 the user tried the plot alone with its still parts
            // kept as pictures and the point felt late under the mouse, and wanted the equalizer back as it was (the
            // compressor keeps its cached curve)
            Invalidate();
        }

        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }

        void SpectrumFill(ILive cv, Rectangle r)
        {
            if (!specOn || spec == null || spec.Length != r.Width) return;
            var pts = new List<PointF> { new PointF(r.X, r.Bottom) };
            for (int px = 0; px < spec.Length; px += 2) pts.Add(new PointF(r.X + px, r.Bottom - r.Height * Math.Max(0f, Math.Min(1f, (spec[px] + 84f) / 84f))));
            pts.Add(new PointF(r.Right, r.Bottom));
            cv.FillPoly(Color.FromArgb(34, Theme.Text), pts.ToArray());
        }

        // the dashed curve of the band under the mouse, the whole curve with its fill, and the points
        void CurveAndKnobs(Graphics g, Rectangle r, Color line)
        {
            var clip = g.Clip; g.SetClip(r);
            int steps = Math.Max(40, r.Width / 2);
            if (hot >= 0 && hot < P.Bands.Count)
            {
                var one = new PointF[steps + 1];
                for (int i = 0; i <= steps; i++) { double f = MinF * Math.Pow(MaxF / MinF, i / (double)steps); one[i] = new PointF(X(f), Y(AudioFx.Response(P, f, Rate, hot) + P.Preamp)); }
                using (var pen = new Pen(Color.FromArgb(120, Theme.Text2), Theme.S(1.2f)) { DashStyle = DashStyle.Dash }) g.DrawLines(pen, one);
            }
            var curve = new PointF[steps + 1];
            for (int i = 0; i <= steps; i++) { double f = MinF * Math.Pow(MaxF / MinF, i / (double)steps); curve[i] = new PointF(X(f), Y(AudioFx.Response(P, f, Rate))); } // with the pre-gain: the whole line goes up or down by it (20.11)
            using (var path = new GraphicsPath())
            {
                path.AddLines(curve);
                path.AddLine(curve[steps], new PointF(curve[steps].X, Y(0))); path.AddLine(new PointF(curve[steps].X, Y(0)), new PointF(curve[0].X, Y(0))); path.CloseFigure();
                using (var b = new SolidBrush(Color.FromArgb(Dim ? 18 : 36, line))) g.FillPath(b, path);
            }
            using (var pen = new Pen(line, Theme.S(2)) { LineJoin = LineJoin.Round }) g.DrawLines(pen, curve);
            g.Clip = clip;
            for (int i = 0; i < P.Bands.Count; i++)
            {
                var k = Knob(i); bool h = i == hot;
                float size = Theme.S(h ? 19 : 16);
                int a = 0, b = curve.Length - 1;
                while (a + 1 < curve.Length && curve[a + 1].X < k.X - size) a++;
                while (b - 1 >= 0 && curve[b - 1].X > k.X + size) b--;
                var piece = new PointF[Math.Max(0, b - a + 1)]; if (piece.Length > 0) Array.Copy(curve, a, piece, 0, piece.Length);
                Theme.GlassKnob(g, k.X, k.Y, size, 1f, h ? Theme.AccentHover : line, Theme.S(4.5f), Dim, 180f, null, -1f, piece);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var card = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            Theme.Fill(g, card, Theme.S(8), Theme.Card);
            Theme.Stroke(g, card, Theme.S(8), Theme.Border);
            var r = Plot;
            if (r.Width < 20 || r.Height < 20) return;
            FitRange();
            Color line = Dim ? Theme.Muted : Theme.Accent;

            using (var pen = new Pen(Theme.Border))
            using (var zeroPen = new Pen(Theme.BorderStrong))
            {
                for (int db = -(int)Range; db <= Range; db += 6)
                {
                    float y = Y(db);
                    g.DrawLine(db == 0 ? zeroPen : pen, r.X, y, r.Right, y);
                    Theme.Text1(g, (db > 0 ? "+" : db < 0 ? "−" : "") + Math.Abs(db), Theme.Small, new Rectangle(0, (int)y - Theme.S(10), r.X - Theme.S(8), Theme.S(20)), Theme.Muted, TextFormatFlags.Right);
                }
                foreach (var f in new[] { 20, 50, 100, 200, 500, 1000, 2000, 5000, 10000, 20000 })
                {
                    float x = X(f);
                    g.DrawLine(pen, x, r.Y, x, r.Bottom);
                    Theme.Text1(g, f >= 1000 ? f / 1000 + "k" : f.ToString(), Theme.Small, new Rectangle((int)x - Theme.S(30), r.Bottom + Theme.S(4), Theme.S(60), Theme.S(18)), Theme.Muted, TextFormatFlags.HorizontalCenter);
                }
            }

            // what plays now (-84 dB at the bottom of the plot, 0 dB at its top), then the curve and its points
            var clip = g.Clip; g.SetClip(r);
            SpectrumFill(new GdiLive(g), r);
            g.Clip = clip;
            CurveAndKnobs(g, r, line);

            DrawStrips(g, line);

            string hint;
            if (!Dim && (hotPart == Part.Master || dragPart == Part.Master))
                hint = "Общее усиление" + ": " + Gain(P.Preamp) + " · " + L.T("До эквалайзера: убавьте, если полосы подняты высоко и лимитер срабатывает постоянно") + " · " + "тяните вверх-вниз или крутите колесо";
            else if (hot >= 0 && hot < P.Bands.Count)
            {
                var b = P.Bands[hot];
                hint = Kinds[Math.Max(1, Math.Min(6, b.Type))] + " · " + Hz(b.Freq) + (b.HasGain ? " · " + Gain(b.Gain) : "") + " · " + "ширина Q " + b.Q.ToString("0.##", CultureInfo.InvariantCulture) + " · " + "колесо меняет ширину, правая кнопка — вид фильтра";
            }
            else if (Dim) hint = "Эквалайзер выключен";
            else hint = "Тяните точки. Двойной щелчок по пустому месту добавляет точку, правая кнопка на точке — вид фильтра или удаление";
            Theme.Text1(g, hint, Theme.Small, new Rectangle(r.X, r.Bottom + Theme.S(28), Width - r.X - Theme.S(14), Theme.S(22)), Theme.Text2);
        }

        // the kind of a filter as a small picture of its curve, in a box w x h around (cx, cy)
        static void KindIcon(Graphics g, int type, float cx, float cy, float w, float h, Color c)
        {
            float[][] shape;
            switch (type)
            {
                case 2: shape = new[] { new[] { 0f, 0.15f }, new[] { 0.3f, 0.15f }, new[] { 0.7f, 0.85f }, new[] { 1f, 0.85f } }; break;                     // low shelf
                case 3: shape = new[] { new[] { 0f, 0.85f }, new[] { 0.3f, 0.85f }, new[] { 0.7f, 0.15f }, new[] { 1f, 0.15f } }; break;                     // high shelf
                case 4: shape = new[] { new[] { 0f, 1f }, new[] { 0.45f, 0.2f }, new[] { 1f, 0.2f } }; break;                                                   // lows cut
                case 5: shape = new[] { new[] { 0f, 0.2f }, new[] { 0.55f, 0.2f }, new[] { 1f, 1f } }; break;                                                   // highs cut
                case 6: shape = new[] { new[] { 0f, 0.2f }, new[] { 0.36f, 0.2f }, new[] { 0.5f, 1f }, new[] { 0.64f, 0.2f }, new[] { 1f, 0.2f } }; break;   // notch
                default: shape = new[] { new[] { 0f, 0.9f }, new[] { 0.25f, 0.85f }, new[] { 0.5f, 0.1f }, new[] { 0.75f, 0.85f }, new[] { 1f, 0.9f } }; break; // bell
            }
            var pts = shape.Select(p => new PointF(cx - w / 2 + p[0] * w, cy - h / 2 + p[1] * h)).ToArray();
            using (var pen = new Pen(c, Theme.S(1.6f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                if (type >= 4) g.DrawLines(pen, pts); else g.DrawCurve(pen, pts, 0.35f);
        }

        // a dial: a ring three quarters round, lit as far as the value goes (0..1), and a pointer
        internal static void Dial(Graphics g, float cx, float cy, float frac, Color lit, bool hover, bool dim, float radius = 8.5f)
        {
            float rad = Theme.S(radius), pw = Theme.S(radius > 10 ? 3.2f : 2.4f);
            frac = Math.Max(0f, Math.Min(1f, frac));
            var box = new RectangleF(cx - rad, cy - rad, rad * 2, rad * 2);
            using (var back = new Pen(Theme.Border, pw) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawArc(back, box, 135, 270);
            if (frac > 0.005f) using (var on = new Pen(lit, pw) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawArc(on, box, 135, 270 * frac);
            double a = (135 + 270 * frac) * Math.PI / 180;
            float inner = rad - pw - Theme.S(1.5f);
            using (var b = new SolidBrush(hover ? Theme.Raised : Theme.Window)) g.FillEllipse(b, cx - inner, cy - inner, inner * 2, inner * 2);
            using (var pen = new Pen(dim ? Theme.Muted : hover ? Theme.Text : Theme.Text2, Theme.S(1.6f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawLine(pen, cx + (float)Math.Cos(a) * inner * 0.25f, cy + (float)Math.Sin(a) * inner * 0.25f, cx + (float)Math.Cos(a) * inner * 0.95f, cy + (float)Math.Sin(a) * inner * 0.95f);
        }

        // the pre-gain's strip: on top a flat line with arrows up and down (the whole curve moves), the fader, the value
        // under it, «дБ» in the row of the widths; a thin line parts it from the bands' strips
        void DrawMaster(Graphics g, Color line, int top, int bottom, int sw, float th)
        {
            var p = Panel; float cx = MasterX, mid = (top + bottom) / 2f;
            bool h = !Dim && (hotPart == Part.Master || dragPart == Part.Master);
            if (h) Theme.Fill(g, new RectangleF(cx - sw / 2f + 1, p.Y - Theme.S(4), sw - 2, p.Height + Theme.S(8)), Theme.S(6), Color.FromArgb(46, Theme.Text));
            var ic = Dim ? Theme.Muted : h ? Theme.Text : Theme.Text2;
            using (var pen = new Pen(ic, Theme.S(1.6f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            {
                float iy = p.Y + Theme.S(9), w = Theme.S(15);
                g.DrawLine(pen, cx - w / 2, iy, cx - Theme.S(3.5f), iy); g.DrawLine(pen, cx + Theme.S(3.5f), iy, cx + w / 2, iy);
                float a = Theme.S(2.5f), up = iy - Theme.S(4.5f), dn = iy + Theme.S(4.5f);
                g.DrawLine(pen, cx, up, cx, dn);
                g.DrawLines(pen, new[] { new PointF(cx - a, up + a), new PointF(cx, up), new PointF(cx + a, up + a) });
                g.DrawLines(pen, new[] { new PointF(cx - a, dn - a), new PointF(cx, dn), new PointF(cx + a, dn - a) });
            }
            Theme.Tube(g, new RectangleF(cx - th / 2f, top, th, bottom - top));
            using (var tick = new Pen(Theme.BorderStrong)) g.DrawLine(tick, cx - Theme.S(6), mid, cx + Theme.S(6), mid);
            float y = MasterY(P.Preamp);
            if (Math.Abs(y - mid) > 1f) Theme.Fill(g, new RectangleF(cx - th / 2f, Math.Min(y, mid), th, Math.Abs(y - mid)), th / 2f, Dim ? Theme.Muted : line);
            Theme.GlassKnob(g, cx, y, Theme.S(h ? 16 : 14), Dim ? 0f : 1f, h ? Theme.AccentHover : line, th, Dim, y <= mid ? 90f : 270f, null, Dim ? 0f : Math.Abs(y - mid));
            string v = P.Preamp == 0 ? "0" : (P.Preamp > 0 ? "+" : "−") + Math.Abs(P.Preamp).ToString("0.#", CultureInfo.InvariantCulture);
            Theme.Text1(g, v, Theme.Small, new Rectangle((int)cx - Theme.S(20), FreqY - Theme.S(10), Theme.S(40), Theme.S(20)), Dim ? Theme.Muted : h ? Theme.Text : Theme.Text2, TextFormatFlags.HorizontalCenter);
            Theme.Text1(g, "дБ", Theme.Small, new Rectangle((int)cx - Theme.S(20), WideY - Theme.S(10), Theme.S(40), Theme.S(20)), Theme.Muted, TextFormatFlags.HorizontalCenter);
            using (var sep = new Pen(Theme.Border)) { float sx = p.X + Theme.S(StripW) + Theme.S(3); g.DrawLine(sep, sx, p.Y, sx, p.Bottom); }
        }

        void DrawStrips(Graphics g, Color line)
        {
            var p = Panel; if (p.Width <= 0) return;
            var ord = Order();
            int top = FaderTop, bottom = FaderBottom, sw = Theme.S(StripW);
            float th = Theme.S(5), mid = (top + bottom) / 2f;
            // what the two rows of dials are
            Theme.Text1(g, "Гц", Theme.Small, new Rectangle(p.X + Theme.S(MasterW) - Theme.S(4), FreqY - Theme.S(10), Theme.S(Gutter), Theme.S(20)), Theme.Muted, TextFormatFlags.Right);
            Theme.Text1(g, "Q", Theme.Small, new Rectangle(p.X + Theme.S(MasterW) - Theme.S(4), WideY - Theme.S(10), Theme.S(Gutter), Theme.S(20)), Theme.Muted, TextFormatFlags.Right);
            DrawMaster(g, line, top, bottom, sw, th);
            using (var tick = new Pen(Theme.BorderStrong))
                for (int k = 0; k < ord.Length; k++)
                {
                    var b = P.Bands[ord[k]]; bool h = ord[k] == hot; float cx = StripX(k);
                    // the strip of the band under the mouse (here or on the plot) stands out
                    if (h) Theme.Fill(g, new RectangleF(cx - sw / 2f + 1, p.Y - Theme.S(4), sw - 2, p.Height + Theme.S(8)), Theme.S(6), Color.FromArgb(Dim ? 20 : 46, Theme.Text));
                    KindIcon(g, b.Type, cx, p.Y + Theme.S(9), Theme.S(15), Theme.S(9), Dim ? Theme.Muted : h && hotPart == Part.Kind ? Theme.Text : Theme.Text2);
                    // the fader: a tube, a mark at no gain, the bar from that mark to the knob
                    Theme.Tube(g, new RectangleF(cx - th / 2f, top, th, bottom - top));
                    g.DrawLine(tick, cx - Theme.S(6), mid, cx + Theme.S(6), mid);
                    bool gain = b.HasGain && !Dim;
                    float y = b.HasGain ? FaderY(b.Gain) : mid;
                    if (b.HasGain && Math.Abs(y - mid) > 1f) Theme.Fill(g, new RectangleF(cx - th / 2f, Math.Min(y, mid), th, Math.Abs(y - mid)), th / 2f, Dim ? Theme.Muted : line);
                    Theme.GlassKnob(g, cx, y, Theme.S(h && (hotPart == Part.Fader || dragPart == Part.Fader) ? 16 : 14), gain ? 1f : 0f, h ? Theme.AccentHover : line, th, !gain, y <= mid ? 90f : 270f, null, gain ? Math.Abs(y - mid) : 0f);
                    Dial(g, cx, FreqY, (float)(Math.Log(b.Freq / MinF) / Math.Log(MaxF / MinF)), Dim ? Theme.Muted : h ? Theme.AccentHover : line, h && (hotPart == Part.Freq || dragPart == Part.Freq), Dim);
                    Dial(g, cx, WideY, (float)(Math.Log(Math.Max(0.2, b.Q) / 0.2) / Math.Log(60)), Dim ? Theme.Muted : h ? Theme.AccentHover : line, h && (hotPart == Part.Wide || dragPart == Part.Wide), Dim);
                }
            if (ord.Length < SoundParams.MaxBands)
            {
                // «+»: one more band
                float cx = StripX(ord.Length), s = Theme.S(18); bool h = hotPart == Part.Add && !Dim;
                var box = new RectangleF(cx - s / 2, mid - s / 2, s, s);
                if (h) Theme.Fill(g, box, Theme.S(5), Theme.Raised);
                Theme.Stroke(g, box, Theme.S(5), h ? Theme.BorderStrong : Theme.Border);
                using (var pen = new Pen(Dim ? Theme.Muted : h ? Theme.Text : Theme.Text2, Theme.S(1.6f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    g.DrawLine(pen, cx - Theme.S(4), mid, cx + Theme.S(4), mid); g.DrawLine(pen, cx, mid - Theme.S(4), cx, mid + Theme.S(4));
                }
            }
        }
    }
}
