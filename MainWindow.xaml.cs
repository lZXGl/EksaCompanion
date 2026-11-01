using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace EksaCompanion
{
    public partial class MainWindow : Window
    {
        // Battery calibration: drops from 100% to 20% in 5 hours (16% per hour, 6.25h total capacity)
        private const double BatteryDrainPercentPerHour = 16.0;
        private const double AudioGraceWindowSeconds = 120.0;

        private readonly EksaHardware _hardware;
        private readonly AppSettings _settings;
        private NotifyIcon _notifyIcon;
        private DispatcherTimer _batteryTimer;
        private bool _isRealExit = false;
        private Slider[] _sliders;
        private TextBlock[] _gainLabels;
        private bool _isUpdatingUi = false;
        private DateTime _suppressSliderEventsUntil = DateTime.MinValue;
        private DateTime _lastAudioDetectedUtc = DateTime.MinValue;
        private DateTime _lastBatteryTickUtc = DateTime.UtcNow;
        private bool _isAudioActive = false;
        private int _ticksSinceSave = 0;

        public MainWindow()
        {
            App.Log("MainWindow ctor started");
            _isUpdatingUi = true;
            try
            {
                InitializeComponent();
                App.Log("InitializeComponent completed");
            }
            catch (Exception ex)
            {
                App.Log("InitializeComponent FATAL EXCEPTION: " + ex.ToString());
                throw;
            }

            // If Windows volume was left at 1% from the old idle timer, restore it to 50%
            try
            {
                float curVol = WindowsAudio.GetMasterVolume();
                if (curVol > 0.0f && curVol <= 0.02f)
                {
                    WindowsAudio.SetMasterVolume(0.50f);
                }
            }
            catch { }

            _settings = AppSettings.Load();
            App.Log("Settings loaded");

            _hardware = new EksaHardware();
            _hardware.StatusUpdated += Hardware_StatusUpdated;
            App.Log("Hardware initialized");

            InitControlsArray();
            InitNotifyIcon();
            ApplySettingsToUi();

            _lastBatteryTickUtc = DateTime.UtcNow;
            if (_hardware.IsConnected && WindowsAudio.GetMasterPeakAudio() > 0.0005f)
            {
                _lastAudioDetectedUtc = DateTime.UtcNow;
                _isAudioActive = true;
            }
            UpdateBatteryUi();

            _batteryTimer = new DispatcherTimer();
            _batteryTimer.Interval = TimeSpan.FromSeconds(2);
            _batteryTimer.Tick += delegate(object s, EventArgs e)
            {
                DateTime nowUtc = DateTime.UtcNow;
                double dt = Math.Max(0.0, Math.Min(10.0, (nowUtc - _lastBatteryTickUtc).TotalSeconds));
                _lastBatteryTickUtc = nowUtc;

                if (_hardware.IsConnected)
                {
                    float peak = WindowsAudio.GetMasterPeakAudio();
                    if (peak > 0.0005f)
                    {
                        _lastAudioDetectedUtc = nowUtc;
                    }

                    bool withinGrace = (_lastAudioDetectedUtc != DateTime.MinValue) &&
                                       ((nowUtc - _lastAudioDetectedUtc).TotalSeconds <= AudioGraceWindowSeconds);
                    _isAudioActive = withinGrace;

                    if (_isAudioActive)
                    {
                        _settings.ActiveUsedSeconds += dt;
                        _settings.LastActiveTime = DateTime.Now;
                        _ticksSinceSave++;
                        if (_ticksSinceSave >= 15)
                        {
                            _ticksSinceSave = 0;
                            _settings.Save();
                        }
                    }
                }
                else
                {
                    _isAudioActive = false;
                }
                UpdateBatteryUi();
            };
            _batteryTimer.Start();

            // Initial hardware sync
            _hardware.SetMicEnc(_settings.MicEncEnabled);
            _hardware.SetAudioMode(_settings.SurroundEnabled);
            _hardware.SetEqualizer(_settings.EqBands, _settings.SelectedPreset);
            this.Closing += delegate(object s, System.ComponentModel.CancelEventArgs e) {
                App.Log("MainWindow Closing event fired! Cancel=" + !_isRealExit);
            };
            this.Closed += delegate(object s, EventArgs e) {
                App.Log("MainWindow Closed event fired!");
            };
            App.Log("Initial hardware sync completed");
        }

        private void InitControlsArray()
        {
            _sliders = new Slider[] {
                Slider0, Slider1, Slider2, Slider3, Slider4,
                Slider5, Slider6, Slider7, Slider8, Slider9
            };

            _gainLabels = new TextBlock[] {
                TxtGain0, TxtGain1, TxtGain2, TxtGain3, TxtGain4,
                TxtGain5, TxtGain6, TxtGain7, TxtGain8, TxtGain9
            };
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        private System.Drawing.Icon CreateNativeTrayIcon()
        {
            System.Drawing.Size sz = SystemInformation.SmallIconSize;
            int w = sz.Width > 0 ? sz.Width : 16;
            int h = sz.Height > 0 ? sz.Height : 16;

            using (Bitmap bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(System.Drawing.Color.Transparent);
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

                float fontSize = w * 0.96f;
                using (Font font = new Font("Segoe Fluent Icons", fontSize, System.Drawing.FontStyle.Regular, GraphicsUnit.Pixel))
                using (StringFormat sf = (StringFormat)StringFormat.GenericTypographic.Clone())
                {
                    sf.Alignment = StringAlignment.Center;
                    sf.LineAlignment = StringAlignment.Center;
                    // \uE95B = Windows 11 Native Headset Glyph
                    g.DrawString("\uE95B", font, System.Drawing.Brushes.White, new RectangleF(w * 0.53f, h * 0.53f, 0f, 0f), sf);
                }

                IntPtr hIcon = bmp.GetHicon();
                try
                {
                    return (System.Drawing.Icon)System.Drawing.Icon.FromHandle(hIcon).Clone();
                }
                finally
                {
                    DestroyIcon(hIcon);
                }
            }
        }

        private void InitNotifyIcon()
        {
            _notifyIcon = new NotifyIcon();
            try
            {
                _notifyIcon.Icon = CreateNativeTrayIcon();
            }
            catch
            {
                try
                {
                    string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.ico");
                    if (File.Exists(iconPath))
                        _notifyIcon.Icon = new Icon(iconPath);
                    else
                        _notifyIcon.Icon = SystemIcons.Application;
                }
                catch
                {
                    _notifyIcon.Icon = SystemIcons.Application;
                }
            }

            _notifyIcon.Visible = true;
            _notifyIcon.Text = "EKSA E910 Companion";
            _notifyIcon.DoubleClick += delegate(object s, EventArgs e) { ToggleWindow(); };
            _notifyIcon.Click += delegate(object s, EventArgs e) {
                System.Windows.Forms.MouseEventArgs me = e as System.Windows.Forms.MouseEventArgs;
                if (me != null && me.Button == MouseButtons.Left)
                    ToggleWindow();
            };

            ContextMenuStrip contextMenu = new ContextMenuStrip();
            ToolStripMenuItem itemOpen = new ToolStripMenuItem("Open Dashboard", null, delegate(object s, EventArgs e) { ShowWindow(); });
            itemOpen.Font = new System.Drawing.Font(itemOpen.Font, System.Drawing.FontStyle.Bold);
            contextMenu.Items.Add(itemOpen);
            contextMenu.Items.Add(new ToolStripSeparator());

            // Preset Submenu
            ToolStripMenuItem menuPresets = new ToolStripMenuItem("EQ Presets");
            foreach (string preset in Presets.All.Keys)
            {
                string pName = preset;
                menuPresets.DropDownItems.Add(new ToolStripMenuItem(pName, null, delegate(object s, EventArgs e) { ApplyPreset(pName); }));
            }
            contextMenu.Items.Add(menuPresets);

            // Audio Mode
            ToolStripMenuItem itemSurround = new ToolStripMenuItem("7.1 Surround Sound", null, delegate(object s, EventArgs e) { ToggleSurround(); });
            contextMenu.Items.Add(itemSurround);

            // Mic ENC
            ToolStripMenuItem itemMicEnc = new ToolStripMenuItem("Mic ENC (Noise Cancel)", null, delegate(object s, EventArgs e) { ToggleMicEnc(); });
            contextMenu.Items.Add(itemMicEnc);

            // Battery Reset
            ToolStripMenuItem itemChargeReset = new ToolStripMenuItem("Mark Battery Charged (100%)", null, delegate(object s, EventArgs e) { ResetBatteryToFull(); });
            contextMenu.Items.Add(itemChargeReset);

            contextMenu.Items.Add(new ToolStripSeparator());
            contextMenu.Items.Add(new ToolStripMenuItem("Exit", null, delegate(object s, EventArgs e) { ExitApplication(); }));

            _notifyIcon.ContextMenuStrip = contextMenu;
        }

        private void AnimateSliderValue(Slider slider, double fromVal, double toVal)
        {
            _suppressSliderEventsUntil = DateTime.UtcNow.AddMilliseconds(230);
            slider.Value = toVal;
            if (Math.Abs(fromVal - toVal) < 0.05) return;

            DoubleAnimation anim = new DoubleAnimation(fromVal, toVal, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop
            };
            slider.BeginAnimation(RangeBase.ValueProperty, anim);
        }

        private int GetBatteryPercentage(out double elapsedHours)
        {
            elapsedHours = Math.Max(0.0, _settings.ActiveUsedSeconds / 3600.0);
            // 100% -> 20% in 5 hours of active sound = 16% per hour (375 minutes total to 0%)
            double pct = 100.0 - (elapsedHours * BatteryDrainPercentPerHour);
            if (pct < 0.0) pct = 0.0;
            if (pct > 100.0) pct = 100.0;
            return (int)Math.Round(pct);
        }

        private void UpdateBatteryUi()
        {
            double elapsedHours;
            int pct = GetBatteryPercentage(out elapsedHours);

            TxtBatteryPct.Text = pct + "%";

            // Battery fill width (0..15px inside 20px outer border)
            double targetWidth = Math.Max(1.5, Math.Round((pct / 100.0) * 15.0, 1));
            if (pct == 0) targetWidth = 0.0;
            double prevWidth = RectBatteryFill.Width;
            RectBatteryFill.Width = targetWidth;
            if (!double.IsNaN(prevWidth) && Math.Abs(prevWidth - targetWidth) > 0.2)
            {
                DoubleAnimation barAnim = new DoubleAnimation(prevWidth, targetWidth, TimeSpan.FromMilliseconds(220))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                    FillBehavior = FillBehavior.Stop
                };
                RectBatteryFill.BeginAnimation(FrameworkElement.WidthProperty, barAnim);
            }

            string accentHex;
            string textHex;
            string borderHex;
            string bgHex;

            if (pct > 40)
            {
                accentHex = "#C084FC";
                textHex = "#E9D5FF";
                borderHex = "#3B215D";
                bgHex = "#1A1228";
            }
            else if (pct > 20)
            {
                accentHex = "#FBBF24";
                textHex = "#FDE68A";
                borderHex = "#78350F";
                bgHex = "#271A0C";
            }
            else
            {
                accentHex = "#FB7185";
                textHex = "#FECDD3";
                borderHex = "#881337";
                bgHex = "#2E1418";
            }

            SolidColorBrush accentBrush = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(accentHex));
            BdBatteryIcon.BorderBrush = accentBrush;
            RectBatteryFill.Fill = accentBrush;
            RectBatteryTip.Fill = accentBrush;
            TxtBatteryPct.Foreground = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(textHex));
            BtnBatteryReset.BorderBrush = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(borderHex));
            BtnBatteryReset.Background = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(bgHex));

            int usedTotalMins = (int)Math.Round(elapsedHours * 60.0);
            int usedH = usedTotalMins / 60;
            int usedM = usedTotalMins % 60;

            double remHoursTotal = Math.Max(0.0, (100.0 / BatteryDrainPercentPerHour) - elapsedHours);
            int remTotalMins = (int)Math.Round(remHoursTotal * 60.0);
            int remH = remTotalMins / 60;
            int remM = remTotalMins % 60;

            if (_hardware != null && _hardware.IsConnected)
            {
                if (WindowsAudio.GetMasterPeakAudio() > 0.0005f)
                {
                    _lastAudioDetectedUtc = DateTime.UtcNow;
                    _isAudioActive = true;
                }

                if (_isAudioActive)
                {
                    TxtRuntimeSub.Text = string.Format("Active {0}h {1}m · ~{2}h {3}m left", usedH, usedM, remH, remM);
                }
                else
                {
                    TxtRuntimeSub.Text = string.Format("Paused (No Audio) · ~{0}h {1}m left", remH, remM);
                }

                if (_notifyIcon != null)
                {
                    _notifyIcon.Text = string.Format("EKSA E910: {0}%{1} | {2} | ENC {3}",
                        pct,
                        (_isAudioActive ? "" : " (Paused)"),
                        (_settings.SurroundEnabled ? "7.1" : "Stereo"),
                        (_settings.MicEncEnabled ? "On" : "Off"));
                }
            }
            else
            {
                TxtRuntimeSub.Text = "Please insert the 5.8GHz USB transmitter";
            }
        }

        private void ResetBatteryToFull()
        {
            _settings.LastChargedTime = DateTime.Now;
            _settings.LastActiveTime = DateTime.Now;
            _settings.ActiveUsedSeconds = 0.0;
            _settings.Save();
            UpdateBatteryUi();
            App.Log("Battery indicator reset to 100%");
        }

        private void BtnBatteryReset_Click(object sender, RoutedEventArgs e)
        {
            ResetBatteryToFull();
        }

        private void UpdateGainLabel(int bandIdx, sbyte gain)
        {
            _gainLabels[bandIdx].Text = (gain > 0 ? "+" + gain : gain.ToString()) + " dB";
            if (gain > 0)
                _gainLabels[bandIdx].Foreground = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#C084FC"));
            else if (gain < 0)
                _gainLabels[bandIdx].Foreground = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#7C3AED"));
            else
                _gainLabels[bandIdx].Foreground = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#64748B"));
        }

        private void ApplySettingsToUi()
        {
            _isUpdatingUi = true;

            ChkAutoStart.IsChecked = _settings.AutoStart;
            ChkStartMinimized.IsChecked = _settings.StartMinimized;

            UpdateMicEncUi(_settings.MicEncEnabled);
            UpdateSurroundUi(_settings.SurroundEnabled);

            for (int i = 0; i < 10; i++)
            {
                sbyte gain = _settings.EqBands[i];
                _sliders[i].Value = gain;
                UpdateGainLabel(i, gain);
            }

            HighlightPresetButton(_settings.SelectedPreset);
            UpdateDongleStatusUi(_hardware.IsConnected, null);
            UpdateBatteryUi();

            _isUpdatingUi = false;
        }

        private void UpdateDongleStatusUi(bool isConnected, string uid)
        {
            if (isConnected)
            {
                BadgeDongle.Background = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#231338"));
                BadgeDongle.BorderBrush = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#A855F7"));
                DotDongle.Fill = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#C084FC"));
                TxtDongleStatus.Text = "5.8GHz DONGLE ACTIVE";
                TxtDongleStatus.Foreground = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#E9D5FF"));
            }
            else
            {
                BadgeDongle.Background = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#2E1418"));
                BadgeDongle.BorderBrush = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#F43F5E"));
                DotDongle.Fill = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#FB7185"));
                TxtDongleStatus.Text = "DONGLE UNPLUGGED";
                TxtDongleStatus.Foreground = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#FECDD3"));

                _notifyIcon.Text = "EKSA E910: Dongle Unplugged";
            }
            UpdateBatteryUi();
        }

        private void Hardware_StatusUpdated(object sender, EksaStatusEventArgs e)
        {
            Dispatcher.Invoke(new Action(delegate()
            {
                try
                {
                    UpdateDongleStatusUi(e.IsConnected, e.DeviceUid);
                }
                catch (Exception ex)
                {
                    App.Log("Dispatcher.Invoke error: " + ex);
                }
            }));
        }

        private void UpdateMicEncUi(bool enabled)
        {
            if (enabled)
            {
                TxtMicEncState.Text = "ON";
                TxtMicEncState.Foreground = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#C084FC"));
                BtnMicEnc.BorderBrush = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#A855F7"));
                BtnMicEnc.Background = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#25123E"));
            }
            else
            {
                TxtMicEncState.Text = "OFF";
                TxtMicEncState.Foreground = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#64748B"));
                BtnMicEnc.BorderBrush = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#232B3C"));
                BtnMicEnc.Background = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#171D28"));
            }
        }

        private void UpdateSurroundUi(bool surround)
        {
            if (surround)
            {
                TxtSurroundState.Text = "7.1 SURROUND";
                TxtSurroundState.Foreground = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#E9D5FF"));
                BtnSurround.BorderBrush = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#A855F7"));
                BtnSurround.Background = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#25123E"));
            }
            else
            {
                TxtSurroundState.Text = "STEREO";
                TxtSurroundState.Foreground = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#C084FC"));
                BtnSurround.BorderBrush = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#A855F7"));
                BtnSurround.Background = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#25123E"));
            }
        }

        private void ToggleMicEnc()
        {
            _settings.MicEncEnabled = !_settings.MicEncEnabled;
            UpdateMicEncUi(_settings.MicEncEnabled);
            UpdateBatteryUi();
            _hardware.SetMicEnc(_settings.MicEncEnabled);
            _settings.Save();
            App.Log(string.Format("ToggleMicEnc: enabled={0}", _settings.MicEncEnabled));
        }

        private void ToggleSurround()
        {
            _settings.SurroundEnabled = !_settings.SurroundEnabled;
            UpdateSurroundUi(_settings.SurroundEnabled);

            // If on a mode-sensitive preset (e.g. Deep Bass or EKSA Standard), update curve for the active mode
            sbyte[] modeCurve = Presets.GetCurve(_settings.SelectedPreset, _settings.SurroundEnabled);
            if (modeCurve != null)
            {
                _isUpdatingUi = true;
                for (int i = 0; i < 10; i++)
                {
                    double prev = _sliders[i].Value;
                    _settings.EqBands[i] = modeCurve[i];
                    AnimateSliderValue(_sliders[i], prev, modeCurve[i]);
                    UpdateGainLabel(i, modeCurve[i]);
                }
                _isUpdatingUi = false;
            }

            UpdateBatteryUi();
            _hardware.SetAudioMode(_settings.SurroundEnabled);
            _hardware.SetEqualizer(_settings.EqBands, _settings.SelectedPreset);
            _settings.Save();
        }

        private void BtnMicEnc_Click(object sender, RoutedEventArgs e)
        {
            ToggleMicEnc();
        }

        private void BtnSurround_Click(object sender, RoutedEventArgs e)
        {
            ToggleSurround();
        }

        private void Slider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isUpdatingUi || DateTime.UtcNow < _suppressSliderEventsUntil || _settings == null || _hardware == null || _gainLabels == null) return;

            Slider slider = sender as Slider;
            int bandIdx;
            if (slider != null && slider.Tag != null && int.TryParse(slider.Tag.ToString(), out bandIdx))
            {
                sbyte gain = (sbyte)Math.Round(slider.Value);
                if (_settings.EqBands[bandIdx] == gain) return;
                _settings.EqBands[bandIdx] = gain;
                UpdateGainLabel(bandIdx, gain);

                _settings.SelectedPreset = "Custom";
                HighlightPresetButton("Custom");
                _hardware.SetEqualizer(_settings.EqBands, "Custom");
                _settings.Save();
            }
        }

        private void Preset_Click(object sender, RoutedEventArgs e)
        {
            System.Windows.Controls.Button btn = sender as System.Windows.Controls.Button;
            string presetName = btn != null && btn.Tag != null ? btn.Tag.ToString() : null;
            if (!string.IsNullOrEmpty(presetName))
                ApplyPreset(presetName);
        }

        private void ApplyPreset(string presetName)
        {
            if (presetName == "ZXG (Custom)")
                presetName = "ZXG";

            sbyte[] gains = Presets.GetCurve(presetName, _settings.SurroundEnabled);
            if (gains != null)
            {
                _isUpdatingUi = true;
                _settings.SelectedPreset = presetName;
                for (int i = 0; i < 10; i++)
                {
                    double prev = _sliders[i].Value;
                    _settings.EqBands[i] = gains[i];
                    AnimateSliderValue(_sliders[i], prev, gains[i]);
                    UpdateGainLabel(i, gains[i]);
                }
                _isUpdatingUi = false;

                HighlightPresetButton(presetName);
                _hardware.SetEqualizer(_settings.EqBands, presetName);
                _settings.Save();
            }
        }

        private void HighlightPresetButton(string selectedPreset)
        {
            System.Windows.Controls.Button[] buttons = new System.Windows.Controls.Button[] {
                BtnPresetZxg, BtnPresetFlat, BtnPresetBass, BtnPresetEksa,
                BtnPresetClassic, BtnPresetPop, BtnPresetJazz, BtnPresetRock
            };

            foreach (System.Windows.Controls.Button b in buttons)
            {
                if (b == null) continue;
                string tag = b.Tag != null ? b.Tag.ToString() : null;
                if (tag == selectedPreset || (selectedPreset == "ZXG (Custom)" && tag == "ZXG"))
                {
                    b.BorderBrush = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#A855F7"));
                    b.Foreground = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#C084FC"));
                    b.Background = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#25123E"));
                }
                else
                {
                    b.BorderBrush = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#232B3C"));
                    b.Foreground = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#94A3B8"));
                    b.Background = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#171D28"));
                }
            }
        }

        private void BtnResetEq_Click(object sender, RoutedEventArgs e)
        {
            ApplyPreset("Flat");
        }

        private void ChkAutoStart_Changed(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingUi) return;
            _settings.AutoStart = ChkAutoStart.IsChecked == true;
            _settings.Save();
        }

        private void ChkStartMinimized_Changed(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingUi) return;
            _settings.StartMinimized = ChkStartMinimized.IsChecked == true;
            _settings.Save();
        }

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
                this.DragMove();
        }

        private void BtnMinimize_Click(object sender, RoutedEventArgs e)
        {
            HideToTray();
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            HideToTray();
        }

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        private void PlayWindowEntranceAnimation()
        {
            if (RootBorder == null || RootScale == null) return;

            CubicEase easeOut = new CubicEase { EasingMode = EasingMode.EaseOut };
            Duration dur = new Duration(TimeSpan.FromMilliseconds(160));

            DoubleAnimation fadeAnim = new DoubleAnimation(0.0, 1.0, dur) { EasingFunction = easeOut, FillBehavior = FillBehavior.Stop };
            DoubleAnimation scaleXAnim = new DoubleAnimation(0.975, 1.0, dur) { EasingFunction = easeOut, FillBehavior = FillBehavior.Stop };
            DoubleAnimation scaleYAnim = new DoubleAnimation(0.975, 1.0, dur) { EasingFunction = easeOut, FillBehavior = FillBehavior.Stop };

            RootBorder.Opacity = 1.0;
            RootScale.ScaleX = 1.0;
            RootScale.ScaleY = 1.0;

            RootBorder.BeginAnimation(UIElement.OpacityProperty, fadeAnim);
            RootScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleXAnim);
            RootScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleYAnim);
        }

        public void ShowWindow()
        {
            App.Log("ShowWindow started");
            UpdateBatteryUi();
            if (!this.IsVisible)
                this.Show();

            if (this.WindowState == WindowState.Minimized)
                this.WindowState = WindowState.Normal;

            this.ShowInTaskbar = true;
            this.Topmost = true;
            this.Topmost = false;
            this.Focus();
            PlayWindowEntranceAnimation();

            try
            {
                var helper = new System.Windows.Interop.WindowInteropHelper(this);
                if (helper.Handle != IntPtr.Zero)
                    SetForegroundWindow(helper.Handle);
            }
            catch { }

            App.Log("ShowWindow finished");
        }

        public void HideToTray()
        {
            this.Hide();
            this.ShowInTaskbar = false;
        }

        public void ToggleWindow()
        {
            if (this.IsVisible && this.WindowState != WindowState.Minimized)
                HideToTray();
            else
                ShowWindow();
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (!_isRealExit)
            {
                e.Cancel = true;
                HideToTray();
            }
            else
            {
                base.OnClosing(e);
            }
        }

        public void ExitApplication()
        {
            _isRealExit = true;
            _settings.LastActiveTime = DateTime.Now;
            _settings.Save();
            if (_notifyIcon != null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
            }
            _hardware.Dispose();
            System.Windows.Application.Current.Shutdown();
        }
    }
}
